namespace WhatsappMessageSender.Services;

/// <summary>
/// No-op limiter for tests or when throttling is not wired.
/// </summary>
public sealed class NullWhatsAppSendRateLimiter : IWhatsAppSendRateLimiter
{
    public static readonly NullWhatsAppSendRateLimiter Instance = new();

    public Task WaitForSendSlotAsync(int dispatchPriority, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void NotifySuccessfulSendIfThrottled(int dispatchPriority)
    {
    }
}
