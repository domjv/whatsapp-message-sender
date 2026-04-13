using WhatsappMessageSender.Models;
using Microsoft.Azure.ServiceBus;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System.Text;
using System.Collections.Concurrent;

namespace WhatsappMessageSender.Services;

/// <summary>
/// Reads WhatsApp notification messages from Azure Service Bus queues and
/// processes them with at-least-once delivery (lock-based retry).
///
/// Flow:
///   1. RegisterMessageHandler receives messages per configured queue.
///   2. On success  → CompleteAsync (removes message from the queue).
///   3. On failure  → AbandonAsync  (Service Bus releases the lock so the
///      message becomes visible again after the visibility timeout, up to
///      the queue's MaxDeliveryCount).
///   4. MaxRetries exceeded → DeadLetterAsync.
/// </summary>
public class QueueProcessor : IMessageProcessor
{
    private readonly AppSettings _appSettings;
    private readonly IWhatsAppService _whatsAppService;
    private readonly IBlobStorageService _blobStorageService;
    private readonly IMessageTrackingService _messageTrackingService;
    private readonly ConcurrentDictionary<string, IQueueClient> _queueClients;
    private readonly Dictionary<string, string> _queueContainerMapping;
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
        _queueClients = new ConcurrentDictionary<string, IQueueClient>();
        _queueContainerMapping = _appSettings.ServiceBus.Queues
            .ToDictionary(q => q.QueueName, q => q.ContainerName);
    }

    public void StartProcessing()
    {
        var connectionString = _appSettings.ServiceBus!.ConnectionString;
        var maxConcurrent = _appSettings.ServiceBus.MaxConcurrentCalls;

        foreach (var queueConfig in _appSettings.ServiceBus.Queues)
        {
            var queueClient = new QueueClient(connectionString, queueConfig.QueueName);
            _queueClients.TryAdd(queueConfig.QueueName, queueClient);

            queueClient.PrefetchCount = 0;

            var messageHandlerOptions = new MessageHandlerOptions(ExceptionReceivedHandler)
            {
                MaxConcurrentCalls = maxConcurrent,
                AutoComplete = false
            };

            queueClient.RegisterMessageHandler(ProcessMessagesAsync, messageHandlerOptions);
            Console.WriteLine($"Started processing queue: {queueConfig.QueueName}");
        }
    }

    public async Task CloseAsync()
    {
        foreach (var queueClient in _queueClients.Values)
        {
            await queueClient.CloseAsync();
        }
    }

    public void Dispose()
    {
        foreach (var queueClient in _queueClients.Values)
        {
            queueClient.CloseAsync().Wait();
        }
    }

    // -------------------------------------------------------------------------
    // ServiceBus callback — extracts data then delegates to the core method
    // -------------------------------------------------------------------------

    private async Task ProcessMessagesAsync(Message message, CancellationToken token)
    {
        var messageId = message.MessageId;
        var queueName = message.UserProperties.TryGetValue("QueueName", out var queueNameObj)
            ? queueNameObj?.ToString() ?? _appSettings.ServiceBus!.Queues[0].QueueName
            : _appSettings.ServiceBus!.Queues[0].QueueName;

        if (!_queueClients.TryGetValue(queueName, out var queueClient))
        {
            Console.WriteLine($"No queue client found for queue: {queueName}");
            return;
        }

        var messageType = message.UserProperties.TryGetValue("MessageType", out var messageTypeObj)
            ? messageTypeObj?.ToString() ?? "whatsapp"
            : "whatsapp";

        var messageName = message.UserProperties.TryGetValue("MessageName", out var messageNameObj)
            ? messageNameObj?.ToString()
            : null;

        var messageBody = Encoding.UTF8.GetString(message.Body);
        var deliveryCount = message.SystemProperties.DeliveryCount;

        await ProcessMessageCoreAsync(
            messageId: messageId,
            messageBody: messageBody,
            deliveryCount: deliveryCount,
            queueName: queueName,
            messageType: messageType,
            messageName: messageName,
            completeAsync: () => SafeCompleteAsync(queueClient, message),
            deadLetterAsync: (reason, desc) => SafeDeadLetterAsync(queueClient, message, reason, desc),
            abandonAsync: (props) => SafeAbandonAsync(queueClient, message, props));
    }

    // -------------------------------------------------------------------------
    // Core message processing — internal so unit tests can call it directly
    // without needing a real ServiceBus Message object.
    // -------------------------------------------------------------------------

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
            await _messageTrackingService.TrackMessageStatusAsync(
                messageProperties.MessageName, "Processing");

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

            WhatsAppMessage? msg;
            try
            {
                msg = JsonConvert.DeserializeObject<WhatsAppMessage>(messageBody);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                await _messageTrackingService.TrackMessageStatusAsync(
                    messageProperties.MessageName, "Failed", "Invalid message format");
                await deadLetterAsync("InvalidFormat", "Message could not be deserialized");
                return;
            }

            if (msg == null)
            {
                await _messageTrackingService.TrackMessageStatusAsync(
                    messageProperties.MessageName, "Failed", "Invalid message format");
                await deadLetterAsync("InvalidFormat", "Message could not be deserialized");
                return;
            }

            string? filePath = null;
            if (!string.IsNullOrEmpty(msg.AttachmentUrl) &&
                _queueContainerMapping.TryGetValue(messageProperties.ChannelName, out var containerName))
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
                    messageProperties.MessageName, "Delivered");
                await completeAsync();
                Console.WriteLine($"Message {messageId} sent to queue: {queueName}");
            }
            else
            {
                await _messageTrackingService.TrackMessageStatusAsync(
                    messageProperties.MessageName, "Retry Pending",
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
                messageName, "Retry Pending",
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

    private static async Task SafeCompleteAsync(IQueueClient queueClient, Message message)
    {
        try
        {
            await queueClient.CompleteAsync(message.SystemProperties.LockToken);
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
        IQueueClient queueClient, Message message, string reason, string description)
    {
        try
        {
            await queueClient.DeadLetterAsync(message.SystemProperties.LockToken, reason, description);
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
        IQueueClient queueClient, Message message, IDictionary<string, object> props)
    {
        try
        {
            await queueClient.AbandonAsync(message.SystemProperties.LockToken, props);
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
}
