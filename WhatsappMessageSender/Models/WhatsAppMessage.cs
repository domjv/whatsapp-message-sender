namespace WhatsappMessageSender.Models;

public class WhatsAppMessage
{
    public required string Name { get; set; }
    public required string Phone { get; set; }
    public required string Message { get; set; }
    public string? AttachmentUrl { get; set; }
}
