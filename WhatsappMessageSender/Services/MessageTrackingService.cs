using System.Text.Json;
using System.Text;
using WhatsappMessageSender.Models;
using Microsoft.Extensions.Options;

namespace WhatsappMessageSender.Services;

public class MessageTrackingService
{
    private static HttpClient? _httpClient;
    private static MessageTrackingSettings? _settings;

    public static void Initialize(IOptions<AppSettings> options)
    {
        _settings = options.Value.MessageTracking;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"token {_settings.AuthToken}");
    }

    public static async Task TrackMessageStatusAsync(string messageId, string status, string? error = null)
    {
        if (_httpClient == null || _settings == null)
        {
            throw new InvalidOperationException("MessageTrackingService has not been initialized");
        }

        Console.WriteLine($"Message {messageId} status: {status} {(error != null ? $"Error: {error}" : "")}");

        var requestBody = new
        {
            message_name = messageId,
            message_status = status,
            message_sent_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            error_message = error
        };

        try
        {
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.ExpectContinue = false;

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