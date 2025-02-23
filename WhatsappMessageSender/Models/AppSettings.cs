namespace WhatsappMessageSender.Models;

public class AppSettings
{
    public ServiceBusSettings ServiceBus { get; set; } = null!;
    public BlobStorageSettings BlobStorage { get; set; } = null!;
    public WhatsAppSettings WhatsApp { get; set; } = null!;
    public MessageTrackingSettings MessageTracking { get; set; } = null!;
}

public class ServiceBusSettings
{
    public string ConnectionString { get; set; } = null!;
    public List<QueueConfig> Queues { get; set; } = null!;
    public int MaxConcurrentCalls { get; set; }
}

public class QueueConfig
{
    public string QueueName { get; set; } = null!;
    public string ContainerName { get; set; } = null!;
}

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