using Microsoft.AspNetCore.Http;

namespace OpenModulePlatform.Web.Shared.Services;

public static class OmpUrlPathHelper
{
    public static string CombinePortalHref(string? portalBaseUrl, string? href)
    {
        if (!string.IsNullOrWhiteSpace(href)
            && Uri.IsWellFormedUriString(href, UriKind.Absolute))
        {
            return href.Trim();
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

        if (Uri.IsWellFormedUriString(trimmed, UriKind.Absolute))
        {
            return trimmed.TrimEnd('/');
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
