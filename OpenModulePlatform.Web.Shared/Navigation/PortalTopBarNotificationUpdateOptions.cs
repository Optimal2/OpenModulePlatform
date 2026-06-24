using OpenModulePlatform.Web.Shared.Options;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Navigation;

public sealed class PortalTopBarNotificationUpdateOptions
{
    public const string ConfigCategory = "portal";
    public const string ModeConfigSetting = "notificationUpdateMode";
    public const string PollIntervalSecondsConfigSetting = "notificationPollIntervalSeconds";

    public const string ManualMode = "manual";
    public const string PollMode = "poll";
    public const string PushMode = "push";

    public const int DefaultPollIntervalSeconds = 60;
    public const int MinPollIntervalSeconds = 10;
    public const int MaxPollIntervalSeconds = 3600;

    private static readonly HashSet<string> ValidModes =
    [
        ManualMode,
        PollMode,
        PushMode
    ];

    public string Mode { get; init; } = PollMode;

    public int PollIntervalSeconds { get; init; } = DefaultPollIntervalSeconds;

    public bool UsesPolling => string.Equals(Mode, PollMode, StringComparison.OrdinalIgnoreCase);

    public static PortalTopBarNotificationUpdateOptions FromConfig(
        string? modeValue,
        string? pollIntervalSecondsValue)
        => new()
        {
            Mode = NormalizeMode(modeValue),
            PollIntervalSeconds = NormalizePollIntervalSeconds(pollIntervalSecondsValue)
        };

    public static PortalTopBarNotificationUpdateOptions FromWebAppOptions(TopBarPollingOptions? options)
    {
        var mode = options?.Enabled == false ? ManualMode : PollMode;
        var intervalText = options?.VisibleIntervalSeconds.ToString(CultureInfo.InvariantCulture);

        return new()
        {
            Mode = mode,
            PollIntervalSeconds = NormalizePollIntervalSeconds(intervalText)
        };
    }

    private static string NormalizeMode(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? PollMode
            : value.Trim().ToLowerInvariant();

        return ValidModes.Contains(normalized) ? normalized : PollMode;
    }

    private static int NormalizePollIntervalSeconds(string? value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return DefaultPollIntervalSeconds;
        }

        return seconds is >= MinPollIntervalSeconds and <= MaxPollIntervalSeconds
            ? seconds
            : DefaultPollIntervalSeconds;
    }
}
