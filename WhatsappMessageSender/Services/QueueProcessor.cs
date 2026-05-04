using WhatsappMessageSender.Models;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System.Text;
using System.Collections.Concurrent;

namespace WhatsappMessageSender.Services;

/// <summary>
/// Reads WhatsApp notification messages from Azure Service Bus topic subscriptions and
/// processes them with at-least-once delivery (lock-based retry).
///
/// Flow:
///   1. For each topic, either RegisterMessageHandler (non-session subscription) or
///      RegisterSessionHandler (when <see cref="TopicSubscriptionConfig.RequiresSession"/> is true).
///   2. Messages are queued to an in-memory priority dispatcher (auth first).
///   2. On success  → CompleteAsync (removes message from the queue).
///   3. On failure  → AbandonAsync  (Service Bus releases the lock so the
///      message becomes visible again after the visibility timeout, up to
///      the subscription's MaxDeliveryCount).
///   4. MaxRetries exceeded → DeadLetterAsync.
/// </summary>
public class QueueProcessor : IMessageProcessor
{
    private readonly AppSettings _appSettings;
    private readonly IWhatsAppService _whatsAppService;
    private readonly IBlobStorageService _blobStorageService;
    private readonly IMessageTrackingService _messageTrackingService;
    private readonly ConcurrentDictionary<string, ISubscriptionClient> _subscriptionClients;
    private readonly Dictionary<string, string> _topicContainerMapping;
    private readonly Dictionary<string, int> _topicPriorityMapping;
    private readonly PriorityQueue<PendingMessage, (int Priority, long Sequence)> _pendingMessages = new();
    private readonly SemaphoreSlim _pendingSignal = new(0);
    private readonly object _pendingLock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _dispatcherTask;
    private long _enqueueSequence;

    // Selenium/WhatsApp Web driver is single-session and not thread-safe.
    // Serialize sends even when Service Bus dispatches callbacks concurrently.
    private readonly SemaphoreSlim _whatsAppSendSemaphore = new(1, 1);

    public QueueProcessor(
        IConfiguration configuration,
        IWhatsAppService whatsAppService,
        IBlobStorageService blobStorageService,
        IMessageTrackingService messageTrackingService)
    {
        _appSettings = configuration.Get<AppSettings>()
            ?? throw new InvalidOperationException("Invalid configuration");

        if (_appSettings.ServiceBus == null)
            throw new InvalidOperationException(
                "ServiceBus configuration is missing. Set 'MessageBroker' to 'ServiceBus' and provide a 'ServiceBus' config section.");

        _whatsAppService = whatsAppService;
        _blobStorageService = blobStorageService;
        _messageTrackingService = messageTrackingService;
        _subscriptionClients = new ConcurrentDictionary<string, ISubscriptionClient>();
        _topicContainerMapping = _appSettings.ServiceBus.Topics
            .GroupBy(t => t.TopicName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ContainerName, StringComparer.OrdinalIgnoreCase);
        _topicPriorityMapping = _appSettings.ServiceBus.Topics
            .GroupBy(t => t.TopicName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Priority, StringComparer.OrdinalIgnoreCase);
    }

