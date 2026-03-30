using System.Globalization;
using OpenModulePlatform.Web.Shared.Options;

namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Builds a basic shared portal shortcut model from configuration.
/// </summary>
/// <remarks>
/// The runtime top bar shown in modules is normally created by <see cref="PortalTopBarService"/>
/// so it can populate module links using the same access logic as the Portal start page.
/// This factory remains as a lightweight configuration-only fallback.
/// </remarks>
public static class PortalTopBarModelFactory
{
    public static PortalTopBarModel Create(WebAppOptions options)
    {
        var topBarOptions = options.PortalTopBar ?? new PortalTopBarOptions();

        if (!topBarOptions.Enabled)
        {
            return PortalTopBarModel.Hidden;
        }

        var portalLink = new PortalTopBarLink("Portal", CombinePortalHref(topBarOptions.PortalBaseUrl, "/"));

        return new PortalTopBarModel
        {
            IsVisible = true,
            Links = [portalLink],
            PortalLink = portalLink,
            ModuleLinks = Array.Empty<PortalTopBarLink>(),
            LanguageOptions = options.SupportedCultures
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(c => new PortalTopBarCultureOption(
                    c,
                    c.StartsWith("sv", StringComparison.OrdinalIgnoreCase) ? "Swedish" : c.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "English" : c,
                    string.Equals(c, CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase)))
                .ToArray(),
            OverflowToggleTextKey = "More",
            CollapsedToggleTextKey = "Modules",
            LanguageToggleTextKey = "Language"
        };
    }

    internal static string CombinePortalHref(string portalBaseUrl, string href)
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
