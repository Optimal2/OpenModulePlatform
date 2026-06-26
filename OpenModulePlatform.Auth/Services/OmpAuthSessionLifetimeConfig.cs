using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace OpenModulePlatform.Auth.Services;

public sealed class OmpAuthSessionLifetimeConfig
{
    private OmpAuthSessionLifetimeConfig(
        int defaultMinutes,
        IReadOnlyDictionary<int, int> providerMinutes,
        bool usedWholeSettingFallback,
        int ignoredEntryCount)
    {
        DefaultMinutes = defaultMinutes;
        ProviderMinutes = providerMinutes;
        UsedWholeSettingFallback = usedWholeSettingFallback;
        IgnoredEntryCount = ignoredEntryCount;
    }

    public int DefaultMinutes { get; }

    public IReadOnlyDictionary<int, int> ProviderMinutes { get; }

    public bool UsedWholeSettingFallback { get; }

    public int IgnoredEntryCount { get; }

    public int ResolveMinutes(int? providerId)
    {
        if (providerId is > OmpAuthSessionLifetimeDefaults.FallbackProviderId &&
            ProviderMinutes.TryGetValue(providerId.Value, out var providerMinutes))
        {
            return providerMinutes;
        }

        return DefaultMinutes;
    }

    public static OmpAuthSessionLifetimeConfig Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return BuiltInFallback(usedWholeSettingFallback: true);
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return BuiltInFallback(usedWholeSettingFallback: true);
            }

            var defaultMinutes = OmpAuthSessionLifetimeDefaults.BuiltInDefaultMinutes;
            var providerMinutes = new Dictionary<int, int>();
            var ignoredEntryCount = 0;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!int.TryParse(
                        property.Name,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var providerId) ||
                    providerId < OmpAuthSessionLifetimeDefaults.FallbackProviderId)
                {
                    ignoredEntryCount++;
                    continue;
                }

                if (!TryReadMinutes(property.Value, out var minutes))
                {
                    ignoredEntryCount++;
                    continue;
                }

                if (providerId == OmpAuthSessionLifetimeDefaults.FallbackProviderId)
                {
                    defaultMinutes = minutes;
                }
                else
                {
                    providerMinutes[providerId] = minutes;
                }
            }

            return new OmpAuthSessionLifetimeConfig(
                defaultMinutes,
                new ReadOnlyDictionary<int, int>(providerMinutes),
                usedWholeSettingFallback: false,
                ignoredEntryCount);
        }
        catch (JsonException)
        {
            return BuiltInFallback(usedWholeSettingFallback: true);
        }
    }

    private static OmpAuthSessionLifetimeConfig BuiltInFallback(bool usedWholeSettingFallback)
        => new(
            OmpAuthSessionLifetimeDefaults.BuiltInDefaultMinutes,
            new ReadOnlyDictionary<int, int>(new Dictionary<int, int>()),
            usedWholeSettingFallback,
            ignoredEntryCount: 0);

    private static bool TryReadMinutes(JsonElement value, out int minutes)
    {
        long rawMinutes;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!value.TryGetInt64(out rawMinutes))
                {
                    minutes = 0;
                    return false;
                }

                break;
            case JsonValueKind.String:
                if (!long.TryParse(
                        value.GetString(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out rawMinutes))
                {
                    minutes = 0;
                    return false;
                }

                break;
            default:
                minutes = 0;
                return false;
        }

        if (rawMinutes <= 0)
        {
            minutes = 0;
            return false;
        }

        minutes = rawMinutes switch
        {
            < OmpAuthSessionLifetimeDefaults.MinimumMinutes => OmpAuthSessionLifetimeDefaults.MinimumMinutes,
            > OmpAuthSessionLifetimeDefaults.MaximumMinutes => OmpAuthSessionLifetimeDefaults.MaximumMinutes,
            _ => (int)rawMinutes
        };
        return true;
    }
}