    public void StartProcessing()
    {
        var connectionString = _appSettings.ServiceBus!.ConnectionString;
        var maxConcurrent = _appSettings.ServiceBus.MaxConcurrentCalls;
        var maxAutoRenew = TimeSpan.FromMinutes(
            Math.Max(1, _appSettings.ServiceBus.MaxAutoRenewDurationMinutes));

        _dispatcherTask = Task.Run(() => DispatchMessagesLoopAsync(_cts.Token), _cts.Token);
        _ = _dispatcherTask.ContinueWith(
            t =>
            {
                if (!t.IsFaulted || t.Exception == null)
                {
                    return;
                }

                foreach (var ex in t.Exception.InnerExceptions)
                {
                    Console.WriteLine($"[fatal] Service Bus message dispatcher stopped: {ex}");
                }
            },
            TaskContinuationOptions.ExecuteSynchronously);

        foreach (var topicConfig in _appSettings.ServiceBus.Topics)
        {
            var subscriptionClient = new SubscriptionClient(
                connectionString,
                topicConfig.TopicName,
                topicConfig.SubscriptionName);
            var clientKey = GetSubscriptionClientKey(topicConfig.TopicName, topicConfig.SubscriptionName);
            _subscriptionClients.TryAdd(clientKey, subscriptionClient);
            subscriptionClient.PrefetchCount = 0;

            if (topicConfig.RequiresSession)
            {
                var sessionHandlerOptions = new SessionHandlerOptions(ExceptionReceivedHandler)
                {
                    MaxConcurrentSessions = maxConcurrent,
                    MessageWaitTimeout = TimeSpan.FromMinutes(1),
                    MaxAutoRenewDuration = maxAutoRenew,
                    AutoComplete = false
                };

                subscriptionClient.RegisterSessionHandler(
                    (session, message, token) =>
                        ProcessMessagesAsync(session, message, token, topicConfig.TopicName, topicConfig.SubscriptionName),
                    sessionHandlerOptions);

                Console.WriteLine(
                    $"Started processing topic/subscription (sessions): {topicConfig.TopicName}/{topicConfig.SubscriptionName}");
            }
            else
            {
                var messageHandlerOptions = new MessageHandlerOptions(ExceptionReceivedHandler)
                {
                    MaxConcurrentCalls = maxConcurrent,
                    MaxAutoRenewDuration = maxAutoRenew,
                    AutoComplete = false
                };

                subscriptionClient.RegisterMessageHandler(
                    (message, token) =>
                        ProcessMessagesAsync(subscriptionClient, message, token, topicConfig.TopicName, topicConfig.SubscriptionName),
                    messageHandlerOptions);

                Console.WriteLine(
                    $"Started processing topic/subscription: {topicConfig.TopicName}/{topicConfig.SubscriptionName}");
            }
        }

        if (_appSettings.ServiceBus.Topics.Count == 0)
        {
            Console.WriteLine("Warning: ServiceBus:Topics is empty — no subscriptions will receive messages.");
        }
    }

    public async Task CloseAsync()
    {
        _cts.Cancel();
        _pendingSignal.Release();

        if (_dispatcherTask != null)
        {
            try
            {
                await _dispatcherTask;
            }
            catch (OperationCanceledException) { }
        }

        foreach (var subscriptionClient in _subscriptionClients.Values)
        {
            await subscriptionClient.CloseAsync();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pendingSignal.Release();
        _pendingSignal.Dispose();
        _cts.Dispose();

        foreach (var subscriptionClient in _subscriptionClients.Values)
        {
            subscriptionClient.CloseAsync().Wait();
        }
    }

    // -------------------------------------------------------------------------
    // ServiceBus callback — extracts data then delegates to the core method
    // -------------------------------------------------------------------------

    private async Task ProcessMessagesAsync(
        IReceiverClient receiverClient,
        Message message,
        CancellationToken token,
        string configuredTopicName,
        string subscriptionName)
    {
        var clientKey = GetSubscriptionClientKey(configuredTopicName, subscriptionName);
        if (!_subscriptionClients.TryGetValue(clientKey, out _))
        {
            Console.WriteLine($"No subscription client found for: {configuredTopicName}/{subscriptionName}");
            return;
        }

        var resolvedTopicName = message.UserProperties.TryGetValue("TopicName", out var topicNameObj)
            ? topicNameObj?.ToString() ?? configuredTopicName
            : configuredTopicName;

        var priority = ResolvePriority(resolvedTopicName);
        var pendingMessage = new PendingMessage(
            resolvedTopicName,
            message,
            receiverClient,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        lock (_pendingLock)
        {
            _pendingMessages.Enqueue(
                pendingMessage,
                (priority, Interlocked.Increment(ref _enqueueSequence)));
        }

        _pendingSignal.Release();

        using var registration = token.Register(
            () => pendingMessage.Completion.TrySetCanceled(token));
        await pendingMessage.Completion.Task;
    }

    private async Task DispatchMessagesLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _pendingSignal.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            PendingMessage? pending = null;
            lock (_pendingLock)
            {
                if (_pendingMessages.Count > 0)
                {
                    pending = _pendingMessages.Dequeue();
                }
            }

            if (pending == null)
            {
                continue;
            }

            try
            {
                await ProcessPendingMessageAsync(pending);
                pending.Completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                pending.Completion.TrySetException(ex);
            }
        }
    }

