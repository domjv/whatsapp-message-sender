using System.Text.Json;
using System.Text;
using WhatsappMessageSender.Models;
using Microsoft.Extensions.Options;

namespace WhatsappMessageSender.Services;

public class MessageTrackingService : IMessageTrackingService
{
    private readonly HttpClient _httpClient;
    private readonly MessageTrackingSettings _settings;

    public MessageTrackingService(IOptions<AppSettings> options)
    {
        var appSettings = options.Value
            ?? throw new InvalidOperationException("AppSettings configuration is missing.");

        // Fail fast with explicit configuration errors to avoid obscure
        // startup/runtime null-reference failures.
        _settings = appSettings.MessageTracking
            ?? throw new InvalidOperationException(
                "MessageTracking configuration is missing. Provide 'MessageTracking:ApiUrl' and 'MessageTracking:NotificationSecret'.");

        if (string.IsNullOrWhiteSpace(_settings.NotificationSecret))
            throw new InvalidOperationException(
                "MessageTracking:NotificationSecret is required and cannot be empty.");

        if (!Uri.TryCreate(_settings.ApiUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException(
                "MessageTracking:ApiUrl must be a valid absolute URL.");

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("X-Notification-Secret", _settings.NotificationSecret);
    }

    public async Task TrackMessageStatusAsync(string messageId, string status, string? error = null)
    {
        Console.WriteLine($"Message {messageId} status: {status} {(error != null ? $"Error: {error}" : "")}");

        object requestBody = status switch
        {
            "Sent" => new
            {
                message_id = messageId,
                status = "Sent",
                delivered_at = DateTime.UtcNow.ToString("o")
            },
            "Failed" => new
            {
                message_id = messageId,
                status = "Failed",
                error_message = string.IsNullOrWhiteSpace(error) ? "Unknown delivery failure." : error
            },
            _ => new
            {
                message_id = messageId,
                status = "Pending"
            }
        };

        try
        {
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_settings.ApiUrl, content);
            response.EnsureSuccessStatusCode();

            Console.WriteLine($"Successfully updated message status for {messageId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to update message status: {ex.Message}");
        }
    }
}
