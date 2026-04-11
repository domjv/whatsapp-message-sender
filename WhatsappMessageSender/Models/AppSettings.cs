namespace WhatsappMessageSender.Models;

public class AppSettings
{
    public RedisSettings Redis { get; set; } = null!;
    public BlobStorageSettings BlobStorage { get; set; } = null!;
    public WhatsAppSettings WhatsApp { get; set; } = null!;
    public MessageTrackingSettings MessageTracking { get; set; } = null!;
}

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