namespace WhatsappMessageSender.Models;

public class MessageProperties
{
    public required string MessageType { get; set; }
    public required string QueueName { get; set; }
    public required string MessageName { get; set; }
} 