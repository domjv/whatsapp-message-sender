using WhatsappMessageSender.Models;
using Microsoft.Azure.ServiceBus;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System.Text;
using System.Collections.Concurrent;

namespace WhatsappMessageSender.Services;

public class QueueProcessor : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly WhatsAppService _whatsAppService;
    private readonly BlobStorageService _blobStorageService;
    private readonly ConcurrentDictionary<string, IQueueClient> _queueClients;
    private readonly int _maxConcurrentCalls;

    public QueueProcessor(
        IConfiguration configuration,
        WhatsAppService whatsAppService,
        BlobStorageService blobStorageService
        )
    {
        _configuration = configuration;
        _whatsAppService = whatsAppService;
        _blobStorageService = blobStorageService;
        _queueClients = new ConcurrentDictionary<string, IQueueClient>();
        _maxConcurrentCalls = _configuration.GetValue("ServiceBus:MaxConcurrentCalls", 1);
    }

    public void StartProcessing()
    {
        var queueNames = _configuration.GetSection("ServiceBus:QueueNames").Get<string[]>() 
            ?? throw new InvalidOperationException("Queue names not configured");
        var connectionString = _configuration["ServiceBus:ConnectionString"] 
            ?? throw new InvalidOperationException("ServiceBus connection string not configured");

        foreach (var queueName in queueNames)
        {
            var queueClient = new QueueClient(connectionString, queueName);
            _queueClients.TryAdd(queueName, queueClient);

            var messageHandlerOptions = new MessageHandlerOptions(ExceptionReceivedHandler)
            {
                MaxConcurrentCalls = _maxConcurrentCalls,
                AutoComplete = false
            };

            queueClient.RegisterMessageHandler(ProcessMessagesAsync, messageHandlerOptions);
            Console.WriteLine($"Started processing queue: {queueName}");
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
                        filePath = await _blobStorageService.DownloadFileAsync(msg.AttachmentUrl, msg.Name);
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