using System.Collections.Concurrent;
using System.Text;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using WhatsappMessageSender.Models;

namespace WhatsappMessageSender.Services;

/// <summary>
/// Reads WhatsApp notification messages from Azure Service Bus topic subscriptions and
/// processes them with at-least-once delivery (lock-based retry).
/// </summary>
public class QueueProcessor : IMessageProcessor
{
    private readonly AppSettings _appSettings;
    private readonly IWhatsAppService _whatsAppService;
    private readonly IBlobStorageService _blobStorageService;
    private readonly IMessageTrackingService _messageTrackingService;
    private readonly Dictionary<string, string> _topicContainerMapping;
    private readonly Dictionary<string, int> _topicPriorityMapping;
    private readonly PriorityQueue<PendingMessage, (int Priority, long Sequence)> _pendingMessages = new();
    private readonly SemaphoreSlim _pendingSignal = new(0);
    private readonly object _pendingLock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _dispatcherTask;
    private long _enqueueSequence;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ConcurrentDictionary<string, ServiceBusProcessor> _processors = new();
    private readonly ConcurrentDictionary<string, ServiceBusSessionProcessor> _sessionProcessors = new();

    // Selenium/WhatsApp Web driver is single-session and not thread-safe.
    private readonly SemaphoreSlim _whatsAppSendSemaphore = new(1, 1);
    private readonly IWhatsAppSendRateLimiter _whatsAppSendRateLimiter;

    public QueueProcessor(
        IConfiguration configuration,
        IWhatsAppService whatsAppService,
        IBlobStorageService blobStorageService,
        IMessageTrackingService messageTrackingService,
        IWhatsAppSendRateLimiter whatsAppSendRateLimiter)
    {
        _appSettings = configuration.Get<AppSettings>()
            ?? throw new InvalidOperationException("Invalid configuration");

        if (_appSettings.ServiceBus == null)
            throw new InvalidOperationException(
                "ServiceBus configuration is missing. Set 'MessageBroker' to 'ServiceBus' and provide a 'ServiceBus' config section.");

        _whatsAppService = whatsAppService;
        _blobStorageService = blobStorageService;
        _messageTrackingService = messageTrackingService;
        _whatsAppSendRateLimiter = whatsAppSendRateLimiter;
        _topicContainerMapping = _appSettings.ServiceBus.Topics
            .GroupBy(t => t.TopicName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ContainerName, StringComparer.OrdinalIgnoreCase);
        _topicPriorityMapping = _appSettings.ServiceBus.Topics
            .GroupBy(t => t.TopicName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Priority, StringComparer.OrdinalIgnoreCase);

        var transport = _appSettings.ServiceBus.UseWebSocketsTransport
            ? ServiceBusTransportType.AmqpWebSockets
            : ServiceBusTransportType.AmqpTcp;
        _serviceBusClient = new ServiceBusClient(
            _appSettings.ServiceBus.ConnectionString,
            new ServiceBusClientOptions { TransportType = transport });
    }

