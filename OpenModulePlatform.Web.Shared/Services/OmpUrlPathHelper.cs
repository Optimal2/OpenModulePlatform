using Microsoft.AspNetCore.Http;
using OpenModulePlatform.Web.Shared.Web;

namespace OpenModulePlatform.Web.Shared.Services;

public static class OmpUrlPathHelper
{
    public static string CombinePortalHref(string? portalBaseUrl, string? href)
    {
        // R8-P1-6: IsWellFormedUriString was the only test here, and it is true for
        // "javascript:alert(1)" -- an absolute URI is not the same thing as a safe one. The
        // scheme allowlist is what decides whether an absolute href may be returned as-is.
        if (!string.IsNullOrWhiteSpace(href)
            && Uri.TryCreate(href.Trim(), UriKind.Absolute, out var absoluteHref))
        {
            return OmpUrlSafety.IsAllowedAbsoluteScheme(absoluteHref) ? href.Trim() : "/";
        }

        var normalizedBaseUrl = NormalizeBasePath(portalBaseUrl);
        var normalizedHref = string.IsNullOrWhiteSpace(href)
            ? "/"
            : href.Trim();

        if (normalizedBaseUrl == "/")
        {
            return normalizedHref.StartsWith("/", StringComparison.Ordinal)
                ? normalizedHref
                : $"/{normalizedHref.TrimStart('/')}";
        }

        if (normalizedHref is "/" or "")
        {
            return normalizedBaseUrl;
        }

        return $"{normalizedBaseUrl.TrimEnd('/')}/{normalizedHref.TrimStart('/')}";
    }

    public static string NormalizeBasePath(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || basePath == "/")
        {
            return "/";
        }

        var trimmed = basePath.Trim();

        // PortalBaseUrl is operator-configured and every href in the top bar is built on top of
        // it, so an absolute value has to clear the scheme allowlist before it is accepted as a
        // prefix. Falling back to "/" keeps the app usable with same-origin links rather than
        // emitting a base nobody validated (R8-P1-6).
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteBase))
        {
            return OmpUrlSafety.IsAllowedAbsoluteScheme(absoluteBase)
                ? trimmed.TrimEnd('/')
                : "/";
        }

        return trimmed.StartsWith("/", StringComparison.Ordinal)
            ? trimmed.TrimEnd('/')
            : $"/{trimmed.TrimStart('/').TrimEnd('/')}";
    }

    public static string BuildAppHomeHref(PathString pathBase)
    {
        if (!pathBase.HasValue || string.IsNullOrWhiteSpace(pathBase.Value) || pathBase.Value == "/")
        {
            return "/";
        }

        return $"{pathBase.Value!.TrimEnd('/')}/";
    }
}
