namespace WhatsappMessageSender.Services;

/// <summary>
/// Abstraction over the WhatsApp sending mechanism so that the
/// concrete Selenium-based implementation can be replaced by a mock
/// in unit tests.
/// </summary>
public interface IWhatsAppService
{
    Task<SendMessageResult> SendMessageAsync(string phoneNumber, string textMessage, string? filePath);
}