    public void StartProcessing()
    {
        var serviceBusSettings = _appSettings.ServiceBus
            ?? throw new InvalidOperationException("ServiceBus configuration is missing.");
        var maxConcurrent = serviceBusSettings.MaxConcurrentCalls;
        var maxAutoRenew = TimeSpan.FromMinutes(
            Math.Max(1, serviceBusSettings.MaxAutoRenewDurationMinutes));

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

        foreach (var topicConfig in serviceBusSettings.Topics)
        {
            if (topicConfig.RequiresSession)
            {
                var sessionProcessor = _serviceBusClient.CreateSessionProcessor(
                    topicConfig.TopicName,
                    topicConfig.SubscriptionName,
                    new ServiceBusSessionProcessorOptions
                    {
                        AutoCompleteMessages = false,
                        MaxConcurrentSessions = maxConcurrent,
                        MaxAutoLockRenewalDuration = maxAutoRenew
                    });

                sessionProcessor.ProcessErrorAsync += ExceptionReceivedHandler;
                sessionProcessor.ProcessMessageAsync += args => ProcessSessionMessageAsync(args, topicConfig.TopicName);
                _sessionProcessors.TryAdd(GetSubscriptionClientKey(topicConfig.TopicName, topicConfig.SubscriptionName), sessionProcessor);
                _ = sessionProcessor.StartProcessingAsync(_cts.Token);

                Console.WriteLine(
                    $"Started processing topic/subscription (sessions): {topicConfig.TopicName}/{topicConfig.SubscriptionName}");
            }
            else
            {
                var processor = _serviceBusClient.CreateProcessor(
                    topicConfig.TopicName,
                    topicConfig.SubscriptionName,
                    new ServiceBusProcessorOptions
                    {
                        AutoCompleteMessages = false,
                        MaxConcurrentCalls = maxConcurrent,
                        MaxAutoLockRenewalDuration = maxAutoRenew
                    });
                processor.ProcessErrorAsync += ExceptionReceivedHandler;
                processor.ProcessMessageAsync += args => ProcessNonSessionMessageAsync(args, topicConfig.TopicName);
                _processors.TryAdd(GetSubscriptionClientKey(topicConfig.TopicName, topicConfig.SubscriptionName), processor);
                _ = processor.StartProcessingAsync(_cts.Token);

                Console.WriteLine(
                    $"Started processing topic/subscription: {topicConfig.TopicName}/{topicConfig.SubscriptionName}");
            }
        }

        if (serviceBusSettings.Topics.Count == 0)
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

        foreach (var processor in _processors.Values)
        {
            await processor.StopProcessingAsync();
            await processor.DisposeAsync();
        }

        foreach (var sessionProcessor in _sessionProcessors.Values)
        {
            await sessionProcessor.StopProcessingAsync();
            await sessionProcessor.DisposeAsync();
        }

        await _serviceBusClient.DisposeAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pendingSignal.Release();
        _pendingSignal.Dispose();
        _cts.Dispose();
        _whatsAppSendSemaphore.Dispose();
        _serviceBusClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private Task ProcessNonSessionMessageAsync(ProcessMessageEventArgs args, string configuredTopicName)
    {
        var message = args.Message;
        var resolvedTopicName = message.ApplicationProperties.TryGetValue("TopicName", out var topicNameObj)
            ? topicNameObj?.ToString() ?? configuredTopicName
            : configuredTopicName;

        var priority = ResolvePriority(resolvedTopicName);
        var pendingMessage = new PendingMessage(
            resolvedTopicName,
            message,
            DeliveryCount: message.DeliveryCount,
            CompleteAsync: () => args.CompleteMessageAsync(message),
            DeadLetterAsync: (reason, desc) => args.DeadLetterMessageAsync(message, reason, desc),
            AbandonAsync: props => args.AbandonMessageAsync(message, props),
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        lock (_pendingLock)
        {
            _pendingMessages.Enqueue(
                pendingMessage,
                (priority, Interlocked.Increment(ref _enqueueSequence)));
        }

        _pendingSignal.Release();
        return pendingMessage.Completion.Task;
    }

    private Task ProcessSessionMessageAsync(ProcessSessionMessageEventArgs args, string configuredTopicName)
    {
        var message = args.Message;
        var resolvedTopicName = message.ApplicationProperties.TryGetValue("TopicName", out var topicNameObj)
            ? topicNameObj?.ToString() ?? configuredTopicName
            : configuredTopicName;

        var priority = ResolvePriority(resolvedTopicName);
        var pendingMessage = new PendingMessage(
            resolvedTopicName,
            message,
            DeliveryCount: message.DeliveryCount,
            CompleteAsync: () => args.CompleteMessageAsync(message),
            DeadLetterAsync: (reason, desc) => args.DeadLetterMessageAsync(message, reason, desc),
            AbandonAsync: props => args.AbandonMessageAsync(message, props),
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        lock (_pendingLock)
        {
            _pendingMessages.Enqueue(
                pendingMessage,
                (priority, Interlocked.Increment(ref _enqueueSequence)));
        }

        _pendingSignal.Release();
        return pendingMessage.Completion.Task;
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
        var messageBody = Encoding.UTF8.GetString(message.Body.ToArray());
        var deliveryCount = pending.DeliveryCount;
        var messageType = message.ApplicationProperties.TryGetValue("MessageType", out var messageTypeObj)
            ? messageTypeObj?.ToString() ?? "whatsapp"
            : "whatsapp";
        var messageName = message.ApplicationProperties.TryGetValue("MessageName", out var messageNameObj)
            ? messageNameObj?.ToString()
            : null;

        var dispatchPriority = ResolvePriority(pending.TopicName);

        await ProcessMessageCoreAsync(
            messageId: messageId,
            messageBody: messageBody,
            deliveryCount: deliveryCount,
            queueName: pending.TopicName,
            messageType: messageType,
            messageName: messageName,
            completeAsync: pending.CompleteAsync,
            deadLetterAsync: pending.DeadLetterAsync,
            abandonAsync: pending.AbandonAsync,
            dispatchPriority: dispatchPriority,
            cancellationToken: _cts.Token);
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
        Func<IDictionary<string, object>, Task> abandonAsync,
        int dispatchPriority = 0,
        CancellationToken cancellationToken = default)
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

        var backendMessageId = ResolveBackendMessageId(messageId, parsedPayload, bodyForName, messageName);

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
                messageProperties.ChannelName,
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
                await _messageTrackingService.TrackMessageStatusAsync(
                    messageProperties.ChannelName,
                    backendMessageId,
                    "Failed",
                    $"Unsupported message type: {messageProperties.MessageType}",
                    null,
                    null);
                await deadLetterAsync(
                    "UnsupportedMessageType",
                    $"Unsupported message type: {messageProperties.MessageType}");
                return;
            }

            WhatsAppMessage? msg = parsedPayload.WhatsAppMessage ?? bodyForName;

            if (msg == null)
            {
                await _messageTrackingService.TrackMessageStatusAsync(
                    messageProperties.ChannelName, backendMessageId, "Failed", "Invalid message format", null, null);
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
            await _whatsAppSendSemaphore.WaitAsync(cancellationToken);
            try
            {
                await _whatsAppSendRateLimiter.WaitForSendSlotAsync(dispatchPriority, cancellationToken);
                sendResult = await _whatsAppService.SendMessageAsync(
                    msg.Phone, msg.Message, filePath);
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
                    messageProperties.ChannelName,
                    backendMessageId,
                    "Sent",
                    null,
                    sendResult.ProviderMessageId,
                    deliveredAt);
                await completeAsync();
                Console.WriteLine($"Message {messageId} sent via topic: {queueName}");
            }
            else
            {
                await _messageTrackingService.TrackMessageStatusAsync(
                    messageProperties.ChannelName,
                    backendMessageId,
                    "Pending",
                    $"Will be retried by Service Bus delivery policy. Error: {sendResult.Error}",
                    sendResult.ProviderMessageId,
                    null);
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
                messageProperties.ChannelName,
                backendMessageId,
                "Pending",
                $"Will be retried by Service Bus delivery policy. Error: {ex.Message}",
                null,
                null);
            await abandonAsync(new Dictionary<string, object>
            {
                { "RetryCount", deliveryCount },
                { "LastError", ex.Message }
            });
        }
    }

