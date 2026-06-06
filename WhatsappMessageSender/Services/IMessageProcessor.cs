namespace WhatsappMessageSender.Services;

/// <summary>
/// Abstraction for a message-broker processor so that both the
/// Azure Service Bus and the Redis Streams implementations can be
/// selected at runtime based on configuration.
/// </summary>
public interface IMessageProcessor : IDisposable
{
    void StartProcessing();
    Task CloseAsync();
}
