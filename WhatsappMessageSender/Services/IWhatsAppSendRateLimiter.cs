namespace WhatsappMessageSender.Services;

/// <summary>
/// Throttles WhatsApp sends for lower-priority traffic while allowing high-priority
/// messages (small configured priority values) to bypass the cap.
/// </summary>
public interface IWhatsAppSendRateLimiter
{
    /// <summary>
    /// Blocks until a send slot is available for the given dispatch priority, or returns
    /// immediately when the message is high priority or rate limiting is disabled.
    /// Must be called under the same mutual exclusion as <see cref="IWhatsAppService.SendMessageAsync"/>
    /// when multiple workers could send in parallel (e.g. Redis Streams).
    /// </summary>
    Task WaitForSendSlotAsync(int dispatchPriority, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a successful send toward the per-minute cap when the message was subject to throttling.
    /// </summary>
    void NotifySuccessfulSendIfThrottled(int dispatchPriority);
}
