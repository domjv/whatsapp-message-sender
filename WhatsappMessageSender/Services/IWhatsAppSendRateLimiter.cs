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
    /// Should be called before entering the exclusive <see cref="IWhatsAppService.SendMessageAsync"/>
    /// section so throttled low-priority traffic does not block high-priority sends.
    /// </summary>
    Task WaitForSendSlotAsync(int dispatchPriority, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called after a successful send when the message was subject to throttling.
    /// Current implementation reserves throttled slots before send to avoid concurrent oversubscription.
    /// </summary>
    void NotifySuccessfulSendIfThrottled(int dispatchPriority);
}
