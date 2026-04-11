using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WhatsappMessageSender.Models;
using WhatsappMessageSender.Services;
using Microsoft.Extensions.Options;

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
                services.AddSingleton<RedisStreamProcessor>();
            })
            .Build();

        // Initialize MessageTrackingService
        var appSettings = host.Services.GetRequiredService<IOptions<AppSettings>>();
        MessageTrackingService.Initialize(appSettings);

        var processor = host.Services.GetRequiredService<RedisStreamProcessor>();
        processor.StartProcessing();

        Console.WriteLine("Press any key to exit");
        Console.ReadKey();

        await processor.CloseAsync();
    }
}