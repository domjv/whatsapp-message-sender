using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WhatsappMessageSender.Models;
using WhatsappMessageSender.Services;

namespace WhatsappMessageSender;

class Program
{
    static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                services.Configure<AppSettings>(context.Configuration);
                services.AddSingleton<IWhatsAppService, WhatsAppService>();
                services.AddSingleton<IBlobStorageService, BlobStorageService>();
                services.AddSingleton<IMessageTrackingService, MessageTrackingService>();

                // Select the message processor based on the configured broker
                var broker = context.Configuration["MessageBroker"] ?? "Redis";
                if (broker.Equals("ServiceBus", StringComparison.OrdinalIgnoreCase))
                    services.AddSingleton<IMessageProcessor, QueueProcessor>();
                else
                    services.AddSingleton<IMessageProcessor, RedisStreamProcessor>();
            })
            .Build();

        var processor = host.Services.GetRequiredService<IMessageProcessor>();
        processor.StartProcessing();

        Console.WriteLine("Press any key to exit");
        Console.ReadKey();

        await processor.CloseAsync();
    }
}
