using WhatsappMessageSender.Models;

namespace WhatsappMessageSender.Services;

public interface IWhatsAppApiTemplateService
{
    Task<SendMessageResult> SendTemplateMessageAsync(
        WhatsAppMessage message,
        WhatsAppApiChannelSettings channelSettings,
        CancellationToken cancellationToken = default);
}