    private async Task ProcessPendingMessageAsync(PendingMessage pending)
    {
        var message = pending.Message;
        var messageId = message.MessageId;
        var messageBody = Encoding.UTF8.GetString(message.Body);
        var deliveryCount = message.SystemProperties.DeliveryCount;

        var messageType = message.UserProperties.TryGetValue("MessageType", out var messageTypeObj)
            ? messageTypeObj?.ToString() ?? "whatsapp"
            : "whatsapp";

        var messageName = message.UserProperties.TryGetValue("MessageName", out var messageNameObj)
            ? messageNameObj?.ToString()
            : null;

        await ProcessMessageCoreAsync(
            messageId: messageId,
            messageBody: messageBody,
            deliveryCount: deliveryCount,
            queueName: pending.TopicName,
            messageType: messageType,
            messageName: messageName,
            completeAsync: () => SafeCompleteAsync(pending.ReceiverClient, message),
            deadLetterAsync: (reason, desc) => SafeDeadLetterAsync(pending.ReceiverClient, message, reason, desc),
            abandonAsync: (props) => SafeAbandonAsync(pending.ReceiverClient, message, props));
    }

    internal async Task ProcessMessageCoreAsync(
        string messageId,
        string messageBody,
        int deliveryCount,
        string queueName,
        string messageType,
        string? messageName,
        Func<Task> completeAsync,
        Func<string, string, Task> deadLetterAsync,
        Func<IDictionary<string, object>, Task> abandonAsync)
    {
        var parsedPayload = ParseMessagePayload(messageBody);
        messageType = ResolveMessageType(messageType, parsedPayload);

        WhatsAppMessage? bodyForName = parsedPayload.WhatsAppMessage;
        if (bodyForName == null)
        {
            try
            {
                bodyForName = JsonConvert.DeserializeObject<WhatsAppMessage>(messageBody);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                bodyForName = null;
            }
        }

        messageName = ResolveMessageName(messageName, parsedPayload, bodyForName);

        if (string.IsNullOrEmpty(messageName))
        {
            Console.WriteLine($"Message {messageId} is missing MessageName. Moving to dead letter.");
            await deadLetterAsync("Message Name Not Found", "MessageName is required to send the message");
            return;
        }

        var messageProperties = new MessageProperties
        {
            MessageType = messageType,
            ChannelName = queueName,
            MessageName = messageName
        };

        Console.WriteLine($"Processing message: {messageId} (Attempt {deliveryCount})");

        if (deliveryCount > RetrySettings.MaxRetries)
        {
            Console.WriteLine($"Message {messageId} exceeded maximum retries. Moving to dead letter.");
            await deadLetterAsync(
                "MaxRetriesExceeded",
                $"Message failed after {RetrySettings.MaxRetries} attempts");
            await _messageTrackingService.TrackMessageStatusAsync(
                messageProperties.MessageName, "Failed",
                $"Message failed after {RetrySettings.MaxRetries} attempts");
            return;
        }

        try
        {
            if (!messageProperties.MessageType.Equals("whatsapp", StringComparison.OrdinalIgnoreCase))
            {
                await _messageTrackingService.TrackMessageStatusAsync(
                    messageProperties.MessageName, "Failed",
                    $"Unsupported message type: {messageProperties.MessageType}");
                await deadLetterAsync(
                    "UnsupportedMessageType",
                    $"Unsupported message type: {messageProperties.MessageType}");
                return;
            }

            WhatsAppMessage? msg = parsedPayload.WhatsAppMessage ?? bodyForName;

            if (msg == null)
            {
                await _messageTrackingService.TrackMessageStatusAsync(
                    messageProperties.MessageName, "Failed", "Invalid message format");
                await deadLetterAsync("InvalidFormat", "Message could not be deserialized");
                return;
            }

            string? filePath = null;
            if (!string.IsNullOrEmpty(msg.AttachmentUrl) &&
                _topicContainerMapping.TryGetValue(messageProperties.ChannelName, out var containerName))
            {
                filePath = await _blobStorageService.DownloadFileAsync(
                    msg.AttachmentUrl, msg.Name, containerName);
            }

            SendMessageResult sendResult;
            await _whatsAppSendSemaphore.WaitAsync();
            try
            {
                sendResult = await _whatsAppService.SendMessageAsync(
                    msg.Phone, msg.Message, filePath);
            }
            finally
            {
                _whatsAppSendSemaphore.Release();
            }

            if (sendResult.Success)
            {
                await _messageTrackingService.TrackMessageStatusAsync(
                    messageProperties.MessageName, "Sent");
                await completeAsync();
                Console.WriteLine($"Message {messageId} sent via topic: {queueName}");
            }
            else
            {
                await _messageTrackingService.TrackMessageStatusAsync(
                    messageProperties.MessageName, "Pending",
                    $"Will be retried by Service Bus delivery policy. Error: {sendResult.Error}");
                await abandonAsync(new Dictionary<string, object>
                {
                    { "RetryCount", deliveryCount },
                    { "LastError", sendResult.Error ?? "Unknown error" }
                });
            }
        }
        catch (Exception ex)
        {
            await _messageTrackingService.TrackMessageStatusAsync(
                messageName, "Pending",
                $"Will be retried by Service Bus delivery policy. Error: {ex.Message}");
            await abandonAsync(new Dictionary<string, object>
            {
                { "RetryCount", deliveryCount },
                { "LastError", ex.Message }
            });
        }
    }

