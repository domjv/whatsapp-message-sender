namespace WhatsappMessageSender.Services;

/// <summary>
/// Abstraction over the Frappe message-status tracking API so that
/// the concrete HTTP implementation can be replaced by a mock in
/// unit tests.
/// </summary>
public interface IMessageTrackingService
{
    /// <param name="messageId">Backend <c>message_id</c> (UUID from published payload when available).</param>
    /// <param name="providerMessageId">Optional provider reference (e.g. WhatsApp <c>wamid.*</c>).</param>
    /// <param name="deliveredAtUtc">Optional confirmation time for <c>Sent</c> (UTC).</param>
    Task TrackMessageStatusAsync(
        string messageId,
        string status,
        string? error,
        string? providerMessageId,
        DateTime? deliveredAtUtc);
}
