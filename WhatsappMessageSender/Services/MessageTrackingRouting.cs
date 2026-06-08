using WhatsappMessageSender.Models;

namespace WhatsappMessageSender.Services;

/// <summary>
/// Resolves ERP instance and per-channel MessageTracking settings from <see cref="AppSettings"/>.
/// </summary>
public static class MessageTrackingRouting
{
    public static bool IsValidTrackingSettings(MessageTrackingSettings? settings) =>
        settings != null
        && !string.IsNullOrWhiteSpace(settings.NotificationSecret)
        && Uri.TryCreate(settings.ApiUrl, UriKind.Absolute, out _);

    /// <summary>
    /// Resolves instance id from explicit config or channel naming conventions.
    /// </summary>
    public static string? ResolveErpInstanceId(
        string channelName,
        string? explicitErpInstanceId,
        IReadOnlyList<ErpInstanceConfig> erpInstances)
    {
        if (!string.IsNullOrWhiteSpace(explicitErpInstanceId))
            return explicitErpInstanceId.Trim();

        if (erpInstances.Count == 0)
            return null;

        foreach (var instance in erpInstances.OrderByDescending(i => i.Id.Length))
        {
            if (channelName.StartsWith($"hm-{instance.Id}-", StringComparison.OrdinalIgnoreCase))
                return instance.Id;
        }

        foreach (var instance in erpInstances.OrderByDescending(i => i.Id.Length))
        {
            if (channelName.Equals($"stream-{instance.Id}", StringComparison.OrdinalIgnoreCase))
                return instance.Id;
        }

        return null;
    }

    public static Dictionary<string, MessageTrackingSettings> BuildInstanceTrackingMap(
        IReadOnlyList<ErpInstanceConfig>? erpInstances,
        ICollection<string> errors)
    {
        var map = new Dictionary<string, MessageTrackingSettings>(StringComparer.OrdinalIgnoreCase);

        if (erpInstances == null)
            return map;

        foreach (var instance in erpInstances)
        {
            if (string.IsNullOrWhiteSpace(instance.Id))
            {
                errors.Add("ErpInstances entry is missing Id.");
                continue;
            }

            if (map.ContainsKey(instance.Id))
            {
                errors.Add($"Duplicate ErpInstances Id '{instance.Id}'.");
                continue;
            }

            if (!IsValidTrackingSettings(instance.MessageTracking))
            {
                errors.Add(
                    $"ErpInstances '{instance.Id}' must include a valid MessageTracking ApiUrl and NotificationSecret.");
                continue;
            }

            map[instance.Id] = instance.MessageTracking;
        }

        return map;
    }

    public static Dictionary<string, MessageTrackingSettings> BuildChannelTrackingMap(
        AppSettings settings,
        ICollection<string> errors)
    {
        var erpInstances = settings.ErpInstances ?? [];
        var instanceTracking = BuildInstanceTrackingMap(erpInstances, errors);
        var fallback = IsValidTrackingSettings(settings.MessageTracking)
            ? settings.MessageTracking
            : null;

        var channelMap = new Dictionary<string, MessageTrackingSettings>(StringComparer.OrdinalIgnoreCase);

        void AddChannel(string channelName, string? explicitErpInstanceId)
        {
            if (string.IsNullOrWhiteSpace(channelName))
                return;

            if (channelMap.ContainsKey(channelName))
                return;

            var instanceId = ResolveErpInstanceId(channelName, explicitErpInstanceId, erpInstances);
            MessageTrackingSettings? tracking = null;

            if (instanceId != null)
            {
                if (instanceTracking.TryGetValue(instanceId, out var instanceSettings))
                    tracking = instanceSettings;
                else
                    errors.Add($"Channel '{channelName}' references unknown ErpInstanceId '{instanceId}'.");
            }
            else
            {
                tracking = fallback;
            }

            if (!IsValidTrackingSettings(tracking))
                errors.Add($"Channel '{channelName}' has no resolvable MessageTracking configuration.");
            else
                channelMap[channelName] = tracking!;
        }

        if (settings.ServiceBus?.Topics != null)
        {
            foreach (var topic in settings.ServiceBus.Topics)
                AddChannel(topic.TopicName, topic.ErpInstanceId);
        }

        if (settings.Redis?.Streams != null)
        {
            foreach (var stream in settings.Redis.Streams)
                AddChannel(stream.StreamName, stream.ErpInstanceId);
        }

        return channelMap;
    }

    public static bool ValidateAppSettings(AppSettings settings, out string errorMessage)
    {
        var errors = new List<string>();
        _ = BuildChannelTrackingMap(settings, errors);

        if (errors.Count == 0)
        {
            errorMessage = string.Empty;
            return true;
        }

        errorMessage = string.Join(" ", errors.Distinct());
        return false;
    }
}
