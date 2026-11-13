namespace OpenModulePlatform.Web.ContextGuardModule.Options;

public sealed class ContextGuardOptions
{
    public const string SectionName = "ContextGuard";

    public int HandoffTtlSeconds { get; set; } = 60;
    public int HeartbeatIntervalSeconds { get; set; } = 5;
    public int HeartbeatTimeoutSeconds { get; set; } = 30;
    public int HeartbeatFailureLimit { get; set; } = 3;
    public int MaxPendingHandoffs { get; set; } = 100;
    public int MaxFieldCount { get; set; } = 64;
    public int MaxTotalFormBytes { get; set; } = 65_536;
    public int MaxFieldValueLength { get; set; } = 8_192;
    public string Scope { get; set; } = ContextGuardScopes.User;
    public Dictionary<string, ContextGuardProfileOptions> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ContextGuardProfileOptions
{
    public string DisplayName { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public string Delivery { get; set; } = ContextGuardDelivery.Embed;
    public List<string> AllowedFieldNames { get; set; } = [];
    public string CorrelationFieldName { get; set; } = "correlation";
    public List<string> AllowedOriginHosts { get; set; } = [];
}

public static class ContextGuardScopes
{
    public const string User = "user";
    public const string UserAndClient = "user+client";
}

public static class ContextGuardDelivery
{
    public const string Embed = "embed";
    public const string Redirect = "redirect";
}
