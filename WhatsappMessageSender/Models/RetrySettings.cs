namespace WhatsappMessageSender.Models;

public static class RetrySettings
{
    public const int MaxRetries = 10;
    private const int BaseDelaySeconds = 30;
    
    public static TimeSpan GetDelayForRetry(int deliveryCount)
    {
        var delaySeconds = BaseDelaySeconds * Math.Pow(2, deliveryCount - 1);
        
        delaySeconds = Math.Min(delaySeconds, 3600);
        
        return TimeSpan.FromSeconds(delaySeconds);
    }
} 