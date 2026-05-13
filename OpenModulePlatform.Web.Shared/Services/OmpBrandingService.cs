namespace OpenModulePlatform.Web.Shared.Services;

public sealed class OmpBrandingService
{
    public const string Category = "branding";
    public const string PlatformNameSetting = "platformName";
    public const string PortalNameSetting = "portalName";

    private readonly OmpConfigurationService _configuration;

    public OmpBrandingService(OmpConfigurationService configuration)
    {
        _configuration = configuration;
    }

    public async Task<OmpBranding> GetBrandingAsync(CancellationToken ct)
    {
        var platformName = await _configuration.GetGlobalStringAsync(Category, PlatformNameSetting, ct);
        var portalName = await _configuration.GetGlobalStringAsync(Category, PortalNameSetting, ct);

        return new OmpBranding(
            Normalize(platformName, OmpBranding.Default.PlatformName),
            Normalize(portalName, OmpBranding.Default.PortalName));
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
