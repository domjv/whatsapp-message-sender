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

            var messageHandlerOptions = new MessageHandlerOptions(ExceptionReceivedHandler)
            {
                MaxConcurrentCalls = _appSettings.ServiceBus.MaxConcurrentCalls,
                AutoComplete = false
            };

            queueClient.RegisterMessageHandler(ProcessMessagesAsync, messageHandlerOptions);
            Console.WriteLine($"Started processing queue: {queueConfig.QueueName}");
        }
    }

    private async Task ProcessMessagesAsync(Message message, CancellationToken token)
    {
        var messageBody = Encoding.UTF8.GetString(message.Body);
        var messageId = message.MessageId;
        Console.WriteLine($"Processing message: {messageId}");

        var queueClient = _queueClients.Values.FirstOrDefault();
        if (queueClient == null)
        {
            Console.WriteLine("No queue clients available");
            return;
        }

        try
        {
            var messageType = message.UserProperties.TryGetValue("MessageType", out var property)
                ? property?.ToString()
                : "whatsapp";

            var properties = new MessageProperties
            {
                MessageType = messageType ?? "whatsapp"
            };

            await MessageTrackingService.TrackMessageStatusAsync(messageId, "Processing");

            if (properties.MessageType.Equals("whatsapp", StringComparison.CurrentCultureIgnoreCase))
            {
                var msg = JsonConvert.DeserializeObject<WhatsAppMessage>(messageBody);
                if (msg == null)
                {
                    await MessageTrackingService.TrackMessageStatusAsync(messageId, "Failed", "Invalid message format");
                    await SafeAbandonMessageAsync(queueClient, message);
                    return;
                }

                try
                {
                    string? filePath = null;
                    if (!string.IsNullOrEmpty(msg.AttachmentUrl))
                    {
                        var queueName = _queueClients.FirstOrDefault(x => x.Value == queueClient).Key;
                        var containerName = _queueContainerMapping[queueName];
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
                        await MessageTrackingService.TrackMessageStatusAsync(messageId, "Failed", sendResult.Error);
                        await SafeAbandonMessageAsync(queueClient, message);
                    }
                }
                catch (Exception ex)
                {
                    await MessageTrackingService.TrackMessageStatusAsync(messageId, "Failed", ex.Message);
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
    }
}