    // -------------------------------------------------------------------------
    // Safe ServiceBus operation wrappers
    // -------------------------------------------------------------------------

    private static async Task SafeCompleteAsync(IReceiverClient receiverClient, Message message)
    {
        try
        {
            await receiverClient.CompleteAsync(message.SystemProperties.LockToken);
            Console.WriteLine("Message Acknowledged");
        }
        catch (MessageLockLostException ex)
        {
            Console.WriteLine($"Message lock lost while completing: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error completing message: {ex.Message}");
        }
    }

    private static async Task SafeDeadLetterAsync(
        IReceiverClient receiverClient, Message message, string reason, string description)
    {
        try
        {
            await receiverClient.DeadLetterAsync(message.SystemProperties.LockToken, reason, description);
            Console.WriteLine($"Message dead-lettered: {reason}");
        }
        catch (MessageLockLostException ex)
        {
            Console.WriteLine($"Message lock lost while dead-lettering: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error dead-lettering message: {ex.Message}");
        }
    }

    private static async Task SafeAbandonAsync(
        IReceiverClient receiverClient, Message message, IDictionary<string, object> props)
    {
        try
        {
            await receiverClient.AbandonAsync(message.SystemProperties.LockToken, props);
            Console.WriteLine("Message Abandoned");
        }
        catch (MessageLockLostException ex)
        {
            Console.WriteLine($"Message lock lost while abandoning: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error abandoning message: {ex.Message}");
        }
    }

    private static Task ExceptionReceivedHandler(ExceptionReceivedEventArgs args)
    {
        Console.WriteLine($"Message handler encountered an exception: {args.Exception}");
        return Task.CompletedTask;
    }

