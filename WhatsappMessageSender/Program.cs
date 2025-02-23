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
                services.AddSingleton<WhatsAppService>();
                services.AddSingleton<BlobStorageService>();
                services.AddSingleton<MessageTrackingService>();
                services.AddSingleton<QueueProcessor>();
            })
            .Build();

        var queueProcessor = host.Services.GetRequiredService<QueueProcessor>();
        queueProcessor.StartProcessing();

        Console.WriteLine("Press any key to exit");
        Console.ReadKey();

        await queueProcessor.CloseAsync();
    }
}