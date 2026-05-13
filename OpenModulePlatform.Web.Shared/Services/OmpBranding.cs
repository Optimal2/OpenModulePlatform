namespace OpenModulePlatform.Web.Shared.Services;

public sealed record OmpBranding(string PlatformName, string PortalName)
{
    public static OmpBranding Default { get; } = new("OMP", "Portal");

    public string PortalDisplayName =>
        string.IsNullOrWhiteSpace(PortalName)
            ? PlatformName
            : $"{PlatformName} {PortalName}".Trim();

    public string ApplyPlatformName(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("OpenModulePlatform", PlatformName, StringComparison.Ordinal)
            .Replace("OMP", PlatformName, StringComparison.Ordinal);
    }
}
