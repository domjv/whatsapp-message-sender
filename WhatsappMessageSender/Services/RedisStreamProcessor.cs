using WhatsappMessageSender.Models;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace WhatsappMessageSender.Services;

/// <summary>
/// Reads WhatsApp notification messages from Redis Streams using a consumer-group
/// pattern and processes them with at-least-once delivery guarantees.
///
/// Delivery model (RQ + Redis Streams hybrid):
///   - Redis Streams  : primary pipeline; new messages arrive here via XADD.
///   - Redis Sorted Set (":retries" suffix): RQ-style delayed retry scheduler.
///     Failed messages are placed here with a score equal to their next-retry
///     Unix timestamp; a background loop moves them back to the stream when due.
///
/// Flow:
///   1. XREADGROUP reads un-delivered messages from each configured stream.
///   2. On success  → XACK (message removed from the Pending-Entry-List).
///   3. On failure  → XACK + insert into sorted-set with exponential back-off.
///   4. Retry loop  → promotes due retries back to the stream (XADD + ZREM).
///   5. MaxRetries  → XACK + XADD to a dead-letter stream (":dead" suffix).
/// </summary>
public class RedisStreamProcessor : IMessageProcessor
{
    private const string DeadLetterSuffix = ":dead";
    private const string RetrySuffix = ":retries";

    private readonly AppSettings _appSettings;
    private readonly IWhatsAppService _whatsAppService;
    private readonly IBlobStorageService _blobStorageService;
    private readonly IMessageTrackingService _messageTrackingService;
    private readonly Dictionary<string, string> _streamContainerMapping;
    private readonly Dictionary<string, int> _streamPriorityMapping;
    private readonly IWhatsAppSendRateLimiter _whatsAppSendRateLimiter;
    private readonly SemaphoreSlim _globalProcessingSemaphore;
    // Global one-at-a-time guard for Selenium send calls.
    private readonly SemaphoreSlim _whatsAppSendSemaphore = new(1, 1);

    private IConnectionMultiplexer? _redis;
    // Exposed as internal so the test constructor can inject a mock IDatabase
    internal IDatabase? _db;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _runningTasks = [];

    // -------------------------------------------------------------------------
    // Production constructor (used by DI)
    // -------------------------------------------------------------------------

    public RedisStreamProcessor(
        IConfiguration configuration,
        IWhatsAppService whatsAppService,
        IBlobStorageService blobStorageService,
        IMessageTrackingService messageTrackingService,
        IWhatsAppSendRateLimiter whatsAppSendRateLimiter)
    {
        _appSettings = configuration.Get<AppSettings>()
            ?? throw new InvalidOperationException("Invalid configuration");

        if (_appSettings.Redis == null)
            throw new InvalidOperationException(
                "Redis configuration is missing. Set 'MessageBroker' to 'Redis' and provide a 'Redis' config section.");

        _whatsAppService = whatsAppService;
        _blobStorageService = blobStorageService;
        _messageTrackingService = messageTrackingService;
        _whatsAppSendRateLimiter = whatsAppSendRateLimiter;
        _streamContainerMapping = _appSettings.Redis.Streams
            .ToDictionary(s => s.StreamName, s => s.ContainerName);
        _streamPriorityMapping = _appSettings.Redis.Streams
            .ToDictionary(s => s.StreamName, s => s.Priority);
        _globalProcessingSemaphore = new SemaphoreSlim(
            Math.Max(1, _appSettings.Redis.MaxConcurrentCalls));
    }

    // -------------------------------------------------------------------------
    // Internal test constructor (allows injecting a mock IDatabase)
    // -------------------------------------------------------------------------

