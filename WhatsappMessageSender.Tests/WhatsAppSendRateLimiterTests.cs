using WhatsappMessageSender.Services;

namespace WhatsappMessageSender.Tests;

public class WhatsAppSendRateLimiterTests
{
    [Fact]
    public async Task HighPriority_DoesNotBlock()
    {
        var limiter = new WhatsAppSendRateLimiter(highPriorityLessThan: 10, maxSendsPerMinute: 1, enabled: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitForSendSlotAsync(0);
        await limiter.WaitForSendSlotAsync(9);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500);
    }

    [Fact]
    public async Task Disabled_BypassesCap()
    {
        var limiter = new WhatsAppSendRateLimiter(highPriorityLessThan: 10, maxSendsPerMinute: 1, enabled: false);
        await limiter.WaitForSendSlotAsync(100);
        limiter.NotifySuccessfulSendIfThrottled(100);
        await limiter.WaitForSendSlotAsync(100);
    }

    [Fact]
    public async Task ThrottledTraffic_ReservesSlotBeforeSendToPreventConcurrentOversubscription()
    {
        var limiter = new WhatsAppSendRateLimiter(highPriorityLessThan: 10, maxSendsPerMinute: 1, enabled: true);

        await limiter.WaitForSendSlotAsync(100);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TaskCanceledException>(() => limiter.WaitForSendSlotAsync(100, cts.Token));
    }
}
