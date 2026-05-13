namespace OpenModulePlatform.Web.Shared.Services;

public sealed record OmpBranding(string PlatformName, string PortalName)
{
    public static OmpBranding Default { get; } = new("OMP", "Portal");

    public string PortalDisplayName =>
        string.IsNullOrWhiteSpace(PortalName)
            ? PlatformName
            : $"{PlatformName} {PortalName}".Trim();

    public string ApplyPlatformName(string? text)
        => ApplyTerms(text);

    public string ApplyTerms(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var portalName = string.IsNullOrWhiteSpace(PortalName)
            ? Default.PortalName
            : PortalName;
        var portalNameLower = portalName.ToLowerInvariant();

        return text
            .Replace("OpenModulePlatform", PlatformName, StringComparison.Ordinal)
            .Replace("OMP", PlatformName, StringComparison.Ordinal)
            .Replace("Portal", portalName, StringComparison.Ordinal)
            .Replace("portal", portalNameLower, StringComparison.Ordinal);
    }
}
