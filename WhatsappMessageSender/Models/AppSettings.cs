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
    /// <summary>
    /// Per-ERPNext-instance tracking endpoints. When a topic/stream resolves to an instance id,
    /// its <see cref="ErpInstanceConfig.MessageTracking"/> is used for delivery-status callbacks.
    /// </summary>
    public List<ErpInstanceConfig>? ErpInstances { get; set; }
    /// <summary>
    /// Optional fallback when a channel has no matching <see cref="ErpInstances"/> entry.
    /// </summary>
    public MessageTrackingSettings? MessageTracking { get; set; }
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
    /// <summary>
    /// Maximum number of due retry entries loaded from Redis per scheduler pass.
    /// Keeps large retry backlogs from being read into memory at once.
    /// </summary>
    public int RetrySchedulerBatchSize { get; set; } = 100;
    public List<StreamConfig> Streams { get; set; } = null!;
}

public class StreamConfig
{
    public string StreamName { get; set; } = null!;
    public string ContainerName { get; set; } = null!;
    /// <summary>
    /// ERPNext instance id for delivery-status callbacks. When omitted, resolved from
    /// <c>stream-{instanceId}</c> or <c>hm-{instanceId}-*</c> naming (longest id match first).
    /// </summary>
    public string? ErpInstanceId { get; set; }
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
    /// <summary>
    /// Set true when AMQP TCP is unstable in your environment (proxy/firewall/NAT idle timeouts).
    /// </summary>
    public bool UseWebSocketsTransport { get; set; }
}

public class TopicSubscriptionConfig
{
    public string TopicName { get; set; } = null!;
    public string SubscriptionName { get; set; } = null!;
    public string ContainerName { get; set; } = null!;
    /// <summary>
    /// ERPNext instance id for delivery-status callbacks. When omitted, resolved from
    /// <c>hm-{instanceId}-*</c> topic naming (longest id match first).
    /// </summary>
    public string? ErpInstanceId { get; set; }
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
    /// <summary>
    /// Maximum startup time to wait for WhatsApp Web to be logged in before broker consumption starts.
    /// Increase this for first-time QR linking.
    /// </summary>
    public int StartupWaitSeconds { get; set; } = 120;
}

public class ErpInstanceConfig
{
    public string Id { get; set; } = null!;
    public MessageTrackingSettings MessageTracking { get; set; } = null!;
}

public class MessageTrackingSettings
{
    public string ApiUrl { get; set; } = null!;
    public string NotificationSecret { get; set; } = null!;
}
