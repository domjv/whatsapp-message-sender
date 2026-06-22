using WhatsappMessageSender.Models;

namespace WhatsappMessageSender.Services;

public class NullWhatsAppApiTemplateService : IWhatsAppApiTemplateService
{
    public Task<SendMessageResult> SendTemplateMessageAsync(
        WhatsAppMessage message,
        WhatsAppApiChannelSettings channelSettings,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SendMessageResult
        {
            Success = false,
            Error = "WhatsApp API template service is not configured."
        });
}