    private static Task ExceptionReceivedHandler(ProcessErrorEventArgs args)
    {
        if (args.Exception is ServiceBusException sbEx &&
            (sbEx.IsTransient ||
             sbEx.Message.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase) ||
             sbEx.Message.Contains("connection was inactive", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(
                $"Service Bus transient issue ({args.ErrorSource}): {sbEx.Message}");
            return Task.CompletedTask;
        }

        Console.WriteLine(
            $"Service Bus processor error ({args.ErrorSource}) entity '{args.EntityPath}': {args.Exception}");
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

    private static string ResolveBackendMessageId(
        string messageId,
        ParsedMessagePayload parsedPayload,
        WhatsAppMessage? bodyMessage,
        string messageName)
    {
        if (!string.IsNullOrWhiteSpace(parsedPayload.BackendMessageId))
        {
            return parsedPayload.BackendMessageId!;
        }

        if (!string.IsNullOrWhiteSpace(bodyMessage?.MessageId))
        {
            return bodyMessage.MessageId!;
        }

        if (!string.IsNullOrWhiteSpace(messageName))
        {
            return messageName;
        }

        return messageId;
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
                    WhatsAppMessage: null,
                    BackendMessageId: payload.MessageId ?? payload.CorrelationId);
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
                    MessageId = payload.MessageId,
                    AttachmentUrl = payload.AttachmentUrl
                },
                BackendMessageId: payload.MessageId);
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
        ServiceBusReceivedMessage Message,
        int DeliveryCount,
        Func<Task> CompleteAsync,
        Func<string, string, Task> DeadLetterAsync,
        Func<IDictionary<string, object>, Task> AbandonAsync,
        TaskCompletionSource<bool> Completion);

    private sealed record ParsedMessagePayload(
        string? MessageName,
        string? Channel,
        WhatsAppMessage? WhatsAppMessage,
        string? BackendMessageId)
    {
        public static readonly ParsedMessagePayload Empty = new(null, null, null, null);
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
