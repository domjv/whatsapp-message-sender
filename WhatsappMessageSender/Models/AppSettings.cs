namespace WhatsappMessageSender.Models;

public class AppSettings
{
    /// <summary>
    /// Selects the active message broker. Valid values: "Redis" (default), "ServiceBus".
    /// Only the matching settings block needs to be populated.
    /// </summary>
    public string MessageBroker { get; set; } = "Redis";

    public RedisSettings? Redis { get; set; }
    public ServiceBusSettings? ServiceBus { get; set; }
    public BlobStorageSettings BlobStorage { get; set; } = null!;
    public WhatsAppSettings WhatsApp { get; set; } = null!;
    public MessageTrackingSettings MessageTracking { get; set; } = null!;
}

// ---------------------------------------------------------------------------
// Redis Streams settings
// ---------------------------------------------------------------------------

public class RedisSettings
{
    public string ConnectionString { get; set; } = null!;
    public string ConsumerGroup { get; set; } = "whatsapp-sender";
    public string ConsumerName { get; set; } = Environment.MachineName;
    public int MaxConcurrentCalls { get; set; } = 2;
    public int PendingMessageTimeoutSeconds { get; set; } = 300;
    public List<StreamConfig> Streams { get; set; } = null!;
}

public class StreamConfig
{
    public string StreamName { get; set; } = null!;
    public string ContainerName { get; set; } = null!;
}

// ---------------------------------------------------------------------------
// Azure Service Bus settings
// ---------------------------------------------------------------------------

public class ServiceBusSettings
{
    public string ConnectionString { get; set; } = null!;
    public List<QueueConfig> Queues { get; set; } = null!;
    public int MaxConcurrentCalls { get; set; } = 2;
}

public class QueueConfig
{
    public string QueueName { get; set; } = null!;
    public string ContainerName { get; set; } = null!;
}

// ---------------------------------------------------------------------------
// Shared settings
// ---------------------------------------------------------------------------

public class BlobStorageSettings
{
    public string ConnectionString { get; set; } = null!;
}

public class WhatsAppSettings
{
    public string ProfilePath { get; set; } = null!;
    public string ChromeDriverPath { get; set; } = null!;
}

public class MessageTrackingSettings
{
    public string ApiUrl { get; set; } = null!;
    public string AuthToken { get; set; } = null!;
}
