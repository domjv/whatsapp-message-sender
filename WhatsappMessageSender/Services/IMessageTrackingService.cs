namespace WhatsappMessageSender.Services;

/// <summary>
/// Abstraction over the Frappe message-status tracking API so that
/// the concrete HTTP implementation can be replaced by a mock in
/// unit tests.
/// </summary>
public interface IMessageTrackingService
{
    Task TrackMessageStatusAsync(string messageId, string status, string? error = null);
}
