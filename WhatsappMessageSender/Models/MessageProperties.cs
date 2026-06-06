namespace WhatsappMessageSender.Models;

public class MessageProperties
{
    public required string MessageType { get; set; }
    /// <summary>
    /// The channel identifier: the queue name when using Service Bus,
    /// or the stream name when using Redis Streams.
    /// </summary>
    public required string ChannelName { get; set; }
    public required string MessageName { get; set; }
}
