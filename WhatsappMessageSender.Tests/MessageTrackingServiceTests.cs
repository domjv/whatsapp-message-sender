using System.Net;
using Microsoft.Extensions.Options;
using WhatsappMessageSender.Models;
using WhatsappMessageSender.Services;

namespace WhatsappMessageSender.Tests;

public class MessageTrackingServiceTests
{
    private static AppSettings BaseAppSettings() => new()
    {
        BlobStorage = new BlobStorageSettings { ConnectionString = "UseDevelopmentStorage=true" },
        WhatsApp = new WhatsAppSettings { ProfilePath = "/tmp", ChromeDriverPath = "/tmp" }
    };

    [Fact]
    public void Constructor_NoResolvableTracking_Throws()
    {
        var options = Options.Create(BaseAppSettings());

        var ex = Assert.Throws<InvalidOperationException>(() => new MessageTrackingService(options));
        Assert.Contains("MessageTracking configuration is missing", ex.Message);
    }

    [Fact]
    public void Constructor_InvalidChannelTracking_Throws()
    {
        var settings = BaseAppSettings();
        settings.ServiceBus = new ServiceBusSettings
        {
            ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==",
            Topics =
            [
                new TopicSubscriptionConfig
                {
                    TopicName = "hm-ivyliving-attendance",
                    SubscriptionName = "whatsapp-message-sender"
                }
            ]
        };
        settings.MessageTracking = new MessageTrackingSettings
        {
            ApiUrl = "not-a-url",
            NotificationSecret = "secret-123"
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new MessageTrackingService(settings, httpClient: null));
        Assert.Contains("hm-ivyliving-attendance", ex.Message);
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

    [Fact]
    public void ResolveErpInstanceId_LongestMatch_PrefersIvylivingbudgetOverIvyliving()
    {
        var instances = new List<ErpInstanceConfig>
        {
            new() { Id = "ivyliving", MessageTracking = ValidTracking("http://ivyliving/") },
            new() { Id = "ivylivingbudget", MessageTracking = ValidTracking("http://budget/") }
        };

        var id = MessageTrackingRouting.ResolveErpInstanceId(
            "hm-ivylivingbudget-attendance", null, instances);

        Assert.Equal("ivylivingbudget", id);
    }

    [Fact]
    public void ResolveErpInstanceId_AutoDetectsFromTopicPrefix()
    {
        var instances = new List<ErpInstanceConfig>
        {
            new() { Id = "ajk", MessageTracking = ValidTracking("http://ajk/") }
        };

        var id = MessageTrackingRouting.ResolveErpInstanceId("hm-ajk-leave", null, instances);
        Assert.Equal("ajk", id);
    }

    [Fact]
    public void ResolveErpInstanceId_UsesExplicitIdOverAutoDetect()
    {
        var instances = new List<ErpInstanceConfig>
        {
            new() { Id = "ajk", MessageTracking = ValidTracking("http://ajk/") }
        };

        var id = MessageTrackingRouting.ResolveErpInstanceId("hm-custom-topic", "ajk", instances);
        Assert.Equal("ajk", id);
    }

    [Fact]
    public async Task TrackMessageStatusAsync_RoutesToCorrectInstanceUrlAndSecret()
    {
        const string ajkUrl = "http://ajk.localhost/api/method/report_delivery_status";
        const string ajkSecret = "ajk-secret";
        const string ivyUrl = "http://ivyliving.localhost/api/method/report_delivery_status";
        const string ivySecret = "ivy-secret";

        var settings = BaseAppSettings();
        settings.ErpInstances =
        [
            new ErpInstanceConfig
            {
                Id = "ajk",
                MessageTracking = new MessageTrackingSettings { ApiUrl = ajkUrl, NotificationSecret = ajkSecret }
            },
            new ErpInstanceConfig
            {
                Id = "ivyliving",
                MessageTracking = new MessageTrackingSettings { ApiUrl = ivyUrl, NotificationSecret = ivySecret }
            }
        ];
        settings.ServiceBus = new ServiceBusSettings
        {
            ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==",
            Topics =
            [
                new TopicSubscriptionConfig
                {
                    TopicName = "hm-ajk-attendance",
                    SubscriptionName = "whatsapp-message-sender",
                    ErpInstanceId = "ajk"
                },
                new TopicSubscriptionConfig
                {
                    TopicName = "hm-ivyliving-attendance",
                    SubscriptionName = "whatsapp-message-sender",
                    ErpInstanceId = "ivyliving"
                }
            ]
        };

        string? capturedUrl = null;
        string? capturedSecret = null;
        var handler = new StubHttpHandler((request, _) =>
        {
            capturedUrl = request.RequestUri?.ToString();
            capturedSecret = request.Headers.TryGetValues("X-Notification-Secret", out var values)
                ? values.FirstOrDefault()
                : null;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(handler);
        var service = new MessageTrackingService(settings, httpClient);

        await service.TrackMessageStatusAsync("hm-ajk-attendance", "msg-1", "Pending", null, null, null);

        Assert.Equal(ajkUrl, capturedUrl);
        Assert.Equal(ajkSecret, capturedSecret);

        await service.TrackMessageStatusAsync("hm-ivyliving-attendance", "msg-2", "Sent", null, null, DateTime.UtcNow);

        Assert.Equal(ivyUrl, capturedUrl);
        Assert.Equal(ivySecret, capturedSecret);
    }

    private static MessageTrackingSettings ValidTracking(string url) =>
        new() { ApiUrl = url, NotificationSecret = "secret" };

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request, cancellationToken));
    }
}
