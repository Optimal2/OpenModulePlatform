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
/// 3. RoutePath as a path relative to the assigned host root.
/// 4. PublicUrl as a legacy fallback when RoutePath is empty.
///
/// Relative route paths are never prefixed with the Portal path.
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

            var hostRoot = BuildHostRoot(request, app.HostKey);
            if (string.IsNullOrWhiteSpace(hostRoot))
            {
                return null;
            }

            return CombineHostRootAndRoute(hostRoot, routePath);
        }

        var publicUrl = Clean(app.PublicUrl);
        return string.IsNullOrWhiteSpace(publicUrl) ? null : publicUrl;
    }

    private static string BuildHostRoot(HttpRequest request, string? hostKey)
    {
        var cleanedHostKey = Clean(hostKey);
        var publicBaseUrl = request.GetPublicBaseUrl();

        if (string.IsNullOrWhiteSpace(cleanedHostKey))
        {
            return publicBaseUrl;
        }

        if (Uri.TryCreate(cleanedHostKey, UriKind.Absolute, out var absoluteHost))
        {
            return absoluteHost.GetLeftPart(UriPartial.Authority);
        }

        if (Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var currentBaseUri))
        {
            return $"{currentBaseUri.Scheme}://{cleanedHostKey}";
        }

        return $"https://{cleanedHostKey}";
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
