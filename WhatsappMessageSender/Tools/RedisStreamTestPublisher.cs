using Newtonsoft.Json;
using StackExchange.Redis;
using WhatsappMessageSender.Models;

namespace WhatsappMessageSender.Tools;

/// <summary>
/// Utility class for publishing test messages to a Redis Stream.
///
/// Use this while the Frappe/Python producer is not yet available to verify
/// that <see cref="Services.RedisStreamProcessor"/> processes messages correctly.
///
/// Usage example (add a temporary call in Program.cs before starting the processor):
/// <code>
///   await RedisStreamTestPublisher.PublishAsync(
///       connectionString: "localhost:6379",
///       streamName: "stream-pleasntbiz",
///       message: new WhatsAppMessage
///       {
///           Name       = "MSG-TEST-001",
///           Phone      = "919876543210",
///           Message    = "Hello, this is a test message.",
///           MessageName = "MSG-TEST-001"
///       });
/// </code>
///
/// You can also run it via the `--publish` command-line flag (see Program.cs).
/// </summary>
public static class RedisStreamTestPublisher
{
    /// <summary>
    /// Publishes a single WhatsApp message to the specified Redis Stream using
    /// the preferred JSON <c>data</c>-field wire format.
    /// </summary>
    public static async Task PublishAsync(
        string connectionString,
        string streamName,
        WhatsAppMessage message,
        string messageType = "whatsapp")
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var db = redis.GetDatabase();

        var dataJson = JsonConvert.SerializeObject(message);

        var fields = new NameValueEntry[]
        {
            new("data",         dataJson),
            new("message_type", messageType),
            new("message_name", message.MessageName ?? message.Name),
            new("stream_name",  streamName)
        };

        var id = await db.StreamAddAsync(streamName, fields);
        Console.WriteLine($"[TestPublisher] Published message '{message.MessageName ?? message.Name}' to stream '{streamName}' with id '{id}'");
    }

    /// <summary>
    /// Publishes a single WhatsApp message to the specified Redis Stream using
    /// the individual-field wire format (fallback format).
    /// </summary>
    public static async Task PublishRawFieldsAsync(
        string connectionString,
        string streamName,
        string phone,
        string messageText,
        string messageName,
        string? attachmentUrl = null,
        string messageType = "whatsapp")
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var db = redis.GetDatabase();

        var fields = new List<NameValueEntry>
        {
            new("message_type",  messageType),
            new("message_name",  messageName),
            new("phone",         phone),
            new("message",       messageText),
            new("name",          messageName),
            new("stream_name",   streamName)
        };

        if (!string.IsNullOrEmpty(attachmentUrl))
            fields.Add(new NameValueEntry("attachment_url", attachmentUrl));

        var id = await db.StreamAddAsync(streamName, [.. fields]);
        Console.WriteLine($"[TestPublisher] Published raw message '{messageName}' to stream '{streamName}' with id '{id}'");
    }

    /// <summary>
    /// Publishes multiple test messages useful for exercising all processor scenarios.
    /// </summary>
    public static async Task PublishScenarioSuiteAsync(
        string connectionString,
        string streamName)
    {
        Console.WriteLine($"[TestPublisher] Publishing scenario suite to '{streamName}' ...");

        // 1. Happy path
        await PublishAsync(connectionString, streamName, new WhatsAppMessage
        {
            Name        = "MSG-HAPPY-001",
            Phone       = "919000000001",
            Message     = "Happy-path test message.",
            MessageName = "MSG-HAPPY-001"
        });

        // 2. With attachment (you need a real blob URL for end-to-end; here we use a dummy)
        await PublishAsync(connectionString, streamName, new WhatsAppMessage
        {
            Name          = "MSG-ATTACH-001",
            Phone         = "919000000002",
            Message       = "Message with attachment.",
            MessageName   = "MSG-ATTACH-001",
            AttachmentUrl = "https://example.blob.core.windows.net/container/file.pdf"
        });

        // 3. Missing message_name → should dead-letter
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var db = redis.GetDatabase();
        await db.StreamAddAsync(streamName, new NameValueEntry[]
        {
            new("message_type", "whatsapp"),
            new("phone",        "919000000003"),
            new("message",      "No message_name field"),
            new("stream_name",  streamName)
        });
        Console.WriteLine($"[TestPublisher] Published missing-name message to '{streamName}'");

        // 4. Unsupported type → should dead-letter
        await db.StreamAddAsync(streamName, new NameValueEntry[]
        {
            new("message_type", "sms"),
            new("message_name", "MSG-SMS-001"),
            new("phone",        "919000000004"),
            new("message",      "SMS is not supported"),
            new("stream_name",  streamName)
        });
        Console.WriteLine($"[TestPublisher] Published unsupported-type message to '{streamName}'");

        Console.WriteLine("[TestPublisher] Scenario suite published.");
    }
}
