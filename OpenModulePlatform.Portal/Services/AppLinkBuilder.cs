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
/// 4. RoutePath relative to the current request only when the app has no host or the host matches the current request host.
/// 5. PublicUrl as a legacy fallback when RoutePath is empty.
///
/// The Portal no longer fabricates absolute URLs from <c>HostKey</c> alone.
/// A cross-host app should instead use <c>omp.Hosts.BaseUrl</c> or an absolute <c>RoutePath</c>/<c>PublicUrl</c>.
/// </remarks>
public static class AppLinkBuilder
{
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
        return string.IsNullOrWhiteSpace(publicUrl) ? null : publicUrl;
    }

    private static string? ResolveHostRoot(HttpRequest request, PortalAppEntry app)
    {
        var hostBaseUrl = Clean(app.HostBaseUrl);
        if (!string.IsNullOrWhiteSpace(hostBaseUrl)
            && Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var absoluteBaseUrl))
        {
            return absoluteBaseUrl.GetLeftPart(UriPartial.Authority);
        }

        var hostKey = Clean(app.HostKey);
        if (string.IsNullOrWhiteSpace(hostKey) || HostMatchesCurrentRequest(request, hostKey))
        {
            return request.GetPublicBaseUrl();
        }

        if (Uri.TryCreate(hostKey, UriKind.Absolute, out var absoluteHostKey))
        {
            return absoluteHostKey.GetLeftPart(UriPartial.Authority);
        }

        return null;
    }

    private static bool HostMatchesCurrentRequest(HttpRequest request, string hostKey)
    {
        return string.Equals(hostKey, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostKey, request.Host.Value, StringComparison.OrdinalIgnoreCase);
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
