using WhatsappMessageSender.Models;

namespace WhatsappMessageSender.Tests;

public class RetrySettingsTests
{
    [Fact]
    public void MaxRetries_Is10()
    {
        Assert.Equal(10, RetrySettings.MaxRetries);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 960)]
    [InlineData(7, 1920)]
    public void GetDelayForRetry_ExponentialBackoff(int deliveryCount, double expectedSeconds)
    {
        var delay = RetrySettings.GetDelayForRetry(deliveryCount);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(20)]
    public void GetDelayForRetry_CapsAt3600Seconds(int deliveryCount)
    {
        var delay = RetrySettings.GetDelayForRetry(deliveryCount);
        Assert.Equal(TimeSpan.FromSeconds(3600), delay);
    }
}
