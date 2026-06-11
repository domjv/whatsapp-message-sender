using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsappMessageSender.Logging;
using WhatsappMessageSender.Models;
using WhatsappMessageSender.Services;

namespace WhatsappMessageSender;

class Program
{
    const string ServiceName = "WhatsappMessageSender";

    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var logDirectory = LoggingBootstrap.Configure(builder.Configuration);
        Console.WriteLine($"Logging to {logDirectory} (daily rolling files, prefix whatsapp-sender-)");

        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
            builder.Logging.AddEventLog(settings =>
            {
                settings.SourceName = ServiceName;
                settings.LogName = "Application";
            });
        }

        builder.Services.AddOptions<AppSettings>()
            .Bind(builder.Configuration)
            .Validate(
                settings => MessageTrackingRouting.ValidateAppSettings(settings, out _),
                "Message tracking configuration is invalid. Each configured topic/stream must " +
                "resolve to valid ErpInstances or MessageTracking settings.")
            .ValidateOnStart();
        builder.Services.AddSingleton<IWhatsAppService, WhatsAppService>();
        builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();
        builder.Services.AddSingleton<IMessageTrackingService, MessageTrackingService>();
        builder.Services.AddSingleton<IWhatsAppSendRateLimiter>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
            var lim = settings.WhatsAppSendRateLimit;
            return new WhatsAppSendRateLimiter(
                highPriorityLessThan: lim?.HighPriorityLessThan ?? 10,
                maxSendsPerMinute: lim?.MaxSendsPerMinute ?? 20,
                enabled: lim?.Enabled ?? true);
        });

        var broker = builder.Configuration["MessageBroker"] ?? "Redis";
        if (broker.Equals("ServiceBus", StringComparison.OrdinalIgnoreCase))
            builder.Services.AddSingleton<IMessageProcessor, QueueProcessor>();
        else
            builder.Services.AddSingleton<IMessageProcessor, RedisStreamProcessor>();

        builder.Services.AddHostedService<ProcessorHostedService>();

        var host = builder.Build();
        await host.RunAsync();
    }
}
