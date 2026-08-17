// File: OpenModulePlatform.Web.Shared/Security/OmpAuthDefaults.cs
namespace OpenModulePlatform.Web.Shared.Security;

/// <summary>
/// Shared authentication constants for OMP web applications.
/// </summary>
public static class OmpAuthDefaults
{
    public const string AuthenticationScheme = "OmpAuth";
    public const string OidcAuthenticationScheme = "OmpOidc";
    public const string AdProviderDisplayName = "AD";
    public const string AdfsProviderDisplayName = "ADFS";
    public const string OidcProviderDisplayName = "OIDC";
    public const string OidcDefaultDisplayName = "OpenID Connect";
    public const string OidcLoginPath = "/oidc";
    public const string OidcCallbackPath = "/signin-oidc";
    public const string CookieName = ".OpenModulePlatform.Auth";
    public const string LoginPath = "/auth/login";
    public const string LogoutPath = "/auth/logout";
    public const string AccessDeniedPath = "/status/403";
    public const string SetActiveRolePath = "/security/set-active-role";
    public const string RbacSetActiveRolePath = "/rbac/set-active-role";

    public const string UserIdClaimType = "omp:user_id";
    public const string ProviderClaimType = "omp:provider";
    public const string ProviderUserKeyClaimType = "omp:provider_user_key";
    public const string PrincipalClaimType = "omp:principal";

    // R7-F10. The sign-in stamps the account's current security stamp into the
    // cookie; every request compares it against omp.users so a rotated stamp
    // (account disabled, password changed) ends the session.
    public const string SecurityStampClaimType = "omp:security_stamp";

    public const string ConfigurationCategory = "auth";
    public const string ExternalUserProvisioningModeSetting = "externalUserProvisioningMode";
    public const string SelfRegistrationEnabledSetting = "selfRegistrationEnabled";
    // R7-F10. Session revocation tuning, stored in the omp configuration table
    // under the auth category: how long a verified account state may be cached,
    // and whether an unverifiable state rejects the session or lets it through.
    public const string SessionRevocationCacheSecondsSetting = "sessionRevocationCacheSeconds";
    public const string SessionRevocationFailureModeSetting = "sessionRevocationFailureMode";
    public const string SessionRevocationFailureModeStrict = "strict";
    public const string SessionRevocationFailureModeLenient = "lenient";
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
