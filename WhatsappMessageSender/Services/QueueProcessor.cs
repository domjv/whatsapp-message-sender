using WhatsappMessageSender.Models;
using Microsoft.Azure.ServiceBus;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System.Text;
using System.Collections.Concurrent;

namespace WhatsappMessageSender.Services;

public class QueueProcessor : IDisposable
{
    private readonly AppSettings _appSettings;
    private readonly WhatsAppService _whatsAppService;
    private readonly BlobStorageService _blobStorageService;
    private readonly ConcurrentDictionary<string, IQueueClient> _queueClients;
    private readonly Dictionary<string, string> _queueContainerMapping;
    private readonly SemaphoreSlim _processingSemaphore = new(1);

    public QueueProcessor(
        IConfiguration configuration,
        WhatsAppService whatsAppService,
        BlobStorageService blobStorageService
        )
    {
        _appSettings = configuration.Get<AppSettings>() 
            ?? throw new InvalidOperationException("Invalid configuration");
        _whatsAppService = whatsAppService;
        _blobStorageService = blobStorageService;
        _queueClients = new ConcurrentDictionary<string, IQueueClient>();
        _queueContainerMapping = _appSettings.ServiceBus.Queues
            .ToDictionary(q => q.QueueName, q => q.ContainerName);
    }

    public void StartProcessing()
    {
        var connectionString = _appSettings.ServiceBus.ConnectionString;

        foreach (var queueConfig in _appSettings.ServiceBus.Queues)
        {
            var queueClient = new QueueClient(connectionString, queueConfig.QueueName);
            _queueClients.TryAdd(queueConfig.QueueName, queueClient);

            queueClient.PrefetchCount = 0;

            var messageHandlerOptions = new MessageHandlerOptions(ExceptionReceivedHandler)
            {
                MaxConcurrentCalls = 2,
                AutoComplete = false
            };

            queueClient.RegisterMessageHandler(ProcessMessagesAsync, messageHandlerOptions);
            Console.WriteLine($"Started processing queue: {queueConfig.QueueName}");
        }
    }

    private async Task ProcessMessagesAsync(Message message, CancellationToken token)
    {
        try
        {
            await _processingSemaphore.WaitAsync(token);

            var messageBody = Encoding.UTF8.GetString(message.Body);
            var messageId = message.MessageId;
            var deliveryCount = message.SystemProperties.DeliveryCount;
            
            Console.WriteLine($"Processing message: {messageId} (Attempt {deliveryCount})");

            var queueName = message.UserProperties.TryGetValue("QueueName", out var queueNameObj)
                ? queueNameObj?.ToString()
                : "sbq-pleasntbiz";

            var connectionString = _appSettings.ServiceBus.ConnectionString;
            var queueClient =new QueueClient(connectionString,queueName);
            try
            {
                if (deliveryCount > RetrySettings.MaxRetries)
                {
                    Console.WriteLine($"Message {messageId} exceeded maximum retries. Moving to dead letter queue.");
                    await queueClient.DeadLetterAsync(message.SystemProperties.LockToken, 
                        "MaxRetriesExceeded", 
                        $"Message failed after {RetrySettings.MaxRetries} attempts");
                    return;
                }

                var messageType = message.UserProperties.TryGetValue("MessageType", out var property)
                    ? property?.ToString()
                    : "whatsapp";

                var properties = new MessageProperties
                {
                    MessageType = messageType ?? "whatsapp",
                    QueueName = queueName ?? "sbq-pleasntbiz",

                };

                await MessageTrackingService.TrackMessageStatusAsync(messageId, "Processing");

                if (properties.MessageType.Equals("whatsapp", StringComparison.CurrentCultureIgnoreCase))
                {
                    var msg = JsonConvert.DeserializeObject<WhatsAppMessage>(messageBody);
                    if (msg == null)
                    {
                        await MessageTrackingService.TrackMessageStatusAsync(messageId, "Failed", "Invalid message format");
                        await queueClient.DeadLetterAsync(message.SystemProperties.LockToken, 
                            "InvalidFormat", 
                            "Message could not be deserialized");
                        return;
                    }

                    try
                    {
                        string? filePath = null;
                        if (!string.IsNullOrEmpty(msg.AttachmentUrl))
                        {
                            var containerName = _queueContainerMapping[properties.QueueName];
                            filePath = await _blobStorageService.DownloadFileAsync(msg.AttachmentUrl, msg.Name, containerName);
                            await MessageTrackingService.TrackMessageStatusAsync(messageId, "FileDownloaded");
                        }

                        var sendResult = await _whatsAppService.SendMessageAsync(msg.Phone, msg.Message, filePath);
                        
                        if (sendResult.Success)
                        {
                            await MessageTrackingService.TrackMessageStatusAsync(messageId, "Delivered");
                            await SafeCompleteMessageAsync(queueClient, message);
                        }
                        else
                        {
                            var delay = RetrySettings.GetDelayForRetry(deliveryCount);
                            await MessageTrackingService.TrackMessageStatusAsync(messageId, "RetryScheduled", 
                                $"Will retry in {delay.TotalSeconds} seconds. Error: {sendResult.Error}");
                            
                            // Schedule retry with exponential backoff
                            await queueClient.AbandonAsync(message.SystemProperties.LockToken, 
                                new Dictionary<string, object>
                                {
                                    { "RetryCount", deliveryCount },
                                    { "LastError", sendResult.Error ?? "Unknown error" }
                                });
                        }
                    }
                    catch (Exception ex)
                    {
                        var delay = RetrySettings.GetDelayForRetry(deliveryCount);
                        await MessageTrackingService.TrackMessageStatusAsync(messageId, "RetryScheduled", 
                            $"Will retry in {delay.TotalSeconds} seconds. Error: {ex.Message}");
                        
                        await SafeAbandonMessageAsync(queueClient, message);
                    }
                }
            }
            catch (Exception ex)
            {
                await MessageTrackingService.TrackMessageStatusAsync(messageId, "Failed", ex.Message);
                Console.WriteLine($"Error processing message: {ex.Message}");
                await SafeAbandonMessageAsync(queueClient, message);
            }
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private static async Task SafeCompleteMessageAsync(IQueueClient queueClient, Message message)
    {
        try
        {
            await queueClient.CompleteAsync(message.SystemProperties.LockToken);
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

    private static async Task SafeAbandonMessageAsync(IQueueClient queueClient, Message message)
    {
        try
        {
            await queueClient.AbandonAsync(message.SystemProperties.LockToken);
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

    private static Task ExceptionReceivedHandler(ExceptionReceivedEventArgs exceptionReceivedEventArgs)
    {
        Console.WriteLine($"Message handler encountered an exception: {exceptionReceivedEventArgs.Exception}");
        return Task.CompletedTask;
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
        _processingSemaphore.Dispose();
    }
}