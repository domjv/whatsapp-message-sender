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
                "MessageTracking configuration is missing. Provide 'MessageTracking:ApiUrl' and 'MessageTracking:AuthToken'.");

        if (string.IsNullOrWhiteSpace(_settings.AuthToken))
            throw new InvalidOperationException(
                "MessageTracking:AuthToken is required and cannot be empty.");

        if (!Uri.TryCreate(_settings.ApiUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException(
                "MessageTracking:ApiUrl must be a valid absolute URL.");

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"token {_settings.AuthToken}");
    }

    public async Task TrackMessageStatusAsync(string messageId, string status, string? error = null)
    {
        Console.WriteLine($"Message {messageId} status: {status} {(error != null ? $"Error: {error}" : "")}");

        var requestBody = new
        {
            message_name = messageId,
            message_status = status,
            message_sent_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            error_message = error ?? ""
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
