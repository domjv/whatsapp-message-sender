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
public class RedisStreamProcessor : IDisposable
{
    private const string DeadLetterSuffix = ":dead";
    private const string RetrySuffix = ":retries";

    private readonly AppSettings _appSettings;
    private readonly WhatsAppService _whatsAppService;
    private readonly BlobStorageService _blobStorageService;
    private readonly Dictionary<string, string> _streamContainerMapping;

    private IConnectionMultiplexer? _redis;
    private IDatabase? _db;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _runningTasks = [];

    public RedisStreamProcessor(
        IConfiguration configuration,
        WhatsAppService whatsAppService,
        BlobStorageService blobStorageService)
    {
        _appSettings = configuration.Get<AppSettings>()
            ?? throw new InvalidOperationException("Invalid configuration");
        _whatsAppService = whatsAppService;
        _blobStorageService = blobStorageService;
        _streamContainerMapping = _appSettings.Redis.Streams
            .ToDictionary(s => s.StreamName, s => s.ContainerName);
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public void StartProcessing()
    {
        _redis = ConnectionMultiplexer.Connect(_appSettings.Redis.ConnectionString);
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

            Console.WriteLine($"Started processing stream: {streamName}");
        }
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
        var maxConcurrent = _appSettings.Redis.MaxConcurrentCalls;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Read only new (un-delivered) messages with ">" position
                var entries = await _db!.StreamReadGroupAsync(
                    streamName, group, consumer, ">", count: maxConcurrent);

                if (entries == null || entries.Length == 0)
                {
                    await Task.Delay(500, token);
                    continue;
                }

                await Task.WhenAll(entries.Select(
                    e => HandleMessageAsync(streamName, group, e)));
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
    // Retry scheduler loop (RQ-style delayed jobs via Redis Sorted Set)
    // -------------------------------------------------------------------------

    private async Task RetrySchedulerLoopAsync(string streamName, CancellationToken token)
    {
        var retryKey = streamName + RetrySuffix;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10_000, token); // check every 10 s

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Fetch all retries whose score (retry-after timestamp) is due
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

                    // Promote back to the stream with the updated retry_count
                    var fields = retry.OriginalData
                        .Select(kv => new NameValueEntry(kv.Key, kv.Value))
                        .Append(new NameValueEntry("retry_count", retry.RetryCount.ToString()))
                        .ToArray();

                    await _db.StreamAddAsync(streamName, fields);
                    await _db.SortedSetRemoveAsync(retryKey, json);
                    Console.WriteLine(
                        $"Re-queued retry #{retry.RetryCount} from '{retryKey}' → '{streamName}'");
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
    // Message handling
    // -------------------------------------------------------------------------

    private async Task HandleMessageAsync(
        string streamName, string group, StreamEntry entry)
    {
        var messageId = entry.Id.ToString();
        var (messageType, messageName, resolvedStreamName, whatsAppMessage, retryCount) =
            ExtractMessageData(entry, streamName);

        if (string.IsNullOrEmpty(messageName))
        {
            Console.WriteLine($"Message {messageId} is missing MessageName. Moving to dead letter.");
            await DeadLetterMessageAsync(streamName, group, entry, "MessageName is required");
            return;
        }

        var messageProperties = new MessageProperties
        {
            MessageType = messageType,
            StreamName = resolvedStreamName,
            MessageName = messageName
        };

        Console.WriteLine($"Processing message: {messageId} (Attempt {retryCount + 1})");

        if (retryCount > RetrySettings.MaxRetries)
        {
            Console.WriteLine($"Message {messageId} exceeded maximum retries. Moving to dead letter.");
            await DeadLetterMessageAsync(streamName, group, entry,
                $"Message failed after {RetrySettings.MaxRetries} attempts");
            await MessageTrackingService.TrackMessageStatusAsync(
                messageProperties.MessageName, "Failed",
                $"Message failed after {RetrySettings.MaxRetries} attempts");
            return;
        }

        try
        {
            await MessageTrackingService.TrackMessageStatusAsync(
                messageProperties.MessageName, "Processing");

            if (!messageProperties.MessageType.Equals("whatsapp", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Message {messageId} has unsupported type '{messageProperties.MessageType}'. Moving to dead letter.");
                await DeadLetterMessageAsync(streamName, group, entry,
                    $"Unsupported message type: {messageProperties.MessageType}");
                return;
            }

            if (whatsAppMessage == null)
            {
                await MessageTrackingService.TrackMessageStatusAsync(
                    messageProperties.MessageName, "Failed", "Invalid message format");
                await DeadLetterMessageAsync(streamName, group, entry,
                    "Message could not be deserialized");
                return;
            }

            string? filePath = null;
            if (!string.IsNullOrEmpty(whatsAppMessage.AttachmentUrl) &&
                _streamContainerMapping.TryGetValue(messageProperties.StreamName, out var containerName))
            {
                filePath = await _blobStorageService.DownloadFileAsync(
                    whatsAppMessage.AttachmentUrl, whatsAppMessage.Name, containerName);
            }

            var sendResult = await _whatsAppService.SendMessageAsync(
                whatsAppMessage.Phone, whatsAppMessage.Message, filePath);

            if (sendResult.Success)
            {
                await MessageTrackingService.TrackMessageStatusAsync(
                    messageProperties.MessageName, "Delivered");
                await _db!.StreamAcknowledgeAsync(streamName, group, entry.Id);
                Console.WriteLine($"Message {messageId} sent successfully and acknowledged.");
            }
            else
            {
                await ScheduleRetryAsync(streamName, group, entry, retryCount, sendResult.Error);
                var delay = RetrySettings.GetDelayForRetry(retryCount + 1);
                await MessageTrackingService.TrackMessageStatusAsync(
                    messageProperties.MessageName, "Retry Scheduled",
                    $"Will retry in {delay.TotalSeconds} seconds. Error: {sendResult.Error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing message {messageId}: {ex.Message}");
            await ScheduleRetryAsync(streamName, group, entry, retryCount, ex.Message);
            var delay = RetrySettings.GetDelayForRetry(retryCount + 1);
            await MessageTrackingService.TrackMessageStatusAsync(
                messageName, "Retry Scheduled",
                $"Will retry in {delay.TotalSeconds} seconds. Error: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Retry scheduling (XACK + ZADD)
    // -------------------------------------------------------------------------

    private async Task ScheduleRetryAsync(
        string streamName, string group, StreamEntry entry, int currentRetryCount, string? error)
    {
        var nextRetry = currentRetryCount + 1;
        var delay = RetrySettings.GetDelayForRetry(nextRetry);
        var retryAfter = DateTimeOffset.UtcNow.Add(delay).ToUnixTimeSeconds();

        var originalData = entry.Values
            .ToDictionary(v => v.Name.ToString(), v => v.Value.ToString()!);

        // Strip the old retry_count so the scheduler always uses its own value
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
    // Dead-letter
    // -------------------------------------------------------------------------

    private async Task DeadLetterMessageAsync(
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
    // Message extraction (supports JSON 'data' field or individual fields)
    // -------------------------------------------------------------------------

    private static (string messageType, string messageName, string streamName,
        WhatsAppMessage? msg, int retryCount) ExtractMessageData(
        StreamEntry entry, string defaultStreamName)
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

        // Fall back to individual fields (phone, message, name, …)
        if (msg == null)
        {
            var phone = (string?)entry["phone"];
            var message = (string?)entry["message"];
            var name = (string?)entry["name"];
            var attachmentUrl = (string?)entry["attachment_url"];
            var msgName = (string?)entry["message_name"];

            if (!string.IsNullOrEmpty(phone) && !string.IsNullOrEmpty(message))
            {
                msg = new WhatsAppMessage
                {
                    Phone = phone,
                    Message = message,
                    Name = name ?? msgName ?? "unknown",
                    AttachmentUrl = attachmentUrl,
                    MessageName = msgName
                };
            }
        }

        var messageType = (string?)entry["message_type"] ?? "whatsapp";
        var messageName = (string?)entry["message_name"] ?? msg?.MessageName ?? msg?.Name ?? string.Empty;
        var streamName = (string?)entry["stream_name"] ?? defaultStreamName;

        int.TryParse((string?)entry["retry_count"], out var retryCount);

        return (messageType, messageName, streamName, msg, retryCount);
    }

    // -------------------------------------------------------------------------
    // Internal types
    // -------------------------------------------------------------------------

    private sealed class RetryEntry
    {
        public int RetryCount { get; set; }
        public string StreamName { get; set; } = string.Empty;
        public string LastError { get; set; } = string.Empty;
        public Dictionary<string, string> OriginalData { get; set; } = [];
    }
}