    internal RedisStreamProcessor(
        AppSettings appSettings,
        IWhatsAppService whatsAppService,
        IBlobStorageService blobStorageService,
        IMessageTrackingService messageTrackingService,
        IDatabase database,
        IWhatsAppSendRateLimiter? whatsAppSendRateLimiter = null)
    {
        _appSettings = appSettings;
        _whatsAppService = whatsAppService;
        _blobStorageService = blobStorageService;
        _messageTrackingService = messageTrackingService;
        _db = database;
        _whatsAppSendRateLimiter = whatsAppSendRateLimiter ?? NullWhatsAppSendRateLimiter.Instance;
        _streamContainerMapping = appSettings.Redis!.Streams
            .ToDictionary(s => s.StreamName, s => s.ContainerName);
        _streamPriorityMapping = appSettings.Redis.Streams
            .ToDictionary(s => s.StreamName, s => s.Priority);
        _globalProcessingSemaphore = new SemaphoreSlim(
            Math.Max(1, appSettings.Redis.MaxConcurrentCalls));
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public void StartProcessing()
    {
        _redis = ConnectionMultiplexer.Connect(_appSettings.Redis!.ConnectionString);
        _db = _redis.GetDatabase();

        var group = _appSettings.Redis.ConsumerGroup;
        var consumer = _appSettings.Redis.ConsumerName;

        foreach (var streamConfig in _appSettings.Redis.Streams)
        {
            EnsureConsumerGroupAsync(streamConfig.StreamName, group)
                .GetAwaiter().GetResult();

            var token = _cts.Token;
            var streamName = streamConfig.StreamName;

            _runningTasks.Add(Task.Run(
                () => ProcessStreamLoopAsync(streamName, group, consumer, token), token));
            _runningTasks.Add(Task.Run(
                () => RetrySchedulerLoopAsync(streamName, token), token));
            _runningTasks.Add(Task.Run(
                () => ReclaimPendingLoopAsync(streamName, group, consumer, token), token));

            Console.WriteLine($"Started processing stream: {streamName}");
        }

        Console.WriteLine(
            "Redis consumer is running. You may see no further output until stream entries arrive — press Ctrl+C to stop.");
    }

    public async Task CloseAsync()
    {
        _cts.Cancel();
        try
        {
            await Task.WhenAll(_runningTasks);
        }
        catch (OperationCanceledException) { }

        _redis?.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _redis?.Dispose();
        _globalProcessingSemaphore.Dispose();
        _whatsAppSendSemaphore.Dispose();
    }

    // -------------------------------------------------------------------------
    // Consumer-group setup
    // -------------------------------------------------------------------------

    private async Task EnsureConsumerGroupAsync(string streamName, string group)
    {
        try
        {
            await _db!.StreamCreateConsumerGroupAsync(
                streamName, group, StreamPosition.Beginning, createStream: true);
            Console.WriteLine($"Consumer group '{group}' created for stream '{streamName}'");
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            Console.WriteLine($"Consumer group '{group}' already exists for stream '{streamName}'");
        }
    }

    // -------------------------------------------------------------------------
    // Main read loop
    // -------------------------------------------------------------------------

    private async Task ProcessStreamLoopAsync(
        string streamName, string group, string consumer, CancellationToken token)
    {
        var maxConcurrent = _appSettings.Redis!.MaxConcurrentCalls;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var entries = await _db!.StreamReadGroupAsync(
                    streamName, group, consumer, ">", count: maxConcurrent);

                if (entries == null || entries.Length == 0)
                {
                    await Task.Delay(500, token);
                    continue;
                }

                await Task.WhenAll(entries.Select(
                    e => ProcessEntryWithConcurrencyLimitAsync(streamName, group, e, token)));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading stream '{streamName}': {ex.Message}");
                await Task.Delay(5000, token);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Pending message reclaim loop (XAUTOCLAIM)
    // -------------------------------------------------------------------------

    private async Task ReclaimPendingLoopAsync(
        string streamName, string group, string consumer, CancellationToken token)
    {
        var minIdleMs = Math.Max(1000, _appSettings.Redis!.PendingMessageTimeoutSeconds * 1000);
        var claimCount = Math.Max(1, _appSettings.Redis.MaxConcurrentCalls);

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10_000, token);

                var result = await _db!.ExecuteAsync(
                    "XAUTOCLAIM",
                    streamName,
                    group,
                    consumer,
                    minIdleMs,
                    "0-0",
                    "COUNT",
                    claimCount);

                if (result.IsNull)
                {
                    continue;
                }

                var topLevel = (RedisResult[])result!;
                if (topLevel.Length < 2 || topLevel[1].IsNull)
                {
                    continue;
                }

                var claimedMessages = (RedisResult[])topLevel[1]!;
                foreach (var claimedMessage in claimedMessages)
                {
                    if (claimedMessage.IsNull)
                    {
                        continue;
                    }

                    var parsed = TryParseClaimedStreamEntry(claimedMessage, out var reclaimedEntry);
                    if (!parsed || reclaimedEntry == null)
                    {
                        continue;
                    }

                    var entry = reclaimedEntry.Value;
                    Console.WriteLine(
                        $"Reclaimed pending message {entry.Id} from stream '{streamName}'.");
                    await ProcessEntryWithConcurrencyLimitAsync(
                        streamName, group, entry, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reclaiming pending messages for '{streamName}': {ex.Message}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Retry scheduler loop (RQ-style delayed jobs via Redis Sorted Set)
    // -------------------------------------------------------------------------

    private async Task RetrySchedulerLoopAsync(string streamName, CancellationToken token)
    {
        var retryKey = streamName + RetrySuffix;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10_000, token);

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var dueEntries = await _db!.SortedSetRangeByScoreWithScoresAsync(
                    retryKey, start: 0, stop: now);

                foreach (var entry in dueEntries)
                {
                    var json = entry.Element.ToString();
                    RetryEntry? retry = null;
                    try
                    {
                        retry = JsonConvert.DeserializeObject<RetryEntry>(json);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to deserialize retry entry: {ex.Message}");
                        await _db.SortedSetRemoveAsync(retryKey, json);
                        continue;
                    }

                    if (retry == null)
                    {
                        await _db.SortedSetRemoveAsync(retryKey, json);
                        continue;
                    }

                    var fields = retry.OriginalData
                        .Select(kv => new NameValueEntry(kv.Key, kv.Value))
                        .Append(new NameValueEntry("retry_count", retry.RetryCount.ToString()))
                        .ToArray();

                    await _db.StreamAddAsync(streamName, fields);
                    await _db.SortedSetRemoveAsync(retryKey, json);
                    Console.WriteLine(
                        $"Re-queued retry #{retry.RetryCount} from '{retryKey}' to '{streamName}'");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in retry scheduler for '{streamName}': {ex.Message}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Message handling (internal so unit tests can invoke directly)
    // -------------------------------------------------------------------------

    internal async Task HandleMessageAsync(
        string streamName, string group, StreamEntry entry, CancellationToken cancellationToken = default)
    {
        var messageId = entry.Id.ToString();
        var (messageType, messageName, resolvedChannelName, whatsAppMessage, retryCount) =
            ExtractMessageData(entry, streamName);

        if (string.IsNullOrEmpty(messageName))
        {
            Console.WriteLine($"Message {messageId} is missing MessageName. Moving to dead letter.");
            await DeadLetterMessageAsync(streamName, group, entry, "MessageName is required");
            return;
        }

        var backendMessageId = messageName;
        var dispatchPriority = _streamPriorityMapping.GetValueOrDefault(streamName, 100);

        var messageProperties = new MessageProperties
        {
            MessageType = messageType,
            ChannelName = resolvedChannelName,
            MessageName = messageName
        };

        Console.WriteLine($"Processing message: {messageId} (Attempt {retryCount + 1})");

        if (retryCount > RetrySettings.MaxRetries)
        {
            Console.WriteLine($"Message {messageId} exceeded maximum retries. Moving to dead letter.");
            await DeadLetterMessageAsync(streamName, group, entry,
                $"Message failed after {RetrySettings.MaxRetries} attempts");
            await _messageTrackingService.TrackMessageStatusAsync(
                backendMessageId,
                "Failed",
                $"Message failed after {RetrySettings.MaxRetries} attempts",
                null,
                null);
            return;
        }

        try
        {
            if (!messageProperties.MessageType.Equals("whatsapp", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Message {messageId} has unsupported type '{messageProperties.MessageType}'. Moving to dead letter.");
                await DeadLetterMessageAsync(streamName, group, entry,
                    $"Unsupported message type: {messageProperties.MessageType}");
                return;
            }

            if (whatsAppMessage == null)
            {
                await _messageTrackingService.TrackMessageStatusAsync(
                    backendMessageId, "Failed", "Invalid message format", null, null);
                await DeadLetterMessageAsync(streamName, group, entry,
                    "Message could not be deserialized");
                return;
            }

            backendMessageId = whatsAppMessage.MessageId ?? messageName;

            string? filePath = null;
            if (!string.IsNullOrEmpty(whatsAppMessage.AttachmentUrl) &&
                _streamContainerMapping.TryGetValue(messageProperties.ChannelName, out var containerName))
            {
                filePath = await _blobStorageService.DownloadFileAsync(
                    whatsAppMessage.AttachmentUrl, whatsAppMessage.Name, containerName);
            }

            SendMessageResult sendResult;
            await _whatsAppSendSemaphore.WaitAsync(cancellationToken);
            try
            {
                await _whatsAppSendRateLimiter.WaitForSendSlotAsync(dispatchPriority, cancellationToken);
                sendResult = await _whatsAppService.SendMessageAsync(
                    whatsAppMessage.Phone, whatsAppMessage.Message, filePath);
                if (sendResult.Success)
                {
                    _whatsAppSendRateLimiter.NotifySuccessfulSendIfThrottled(dispatchPriority);
                }
            }
            finally
            {
                _whatsAppSendSemaphore.Release();
            }

            if (sendResult.Success)
            {
                var deliveredAt = DateTime.UtcNow;
                await _messageTrackingService.TrackMessageStatusAsync(
                    backendMessageId,
                    "Sent",
                    null,
                    sendResult.ProviderMessageId,
                    deliveredAt);
                await _db!.StreamAcknowledgeAsync(streamName, group, entry.Id);
                Console.WriteLine($"Message {messageId} sent successfully and acknowledged.");
            }
            else
            {
                await ScheduleRetryAsync(streamName, group, entry, retryCount, sendResult.Error);
                var delay = RetrySettings.GetDelayForRetry(retryCount + 1);
                await _messageTrackingService.TrackMessageStatusAsync(
                    backendMessageId,
                    "Pending",
                    $"Will retry in {delay.TotalSeconds} seconds. Error: {sendResult.Error}",
                    sendResult.ProviderMessageId,
                    null);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing message {messageId}: {ex.Message}");
            await ScheduleRetryAsync(streamName, group, entry, retryCount, ex.Message);
            var delay = RetrySettings.GetDelayForRetry(retryCount + 1);
            await _messageTrackingService.TrackMessageStatusAsync(
                backendMessageId,
                "Pending",
                $"Will retry in {delay.TotalSeconds} seconds. Error: {ex.Message}",
                null,
                null);
        }
    }

    private async Task ProcessEntryWithConcurrencyLimitAsync(
        string streamName, string group, StreamEntry entry, CancellationToken token)
    {
        await _globalProcessingSemaphore.WaitAsync(token);
        try
        {
            await HandleMessageAsync(streamName, group, entry, token);
        }
        finally
        {
            _globalProcessingSemaphore.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Retry scheduling (XACK + ZADD) — internal for testability
    // -------------------------------------------------------------------------

    internal async Task ScheduleRetryAsync(
        string streamName, string group, StreamEntry entry, int currentRetryCount, string? error)
    {
        var nextRetry = currentRetryCount + 1;
        var delay = RetrySettings.GetDelayForRetry(nextRetry);
        var retryAfter = DateTimeOffset.UtcNow.Add(delay).ToUnixTimeSeconds();

        var originalData = entry.Values
            .ToDictionary(v => v.Name.ToString(), v => v.Value.ToString()!);

        originalData.Remove("retry_count");

        var retryEntry = new RetryEntry
        {
            RetryCount = nextRetry,
            StreamName = streamName,
            LastError = error ?? "Unknown error",
            OriginalData = originalData
        };

        var retryKey = streamName + RetrySuffix;
        await _db!.SortedSetAddAsync(retryKey, JsonConvert.SerializeObject(retryEntry), retryAfter);
        await _db.StreamAcknowledgeAsync(streamName, group, entry.Id);

        Console.WriteLine(
            $"Message {entry.Id} scheduled for retry #{nextRetry} " +
            $"in {delay.TotalSeconds} s (error: {error})");
    }

    // -------------------------------------------------------------------------
    // Dead-letter — internal for testability
    // -------------------------------------------------------------------------

    internal async Task DeadLetterMessageAsync(
        string streamName, string group, StreamEntry entry, string reason)
    {
        var deadLetterStream = streamName + DeadLetterSuffix;
        var fields = entry.Values
            .Select(v => new NameValueEntry(v.Name, v.Value))
            .Concat([
                new NameValueEntry("dead_letter_reason", reason),
                new NameValueEntry("original_id", entry.Id.ToString()),
                new NameValueEntry("dead_lettered_at", DateTime.UtcNow.ToString("o"))
            ])
            .ToArray();

        await _db!.StreamAddAsync(deadLetterStream, fields);
        await _db.StreamAcknowledgeAsync(streamName, group, entry.Id);
        Console.WriteLine($"Message {entry.Id} moved to dead-letter stream: {deadLetterStream}");
    }

    // -------------------------------------------------------------------------
    // Message extraction — internal for testability
    // -------------------------------------------------------------------------

    internal static (string messageType, string messageName, string channelName,
        WhatsAppMessage? msg, int retryCount) ExtractMessageData(
        StreamEntry entry, string defaultChannelName)
    {
        WhatsAppMessage? msg = null;

        // Prefer a single 'data' JSON field (as typically produced by Frappe/Python)
        var dataField = entry["data"];
        if (!dataField.IsNull)
        {
            try
            {
                msg = JsonConvert.DeserializeObject<WhatsAppMessage>(dataField!);
            }
            catch
            {
                // Fall through to individual-field extraction
            }
        }

        // Fall back to individual fields (phone, message, name, ...)
        if (msg == null)
        {
            var phone = (string?)entry["phone"];
            var message = (string?)entry["message"];
            var name = (string?)entry["name"];
            var attachmentUrl = (string?)entry["attachment_url"];
            var msgName = (string?)entry["message_name"];
            var messageId = (string?)entry["message_id"];

            if (!string.IsNullOrEmpty(phone) && !string.IsNullOrEmpty(message))
            {
                msg = new WhatsAppMessage
                {
                    Phone = phone,
                    Message = message,
                    Name = name ?? msgName ?? "unknown",
                    AttachmentUrl = attachmentUrl,
                    MessageId = messageId,
                    MessageName = msgName
                };
            }
        }

        var messageType = (string?)entry["message_type"] ?? "whatsapp";
        var messageName = (string?)entry["message_id"]
            ?? msg?.MessageId
            ?? (string?)entry["message_name"]
            ?? msg?.MessageName
            ?? string.Empty;
        var channelName = (string?)entry["stream_name"] ?? defaultChannelName;

        int.TryParse((string?)entry["retry_count"], out var retryCount);

        return (messageType, messageName, channelName, msg, retryCount);
    }

    internal static bool TryParseClaimedStreamEntry(
        RedisResult rawClaimedMessage,
        out StreamEntry? streamEntry)
    {
        streamEntry = null;
        try
        {
            if (rawClaimedMessage.IsNull)
            {
                return false;
            }

            var messageParts = (RedisResult[])rawClaimedMessage!;
            if (messageParts.Length < 2 || messageParts[0].IsNull || messageParts[1].IsNull)
            {
                return false;
            }

            var messageId = messageParts[0].ToString();
            var rawFields = (RedisResult[])messageParts[1]!;
            return TryParseClaimedFieldArray(messageId, rawFields, out streamEntry);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses XAUTOCLAIM-style field list (alternating key/value <see cref="RedisResult"/>s)
    /// into a <see cref="StreamEntry"/>.
    /// </summary>
    internal static bool TryParseClaimedFieldArray(
        string messageId,
        RedisResult[] rawFields,
        out StreamEntry? streamEntry)
    {
        streamEntry = null;
        try
        {
            if (string.IsNullOrEmpty(messageId) || rawFields.Length == 0)
            {
                return false;
            }

            var fields = new List<NameValueEntry>(rawFields.Length / 2);

            for (var i = 0; i + 1 < rawFields.Length; i += 2)
            {
                if (rawFields[i].IsNull || rawFields[i + 1].IsNull)
                {
                    continue;
                }

                fields.Add(new NameValueEntry(
                    rawFields[i].ToString(),
                    rawFields[i + 1].ToString()));
            }

            streamEntry = new StreamEntry(messageId, [.. fields]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Internal types
    // -------------------------------------------------------------------------

    internal sealed class RetryEntry
    {
        public int RetryCount { get; set; }
        public string StreamName { get; set; } = string.Empty;
        public string LastError { get; set; } = string.Empty;
        public Dictionary<string, string> OriginalData { get; set; } = [];
    }
}
