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
                // Validate critical settings during startup so the service fails
                // fast with a clear message instead of crashing after startup.
                services.AddOptions<AppSettings>()
                    .Bind(context.Configuration)
                    .Validate(settings =>
                    {
                        if (settings.MessageTracking == null)
                            return false;

                        if (string.IsNullOrWhiteSpace(settings.MessageTracking.NotificationSecret))
                            return false;

                        return Uri.TryCreate(
                            settings.MessageTracking.ApiUrl, UriKind.Absolute, out _);
                    },
                    "MessageTracking must include a valid ApiUrl and NotificationSecret.")
                    .ValidateOnStart();
                services.AddSingleton<IWhatsAppService, WhatsAppService>();
                services.AddSingleton<IBlobStorageService, BlobStorageService>();
                services.AddSingleton<IMessageTrackingService, MessageTrackingService>();

                // Select the message processor based on the configured broker
                var broker = context.Configuration["MessageBroker"] ?? "Redis";
                if (broker.Equals("ServiceBus", StringComparison.OrdinalIgnoreCase))
                    services.AddSingleton<IMessageProcessor, QueueProcessor>();
                else
                    services.AddSingleton<IMessageProcessor, RedisStreamProcessor>();

                // Bridge host lifetime to IMessageProcessor.Start/Close.
                services.AddHostedService<ProcessorHostedService>();
            })
            .Build();

        await host.RunAsync();
    }
}
