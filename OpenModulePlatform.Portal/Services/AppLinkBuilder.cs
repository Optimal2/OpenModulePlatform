// File: OpenModulePlatform.Portal/Services/AppLinkBuilder.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Http;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Resolves portal catalog links for app instances.
/// </summary>
/// <remarks>
/// Resolution order:
/// 1. RoutePath when present.
/// 2. RoutePath as-is when it is already an absolute URL.
/// 3. RoutePath relative to the configured host base URL.
/// 4. RoutePath relative to the current Portal base URL when the host has no base URL.
/// 5. PublicUrl as a legacy fallback when RoutePath is empty.
///
/// <c>HostKey</c> is an identity key, not a URL source. Cross-host apps should
/// use <c>omp.Hosts.BaseUrl</c> or an absolute <c>RoutePath</c>/<c>PublicUrl</c>
/// when they are not reachable through the same public Portal base URL.
/// </remarks>
public static class AppLinkBuilder
{
    public static string? ResolveDisplayAddress(HttpRequest request, PortalAppEntry app)
    {
        var configuredAddress = Clean(app.RoutePath) ?? Clean(app.PublicUrl);
        if (!string.IsNullOrWhiteSpace(configuredAddress))
        {
            return configuredAddress;
        }

        return IsPortalApp(app)
            ? (request.PathBase.HasValue ? request.PathBase.Value.ToString() : "/")
            : null;
    }

    public static string? ResolveHref(HttpRequest request, PortalAppEntry app)
    {
        var routePath = Clean(app.RoutePath);
        if (!string.IsNullOrWhiteSpace(routePath))
        {
            if (Uri.TryCreate(routePath, UriKind.Absolute, out var absoluteRoute))
            {
                return absoluteRoute.ToString();
            }

            var hostRoot = ResolveHostRoot(request, app);
            return string.IsNullOrWhiteSpace(hostRoot)
                ? null
                : CombineHostRootAndRoute(hostRoot, routePath);
        }

        var publicUrl = Clean(app.PublicUrl);
        if (!string.IsNullOrWhiteSpace(publicUrl))
        {
            return publicUrl;
        }

        if (IsPortalApp(app))
        {
            var portalPath = request.PathBase.HasValue ? request.PathBase.Value.ToString() : string.Empty;
            return $"{request.GetPublicBaseUrl().TrimEnd('/')}{portalPath}";
        }

        return null;
    }

    private static bool IsPortalApp(PortalAppEntry app)
    {
        return string.Equals(app.AppKey, "omp_portal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(app.AppInstanceKey, "omp_portal", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveHostRoot(HttpRequest request, PortalAppEntry app)
    {
        var hostBaseUrl = Clean(app.HostBaseUrl);
        if (!string.IsNullOrWhiteSpace(hostBaseUrl)
            && Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var absoluteBaseUrl))
        {
            return absoluteBaseUrl.GetLeftPart(UriPartial.Authority);
        }

        return request.GetPublicBaseUrl();
    }

    private static string CombineHostRootAndRoute(string hostRoot, string routePath)
    {
        var normalizedHostRoot = hostRoot.Trim().TrimEnd('/');
        var trimmedRoute = routePath.Trim();
        var preserveTrailingSlash = trimmedRoute.EndsWith('/');
        var normalizedRoute = trimmedRoute.Trim('/');

        if (string.IsNullOrEmpty(normalizedRoute))
        {
            return normalizedHostRoot + "/";
        }

        return preserveTrailingSlash
            ? $"{normalizedHostRoot}/{normalizedRoute}/"
            : $"{normalizedHostRoot}/{normalizedRoute}";
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
