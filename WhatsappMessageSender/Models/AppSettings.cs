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
    /// <summary>
    /// Optional. When null or <see cref="BlobStorageSettings.ConnectionString"/> is empty/invalid,
    /// attachment downloads are skipped (messages without attachments still work).
    /// </summary>
    public BlobStorageSettings? BlobStorage { get; set; }
    public WhatsAppSettings WhatsApp { get; set; } = null!;
    /// <summary>
    /// Optional. When null, defaults apply: priorities &lt; 10 are unlimited, others capped at 20/minute.
    /// </summary>
    public WhatsAppSendRateLimitSettings? WhatsAppSendRateLimit { get; set; }
    public MessageTrackingSettings MessageTracking { get; set; } = null!;
}

/// <summary>
/// Caps successful WhatsApp sends per minute for lower-priority topics/streams.
/// </summary>
public class WhatsAppSendRateLimitSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Dispatch priorities strictly less than this value bypass the per-minute cap (treated as high priority).
    /// Default 10 → priorities 0–9 are immediate; 10+ share the sliding window cap.
    /// </summary>
    public int HighPriorityLessThan { get; set; } = 10;
    public int MaxSendsPerMinute { get; set; } = 20;
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
    /// <summary>
    /// Same semantics as Service Bus topic <see cref="TopicSubscriptionConfig.Priority"/>; used for send throttling.
    /// </summary>
    public int Priority { get; set; } = 100;
}

// ---------------------------------------------------------------------------
// Azure Service Bus settings
// ---------------------------------------------------------------------------

public class ServiceBusSettings
{
    public string ConnectionString { get; set; } = null!;
    public List<TopicSubscriptionConfig> Topics { get; set; } = [];
    public int MaxConcurrentCalls { get; set; } = 4;
    public int MaxAutoRenewDurationMinutes { get; set; } = 10;
}

public class TopicSubscriptionConfig
{
    public string TopicName { get; set; } = null!;
    public string SubscriptionName { get; set; } = null!;
    public string ContainerName { get; set; } = null!;
    /// <summary>
    /// When true, uses <c>RegisterSessionHandler</c> because the Azure subscription has sessions enabled.
    /// When false, uses <c>RegisterMessageHandler</c> for a non-session subscription.
    /// </summary>
    public bool RequiresSession { get; set; }
    /// <summary>
    /// Lower values are processed first. Use 0 for highest priority topics.
    /// </summary>
    public int Priority { get; set; } = 100;
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
    public string NotificationSecret { get; set; } = null!;
}
