using Microsoft.AspNetCore.Http;
using OpenModulePlatform.Web.Shared.Security;
using System.Globalization;
using System.Security.Claims;

namespace OpenModulePlatform.Web.Shared.Services;

public sealed class OmpBrandingService
{
    public const string Category = "branding";
    public const string PlatformNameSetting = "platformName";
    public const string PortalNameSetting = "portalName";
    public const string HeroLogoUrlSetting = "heroLogoUrl";
    public const string FaviconUrlSetting = "faviconUrl";

    private readonly OmpConfigurationService _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly RbacService _rbac;

    public OmpBrandingService(
        OmpConfigurationService configuration,
        IHttpContextAccessor httpContextAccessor,
        RbacService rbac)
    {
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
        _rbac = rbac;
    }

    public async Task<OmpBranding> GetBrandingAsync(CancellationToken ct)
    {
        var user = _httpContextAccessor.HttpContext?.User;
        var userId = TryGetOmpUserId(user);
        var roleContext = user?.Identity?.IsAuthenticated == true
            ? await _rbac.GetUserRoleContextAsync(user, ct)
            : UserRoleContext.Empty;

        var platformName = await _configuration.GetEffectiveStringAsync(
            Category,
            PlatformNameSetting,
            userId,
            roleContext.ActiveRoleId,
            roleContext.EffectivePermissions,
            ct);

        var portalName = await _configuration.GetEffectiveStringAsync(
            Category,
            PortalNameSetting,
            userId,
            roleContext.ActiveRoleId,
            roleContext.EffectivePermissions,
            ct);

        var heroLogoUrl = await _configuration.GetEffectiveStringAsync(
            Category,
            HeroLogoUrlSetting,
            userId,
            roleContext.ActiveRoleId,
            roleContext.EffectivePermissions,
            ct);

        var faviconUrl = await _configuration.GetEffectiveStringAsync(
            Category,
            FaviconUrlSetting,
            userId,
            roleContext.ActiveRoleId,
            roleContext.EffectivePermissions,
            ct);

        return new OmpBranding(
            Normalize(platformName, OmpBranding.Default.PlatformName),
            Normalize(portalName, OmpBranding.Default.PortalName),
            NormalizeOptional(heroLogoUrl),
            NormalizeOptional(faviconUrl));
    }

    private static int? TryGetOmpUserId(ClaimsPrincipal? user)
    {
        var claimValue = user?.FindFirstValue(OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(claimValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : null;
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
