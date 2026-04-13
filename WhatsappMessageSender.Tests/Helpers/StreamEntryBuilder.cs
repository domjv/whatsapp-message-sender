using Newtonsoft.Json;
using StackExchange.Redis;
using WhatsappMessageSender.Models;

namespace WhatsappMessageSender.Tests.Helpers;

/// <summary>
/// Factory helpers for building <see cref="StreamEntry"/> objects used in
/// <see cref="RedisStreamProcessorTests"/>.
/// </summary>
public static class StreamEntryBuilder
{
    private static string NextId() =>
        $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Random.Shared.Next(1, int.MaxValue)}";

    /// <summary>Builds a valid WhatsApp stream entry using the JSON data field.</summary>
    public static StreamEntry ValidWhatsAppMessage(
        string messageName = "MSG-001",
        string phone = "919876543210",
        string message = "Test message",
        string? attachmentUrl = null,
        string streamName = "stream-test",
        int retryCount = 0)
    {
        var msg = new WhatsAppMessage
        {
            Name        = messageName,
            Phone       = phone,
            Message     = message,
            AttachmentUrl = attachmentUrl,
            MessageName = messageName
        };

        var fields = new List<NameValueEntry>
        {
            new("data",         JsonConvert.SerializeObject(msg)),
            new("message_type", "whatsapp"),
            new("message_name", messageName),
            new("stream_name",  streamName)
        };

        if (retryCount > 0)
            fields.Add(new NameValueEntry("retry_count", retryCount.ToString()));

        return new StreamEntry(NextId(), [.. fields]);
    }

    /// <summary>Builds a stream entry using individual fields (fallback wire format).</summary>
    public static StreamEntry ValidWhatsAppMessageRawFields(
        string messageName = "MSG-RAW-001",
        string phone = "919876543210",
        string message = "Test message raw",
        string streamName = "stream-test")
    {
        return new StreamEntry(NextId(), new NameValueEntry[]
        {
            new("message_type", "whatsapp"),
            new("message_name", messageName),
            new("phone",        phone),
            new("message",      message),
            new("name",         messageName),
            new("stream_name",  streamName)
        });
    }

    /// <summary>Builds a stream entry with no message_name field.</summary>
    public static StreamEntry MissingMessageNameEntry(string streamName = "stream-test")
    {
        return new StreamEntry(NextId(), new NameValueEntry[]
        {
            new("message_type", "whatsapp"),
            new("phone",        "919876543210"),
            new("message",      "No name"),
            new("stream_name",  streamName)
        });
    }

    /// <summary>Builds a stream entry with an unsupported message type.</summary>
    public static StreamEntry UnsupportedTypeEntry(
        string messageName = "MSG-SMS-001",
        string streamName = "stream-test")
    {
        return new StreamEntry(NextId(), new NameValueEntry[]
        {
            new("message_type", "sms"),
            new("message_name", messageName),
            new("phone",        "919876543210"),
            new("message",      "Not whatsapp"),
            new("stream_name",  streamName)
        });
    }

    /// <summary>Builds a stream entry with an invalid JSON data field.</summary>
    public static StreamEntry InvalidJsonDataEntry(
        string messageName = "MSG-BAD-001",
        string streamName = "stream-test")
    {
        return new StreamEntry(NextId(), new NameValueEntry[]
        {
            new("data",         "{ not valid json %%% }"),
            new("message_type", "whatsapp"),
            new("message_name", messageName),
            new("stream_name",  streamName)
        });
    }

    /// <summary>Builds a stream entry that has exhausted retries.</summary>
    public static StreamEntry ExhaustedRetriesEntry(
        string messageName = "MSG-EXHAUSTED-001",
        string streamName = "stream-test")
    {
        return ValidWhatsAppMessage(
            messageName: messageName,
            streamName: streamName,
            retryCount: RetrySettings.MaxRetries + 1);
    }
}
