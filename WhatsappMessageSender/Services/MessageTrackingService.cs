using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhatsappMessageSender.Models;
using Microsoft.Extensions.Options;

namespace WhatsappMessageSender.Services;

/// <summary>
/// HTTP client for Frappe delivery-status callbacks. Implements <see cref="IDisposable"/> so the
/// underlying <see cref="HttpClient"/> is disposed when the host shuts down.
/// </summary>
public sealed class MessageTrackingService : IMessageTrackingService, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyDictionary<string, MessageTrackingSettings> _channelTracking;
    private readonly MessageTrackingSettings? _fallbackTracking;

    public MessageTrackingService(IOptions<AppSettings> options)
        : this(options.Value, httpClient: null)
    {
    }

    internal MessageTrackingService(AppSettings appSettings, HttpClient? httpClient)
    {
        if (appSettings == null)
            throw new InvalidOperationException("AppSettings configuration is missing.");

        var errors = new List<string>();
        _channelTracking = MessageTrackingRouting.BuildChannelTrackingMap(appSettings, errors);
        _fallbackTracking = MessageTrackingRouting.IsValidTrackingSettings(appSettings.MessageTracking)
            ? appSettings.MessageTracking
            : null;

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors.Distinct()));

        if (_channelTracking.Count == 0 && _fallbackTracking == null)
            throw new InvalidOperationException(
                "MessageTracking configuration is missing. Provide ErpInstances and/or " +
                "MessageTracking:ApiUrl and MessageTracking:NotificationSecret.");

        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// MySQL <c>DATETIME</c> / Frappe fields typically reject ISO literals with <c>T</c>/<c>Z</c>.
    /// Use UTC wall-clock in <c>yyyy-MM-dd HH:mm:ss</c> form.
    /// </summary>
    internal static string FormatDeliveredAtUtc(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public async Task TrackMessageStatusAsync(
        string channelName,
        string messageId,
        string status,
        string? error,
        string? providerMessageId,
        DateTime? deliveredAtUtc)
    {
        Console.WriteLine($"Message {messageId} status: {status} {(error != null ? $"Error: {error}" : "")}");

        var tracking = ResolveTrackingSettings(channelName);

        object requestBody = status switch
        {
            "Sent" => new SentPayload
            {
                MessageId = messageId,
                Status = "Sent",
                DeliveredAt = FormatDeliveredAtUtc(deliveredAtUtc ?? DateTime.UtcNow),
                ProviderMessageId = providerMessageId
            },
            "Failed" => new FailedPayload
            {
                MessageId = messageId,
                Status = "Failed",
                ErrorMessage = string.IsNullOrWhiteSpace(error) ? "Unknown delivery failure." : error,
                ProviderMessageId = providerMessageId
            },
            _ => new PendingPayload
            {
                MessageId = messageId,
                Status = "Pending"
            }
        };

        try
        {
            var json = JsonSerializer.Serialize(requestBody, SerializerOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, tracking.ApiUrl) { Content = content };
            request.Headers.Add("X-Notification-Secret", tracking.NotificationSecret);

            using var response = await _httpClient.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Successfully updated message status for {messageId}");
                return;
            }

            LogHttpFailure(response.StatusCode, responseText, messageId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to update message status: {ex.Message}");
        }
    }

    private MessageTrackingSettings ResolveTrackingSettings(string channelName)
    {
        if (!string.IsNullOrWhiteSpace(channelName)
            && _channelTracking.TryGetValue(channelName, out var channelSettings))
        {
            return channelSettings;
        }

        if (_fallbackTracking != null)
            return _fallbackTracking;

        throw new InvalidOperationException(
            $"No message tracking configured for channel '{channelName}'.");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static void LogHttpFailure(HttpStatusCode status, string responseText, string messageId)
    {
        var snippet = responseText.Length > 500 ? responseText[..500] + "…" : responseText;
        Console.WriteLine(
            $"Failed to update message status for {messageId}: HTTP {(int)status} {status}. Body: {snippet}");

        if (status == HttpStatusCode.NotFound &&
            responseText.Contains("message_id not found", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                "Hint: backend did not find this message_id — use the UUID from the published JSON " +
                "`message_id` field (see docs).");
        }

        if (responseText.Contains("Incorrect datetime value", StringComparison.OrdinalIgnoreCase) &&
            responseText.Contains("sent_at", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                "Hint: backend/MySQL rejected delivered_at format — worker now sends UTC as yyyy-MM-dd HH:mm:ss.");
        }
    }

    private sealed class SentPayload
    {
        public string MessageId { get; set; } = null!;
        public string Status { get; set; } = "Sent";
        public string? DeliveredAt { get; set; }
        public string? ProviderMessageId { get; set; }
    }

    private sealed class FailedPayload
    {
        public string MessageId { get; set; } = null!;
        public string Status { get; set; } = "Failed";
        public string ErrorMessage { get; set; } = null!;
        public string? ProviderMessageId { get; set; }
    }

    private sealed class PendingPayload
    {
        public string MessageId { get; set; } = null!;
        public string Status { get; set; } = "Pending";
    }
}
