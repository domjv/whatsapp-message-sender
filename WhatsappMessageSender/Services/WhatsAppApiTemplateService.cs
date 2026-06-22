using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WhatsappMessageSender.Models;

namespace WhatsappMessageSender.Services;

public class WhatsAppApiTemplateService : IWhatsAppApiTemplateService
{
    private static readonly Regex PlaceholderRegex = new(@"\{\{\s*(?<name>[A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly WhatsAppCloudApiSettings _settings;

    public WhatsAppApiTemplateService(HttpClient httpClient, IOptions<AppSettings> options)
    {
        _httpClient = httpClient;
        _settings = options.Value.WhatsAppCloudApi ?? new WhatsAppCloudApiSettings();
    }

    public async Task<SendMessageResult> SendTemplateMessageAsync(
        WhatsAppMessage message,
        WhatsAppApiChannelSettings channelSettings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.AccessToken))
            return new SendMessageResult { Success = false, Error = "WhatsAppCloudApi:AccessToken is required." };
        if (string.IsNullOrWhiteSpace(_settings.PhoneNumberId))
            return new SendMessageResult { Success = false, Error = "WhatsAppCloudApi:PhoneNumberId is required." };
        if (string.IsNullOrWhiteSpace(channelSettings.TemplateName))
            return new SendMessageResult { Success = false, Error = "WhatsApp API channel TemplateName is required." };

        var parameters = BuildTemplateParameters(message, channelSettings);
        object[] components = parameters.Count == 0
            ? Array.Empty<object>()
            : new object[]
            {
                new
                {
                    type = "body",
                    parameters = parameters.Select(value => new { type = "text", text = value }).ToArray()
                }
            };

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = NormalizePhoneNumber(message.Phone),
            type = "template",
            template = new
            {
                name = channelSettings.TemplateName,
                language = new { code = channelSettings.LanguageCode },
                components
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildMessagesUri());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);
        request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new SendMessageResult
            {
                Success = false,
                Error = $"WhatsApp Cloud API returned {(int)response.StatusCode}: {responseText}"
            };
        }

        return new SendMessageResult
        {
            Success = true,
            ProviderMessageId = TryReadProviderMessageId(responseText)
        };
    }

    internal static IReadOnlyList<string> BuildTemplateParameters(
        WhatsAppMessage message,
        WhatsAppApiChannelSettings channelSettings)
    {
        if (channelSettings.TemplateParameters is { Count: > 0 })
        {
            var explicitValues = ReadExplicitTemplateValues(message);
            return channelSettings.TemplateParameters
                .Select(name => ResolveParameterValue(name, explicitValues, message, channelSettings))
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(channelSettings.TemplateBody))
        {
            return ExtractValuesFromRenderedTemplate(channelSettings.TemplateBody!, message.Message)
                .Select(kv => kv.Value)
                .ToArray();
        }

        return [];
    }

    private Uri BuildMessagesUri()
    {
        var baseUrl = string.IsNullOrWhiteSpace(_settings.ApiBaseUrl)
            ? "https://graph.facebook.com"
            : _settings.ApiBaseUrl.TrimEnd('/');
        var version = string.IsNullOrWhiteSpace(_settings.ApiVersion) ? "v20.0" : _settings.ApiVersion.Trim('/');
        return new Uri($"{baseUrl}/{version}/{_settings.PhoneNumberId}/messages");
    }

    private static string NormalizePhoneNumber(string phoneNumber) =>
        phoneNumber.Trim().TrimStart('+').Replace(" ", string.Empty).Replace("-", string.Empty);

    private static Dictionary<string, string> ReadExplicitTemplateValues(WhatsAppMessage message)
    {
        if (message.TemplateParameters is { Count: > 0 })
            return new Dictionary<string, string>(message.TemplateParameters, StringComparer.OrdinalIgnoreCase);

        try
        {
            var obj = JObject.Parse(message.Message);
            var token = obj["template_parameters"] ?? obj["templateParameters"] ?? obj["parameters"];
            if (token is JObject values)
            {
                return values.Properties()
                    .ToDictionary(p => p.Name, p => p.Value.Type == JTokenType.Null ? string.Empty : p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveParameterValue(
        string name,
        IReadOnlyDictionary<string, string> explicitValues,
        WhatsAppMessage message,
        WhatsAppApiChannelSettings channelSettings)
    {
        if (explicitValues.TryGetValue(name, out var value))
            return value;

        if (!string.IsNullOrWhiteSpace(channelSettings.TemplateBody))
        {
            var extracted = ExtractValuesFromRenderedTemplate(channelSettings.TemplateBody!, message.Message);
            if (extracted.TryGetValue(name, out value))
                return value;
        }

        return string.Empty;
    }

    internal static Dictionary<string, string> ExtractValuesFromRenderedTemplate(string templateBody, string renderedMessage)
    {
        var names = new List<string>();
        var pattern = new StringBuilder("^");
        var index = 0;
        foreach (Match match in PlaceholderRegex.Matches(templateBody))
        {
            pattern.Append(Regex.Escape(templateBody[index..match.Index]));
            var name = match.Groups["name"].Value;
            names.Add(name);
            pattern.Append($"(?<{name}>.*?)");
            index = match.Index + match.Length;
        }

        pattern.Append(Regex.Escape(templateBody[index..]));
        pattern.Append("$");

        var normalizedRendered = renderedMessage.Replace("\r\n", "\n");
        var normalizedPattern = pattern.ToString().Replace("\\r\\n", "\\n");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var renderedMatch = Regex.Match(normalizedRendered, normalizedPattern, RegexOptions.Singleline);
        if (!renderedMatch.Success)
            return result;

        foreach (var name in names)
            result[name] = renderedMatch.Groups[name].Value;

        return result;
    }

    private static string? TryReadProviderMessageId(string responseText)
    {
        try
        {
            var obj = JObject.Parse(responseText);
            return obj["messages"]?.FirstOrDefault()?["id"]?.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
