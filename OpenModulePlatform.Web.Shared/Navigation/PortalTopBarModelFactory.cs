using OpenModulePlatform.Web.Shared.Options;

namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Builds the shared portal shortcut model used by module layouts.
/// </summary>
public static class PortalTopBarModelFactory
{
    private static readonly PortalTopBarLinkOptions[] DefaultLinks =
    [
        new() { TextKey = "Portal", Href = "/" },
        new() { TextKey = "Admin", Href = "/admin/overview" },
        new() { TextKey = "Instances", Href = "/admin/instances" },
        new() { TextKey = "Hosts", Href = "/admin/hosts" },
        new() { TextKey = "Modules", Href = "/admin/modules" },
        new() { TextKey = "Apps", Href = "/admin/apps" },
        new() { TextKey = "Artifacts", Href = "/admin/artifacts" },
        new() { TextKey = "Automation", Href = "/admin/automation" },
        new() { TextKey = "RBAC", Href = "/admin/rbac" }
    ];

    public static PortalTopBarModel Create(WebAppOptions options)
    {
        var topBarOptions = options.PortalTopBar ?? new PortalTopBarOptions();

        if (!topBarOptions.Enabled)
        {
            return PortalTopBarModel.Hidden;
        }

        var linkOptions = topBarOptions.Links.Length > 0
            ? topBarOptions.Links
            : DefaultLinks;

        var links = linkOptions
            .Where(static x =>
                !string.IsNullOrWhiteSpace(x.TextKey) &&
                !string.IsNullOrWhiteSpace(x.Href))
            .Select(x => new PortalTopBarLink(
                x.TextKey.Trim(),
                CombinePortalHref(topBarOptions.PortalBaseUrl, x.Href)))
            .ToArray();

        if (links.Length == 0)
        {
            return PortalTopBarModel.Hidden;
        }

        return new PortalTopBarModel
        {
            IsVisible = true,
            Links = links,
            OverflowToggleTextKey = "More"
        };
    }

    private static string CombinePortalHref(string portalBaseUrl, string href)
    {
        if (Uri.IsWellFormedUriString(href, UriKind.Absolute))
        {
            return href;
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

    private static string NormalizeBasePath(string? portalBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(portalBaseUrl) || portalBaseUrl == "/")
        {
            return "/";
        }

        var trimmed = portalBaseUrl.Trim();

        if (Uri.IsWellFormedUriString(trimmed, UriKind.Absolute))
        {
            return trimmed.TrimEnd('/');
        }

        return trimmed.StartsWith("/", StringComparison.Ordinal)
            ? trimmed.TrimEnd('/')
            : $"/{trimmed.TrimStart('/').TrimEnd('/')}";
    }
}
