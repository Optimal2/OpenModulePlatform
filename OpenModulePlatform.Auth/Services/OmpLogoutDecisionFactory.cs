using Microsoft.AspNetCore.WebUtilities;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using System.Security.Claims;

namespace OpenModulePlatform.Auth.Services;

internal static class OmpLogoutDecisionFactory
{
    public const string LogoutKindLocal = "local";
    public const string LogoutKindOidc = "oidc";
    public const string LogoutKindWindows = "windows";

    public static OmpLogoutDecision Create(
        ClaimsPrincipal user,
        OmpAuthOptions authOptions,
        OmpOidcProviderStatus oidcProviderStatus)
    {
        var provider = user.FindFirstValue(OmpAuthDefaults.ProviderClaimType);
        var logoutKind = ResolveLogoutKind(provider, authOptions, oidcProviderStatus);
        var loginPath = NormalizeLocalAuthPath(authOptions.LoginPath, OmpAuthDefaults.LoginPath);

        return new OmpLogoutDecision(
            QueryHelpers.AddQueryString(loginPath, "logout", logoutKind),
            SignOutOidc: string.Equals(logoutKind, LogoutKindOidc, StringComparison.Ordinal),
            Provider: provider);
    }

    public static string NormalizeLocalAuthPath(string? configuredPath, string fallbackPath)
    {
        var fallback = IsSafeLocalReturnUrl(fallbackPath)
            ? fallbackPath
            : "/";
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return fallback;
        }

        var candidate = configuredPath.Trim();
        return IsSafeLocalReturnUrl(candidate)
            && !candidate.Contains('?', StringComparison.Ordinal)
            && !candidate.Contains('#', StringComparison.Ordinal)
            ? candidate
            : fallback;
    }

    private static string ResolveLogoutKind(
        string? provider,
        OmpAuthOptions authOptions,
        OmpOidcProviderStatus oidcProviderStatus)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return LogoutKindLocal;
        }

        if (string.Equals(provider, OmpAuthDefaults.AdProviderDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return LogoutKindWindows;
        }

        var configuredOidcProvider = NormalizeOidcProviderName(authOptions.Oidc.ProviderName);
        if (oidcProviderStatus.IsEnabled &&
            string.Equals(provider.Trim(), configuredOidcProvider, StringComparison.OrdinalIgnoreCase))
        {
            return LogoutKindOidc;
        }

        return LogoutKindLocal;
    }

    private static string NormalizeOidcProviderName(string? providerName)
        => string.IsNullOrWhiteSpace(providerName)
            ? OmpAuthDefaults.OidcProviderDisplayName
            : providerName.Trim();

    private static bool IsSafeLocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
            || !returnUrl.StartsWith("/", StringComparison.Ordinal)
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var unescaped = Uri.UnescapeDataString(returnUrl);
            return !unescaped.StartsWith("//", StringComparison.Ordinal)
                && !unescaped.Contains('\\', StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }
}

internal sealed record OmpLogoutDecision(
    string RedirectUri,
    bool SignOutOidc,
    string? Provider);