    private int ResolvePriority(string topicName)
    {
        if (_topicPriorityMapping.TryGetValue(topicName, out var configuredPriority))
        {
            return configuredPriority;
        }

        // Keep auth-related topics higher priority even if config misses them.
        if (topicName.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return 100;
    }

    private static string GetSubscriptionClientKey(string topicName, string subscriptionName) =>
        $"{topicName}::{subscriptionName}";

    private static string ResolveMessageType(string candidate, ParsedMessagePayload parsedPayload)
    {
        if (!string.IsNullOrWhiteSpace(parsedPayload.Channel))
        {
            return parsedPayload.Channel!;
        }

        return string.IsNullOrWhiteSpace(candidate) ? "whatsapp" : candidate;
    }

    private static string ResolveMessageName(
        string? candidate,
        ParsedMessagePayload parsedPayload,
        WhatsAppMessage? bodyMessage)
    {
        if (!string.IsNullOrWhiteSpace(parsedPayload.MessageName))
        {
            return parsedPayload.MessageName!;
        }

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        if (!string.IsNullOrWhiteSpace(bodyMessage?.MessageName))
        {
            return bodyMessage.MessageName;
        }

        if (!string.IsNullOrWhiteSpace(bodyMessage?.MessageId))
        {
            return bodyMessage.MessageId;
        }

        return string.Empty;
    }

    private static ParsedMessagePayload ParseMessagePayload(string messageBody)
    {
        try
        {
            var payload = JsonConvert.DeserializeObject<ServiceBusNotificationMessage>(messageBody);
            if (payload == null)
            {
                return ParsedMessagePayload.Empty;
            }

            if (string.IsNullOrWhiteSpace(payload.RecipientAddress) || string.IsNullOrWhiteSpace(payload.Body))
            {
                return new ParsedMessagePayload(
                    MessageName: payload.MessageId ?? payload.CorrelationId ?? payload.EventName,
                    Channel: payload.Channel,
                    WhatsAppMessage: null);
            }

            var msgName = payload.MessageId ?? payload.CorrelationId ?? payload.EventName;
            return new ParsedMessagePayload(
                MessageName: msgName,
                Channel: payload.Channel,
                WhatsAppMessage: new WhatsAppMessage
                {
                    Name = payload.RecipientName ?? msgName ?? "unknown",
                    Phone = NormalizePhone(payload.RecipientAddress),
                    Message = payload.Body,
                    MessageName = msgName,
                    AttachmentUrl = payload.AttachmentUrl
                });
        }
        catch (JsonException)
        {
            return ParsedMessagePayload.Empty;
        }
    }

    private static string NormalizePhone(string recipientAddress)
    {
        var allowedChars = recipientAddress
            .Where(c => char.IsDigit(c) || c == '+')
            .ToArray();
        return new string(allowedChars);
    }

    private sealed record PendingMessage(
        string TopicName,
        Message Message,
        IReceiverClient ReceiverClient,
        TaskCompletionSource<bool> Completion);

    private sealed record ParsedMessagePayload(
        string? MessageName,
        string? Channel,
        WhatsAppMessage? WhatsAppMessage)
    {
        public static readonly ParsedMessagePayload Empty = new(null, null, null);
    }

    private sealed class ServiceBusNotificationMessage
    {
        [JsonProperty("message_id")]
        public string? MessageId { get; set; }

        [JsonProperty("correlation_id")]
        public string? CorrelationId { get; set; }

        [JsonProperty("channel")]
        public string? Channel { get; set; }

        [JsonProperty("event_name")]
        public string? EventName { get; set; }

        [JsonProperty("recipient_address")]
        public string? RecipientAddress { get; set; }

        [JsonProperty("recipient_name")]
        public string? RecipientName { get; set; }

        [JsonProperty("body")]
        public string? Body { get; set; }

        // Optional compatibility field in case backend includes attachment at root.
        [JsonProperty("attachment_url")]
        public string? AttachmentUrl { get; set; }
    }
}
