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
    private readonly MessageTrackingSettings _settings;

    public MessageTrackingService(IOptions<AppSettings> options)
    {
        var appSettings = options.Value
            ?? throw new InvalidOperationException("AppSettings configuration is missing.");

        _settings = appSettings.MessageTracking
            ?? throw new InvalidOperationException(
                "MessageTracking configuration is missing. Provide 'MessageTracking:ApiUrl' and 'MessageTracking:NotificationSecret'.");

        if (string.IsNullOrWhiteSpace(_settings.NotificationSecret))
            throw new InvalidOperationException(
                "MessageTracking:NotificationSecret is required and cannot be empty.");

        if (!Uri.TryCreate(_settings.ApiUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException(
                "MessageTracking:ApiUrl must be a valid absolute URL.");

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.Add("X-Notification-Secret", _settings.NotificationSecret);
    }

    /// <summary>
    /// MySQL <c>DATETIME</c> / Frappe fields typically reject ISO literals with <c>T</c>/<c>Z</c>.
    /// Use UTC wall-clock in <c>yyyy-MM-dd HH:mm:ss</c> form.
    /// </summary>
    internal static string FormatDeliveredAtUtc(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public async Task TrackMessageStatusAsync(
        string messageId,
        string status,
        string? error,
        string? providerMessageId,
        DateTime? deliveredAtUtc)
    {
        Console.WriteLine($"Message {messageId} status: {status} {(error != null ? $"Error: {error}" : "")}");

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
            using var response = await _httpClient.PostAsync(_settings.ApiUrl, content);
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
