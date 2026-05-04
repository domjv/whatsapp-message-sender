using Microsoft.Extensions.Options;
using WhatsappMessageSender.Models;
using WhatsappMessageSender.Services;

namespace WhatsappMessageSender.Tests;

public class MessageTrackingServiceTests
{
    [Fact]
    public void Constructor_MissingMessageTracking_Throws()
    {
        var options = Options.Create(new AppSettings
        {
            MessageTracking = null!,
            BlobStorage = new BlobStorageSettings { ConnectionString = "UseDevelopmentStorage=true" },
            WhatsApp = new WhatsAppSettings { ProfilePath = "/tmp", ChromeDriverPath = "/tmp" }
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new MessageTrackingService(options));
        Assert.Contains("MessageTracking configuration is missing", ex.Message);
    }

    [Fact]
    public void Constructor_InvalidApiUrl_Throws()
    {
        var options = Options.Create(new AppSettings
        {
            MessageTracking = new MessageTrackingSettings
            {
                ApiUrl = "not-a-url",
                NotificationSecret = "secret-123"
            },
            BlobStorage = new BlobStorageSettings { ConnectionString = "UseDevelopmentStorage=true" },
            WhatsApp = new WhatsAppSettings { ProfilePath = "/tmp", ChromeDriverPath = "/tmp" }
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new MessageTrackingService(options));
        Assert.Contains("MessageTracking:ApiUrl", ex.Message);
    }

    [Fact]
    public void FormatDeliveredAtUtc_UsesMySqlFriendlyUtcString()
    {
        var utc = new DateTime(2026, 5, 4, 13, 29, 18, DateTimeKind.Utc);
        var s = MessageTrackingService.FormatDeliveredAtUtc(utc);
        Assert.Equal("2026-05-04 13:29:18", s);
        Assert.DoesNotContain('T', s);
        Assert.DoesNotContain('Z', s);
    }
}
