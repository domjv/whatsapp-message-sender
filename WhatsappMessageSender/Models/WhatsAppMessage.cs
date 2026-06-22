using Newtonsoft.Json;

namespace WhatsappMessageSender.Models;

public class WhatsAppMessage
{
    public required string Name { get; set; }
    public required string Phone { get; set; }
    public required string Message { get; set; }
    public string? AttachmentUrl { get; set; }
    [JsonProperty("message_id")]
    public string? MessageId { get; set; }
    public string? MessageName { get; set; }
    public Dictionary<string, string>? TemplateParameters { get; set; }
}
