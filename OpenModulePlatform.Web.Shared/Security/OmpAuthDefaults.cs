// File: OpenModulePlatform.Web.Shared/Security/OmpAuthDefaults.cs
namespace OpenModulePlatform.Web.Shared.Security;

/// <summary>
/// Shared authentication constants for OMP web applications.
/// </summary>
public static class OmpAuthDefaults
{
    public const string AuthenticationScheme = "OmpAuth";
    public const string CookieName = ".OpenModulePlatform.Auth";
    public const string LoginPath = "/auth/login";
    public const string LogoutPath = "/auth/logout";
    public const string AccessDeniedPath = "/status/403";

    public const string UserIdClaimType = "omp:user_id";
    public const string ProviderClaimType = "omp:provider";
    public const string ProviderUserKeyClaimType = "omp:provider_user_key";
    public const string PrincipalClaimType = "omp:principal";

    public const string ConfigurationCategory = "auth";
    public const string ExternalUserProvisioningModeSetting = "externalUserProvisioningMode";
    public const string SelfRegistrationEnabledSetting = "selfRegistrationEnabled";
    public const string ExternalUserProvisioningModeManual = "Manual";
    public const string ExternalUserProvisioningModeIfRole = "IfRole";
    public const string ExternalUserProvisioningModeAutoIfRole = "AutoIfRole";
    public const string ExternalUserProvisioningModeIfAuthenticated = "IfAuthenticated";
    public const string ExternalUserProvisioningModeAutoIfAuthenticated = "AutoIfAuthenticated";
    public const string ExternalUserProvisioningModeAutomaticForAuthorizedUsers = "AutomaticForAuthorizedUsers";

    public static bool ParseEnabledConfigValue(string? value, bool defaultValue = true)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return defaultValue;
        }

        if (string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "enabled", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }
}
