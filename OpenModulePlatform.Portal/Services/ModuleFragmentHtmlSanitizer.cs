// File: OpenModulePlatform.Portal/Services/ModuleFragmentHtmlSanitizer.cs
using AngleSharp.Dom;
using Ganss.Xss;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Reduces a module-served dashboard fragment to the small HTML subset the Portal
/// is willing to render inside its own page.
/// </summary>
/// <remarks>
/// The fragment is inserted into the Portal's DOM, so anything the Portal's own
/// scripts react to must be removed, not only classic XSS vectors:
/// <list type="bullet">
/// <item>Elements outside the allowlist are dropped together with their content
/// (script, style, iframe, object, embed, form, img, svg <c>use</c>/<c>foreignObject</c>, ...).</item>
/// <item>Only <c>class</c>, <c>href</c>, <c>aria-*</c>, <c>data-module-*</c>, table spans
/// and the presentational SVG geometry attributes survive; <c>style</c>, <c>id</c> and
/// every <c>on*</c> handler are removed.</item>
/// <item>Other <c>data-*</c> attributes are removed because the Portal's dashboard
/// scripts bind to <c>data-dashboard-*</c>, <c>data-widget-*</c>, <c>data-portal-*</c> and
/// many more; a fragment carrying them could drive Portal behaviour.</item>
/// <item>Every <c>href</c> is rewritten to the module's base, or removed when it
/// points anywhere else (other hosts, the Portal, other modules, script schemes).</item>
/// </list>
/// </remarks>
public static class ModuleFragmentHtmlSanitizer
{
    public const string ModuleDataAttributePrefix = "data-module-";
    public const string WidgetModeAttribute = "data-widget-mode";

    private static readonly string[] AllowedTags =
    [
        "div", "span", "p", "a", "ul", "li", "strong", "em",
        "table", "thead", "tbody", "tr", "th", "td",
        "svg", "g", "path", "circle", "ellipse", "line", "polyline", "polygon", "rect"
    ];

    private static readonly string[] AllowedAttributes =
    [
        "class", "href", "colspan", "rowspan",
        "viewbox", "width", "height", "fill", "stroke", "stroke-width", "stroke-linecap",
        "stroke-linejoin", "d", "cx", "cy", "r", "rx", "ry", "x", "y", "x1", "y1", "x2", "y2",
        "points", "transform", "focusable", "xmlns"
    ];

    /// <summary>
    /// Sanitizes <paramref name="html"/> and rewrites its links against
    /// <paramref name="moduleBaseHref"/> (the module web app base the Portal resolved).
    /// </summary>
    public static ModuleFragmentResult Sanitize(string? html, string moduleBaseHref)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new ModuleFragmentResult(true, string.Empty, null);
        }

        string? modeClass = null;
        var sanitizer = CreateSanitizer();
        sanitizer.PostProcessDom += (_, e) =>
        {
            foreach (var element in e.Document.QuerySelectorAll($"[{WidgetModeAttribute}]").ToArray())
            {
                modeClass ??= ModuleFragmentWidget.GetModeClass(element.GetAttribute(WidgetModeAttribute));
                element.RemoveAttribute(WidgetModeAttribute);
            }

            foreach (var element in e.Document.QuerySelectorAll("[href]").ToArray())
            {
                var rewritten = RewriteHref(element.GetAttribute("href"), moduleBaseHref);
                if (rewritten is null)
                {
                    element.RemoveAttribute("href");
                }
                else
                {
                    element.SetAttribute("href", rewritten);
                }
            }
        };

        var sanitized = sanitizer.Sanitize(html);
        return new ModuleFragmentResult(true, sanitized, modeClass);
    }

    /// <summary>
    /// Resolves one fragment link against the module base. Returns null when the link
    /// must be dropped.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>jobs/5</c> (no leading slash) is module-relative: <c>{base}/jobs/5</c>.</item>
    /// <item><c>/jobs/5</c> is module-root-relative: <c>{base}/jobs/5</c>, unless it already
    /// starts with the module's own base path (a module that renders links with its
    /// path base included), in which case only the base origin is added.</item>
    /// <item>An absolute http(s) URL is kept only when it starts with an absolute module base.</item>
    /// <item>Everything else - other schemes, <c>//host</c>, backslashes, <c>.</c>/<c>..</c>
    /// segments and bare <c>#fragment</c> links - is dropped.</item>
    /// </list>
    /// </remarks>
    public static string? RewriteHref(string? href, string moduleBaseHref)
    {
        var value = href?.Trim();
        if (string.IsNullOrEmpty(value)
            || string.IsNullOrWhiteSpace(moduleBaseHref)
            || value.StartsWith('#')
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            return null;
        }

        var (baseOrigin, basePath) = SplitBase(moduleBaseHref.Trim());
        if (basePath is null)
        {
            return null;
        }

        string result;
        if (value.StartsWith('/'))
        {
            var alreadyInBase = basePath.Length == 0
                || string.Equals(PathPart(value), basePath, StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(basePath + "?", StringComparison.OrdinalIgnoreCase);
            result = alreadyInBase
                ? baseOrigin + value
                : baseOrigin + basePath + value;
        }
        else if (HasScheme(value))
        {
            if (baseOrigin.Length == 0
                || !Uri.TryCreate(value, UriKind.Absolute, out var absolute)
                || absolute.Scheme is not ("http" or "https"))
            {
                return null;
            }

            var origin = absolute.GetLeftPart(UriPartial.Authority);
            if (!string.Equals(origin, baseOrigin, StringComparison.OrdinalIgnoreCase)
                || !(basePath.Length == 0
                    || string.Equals(absolute.AbsolutePath, basePath, StringComparison.OrdinalIgnoreCase)
                    || absolute.AbsolutePath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            // Uri has already collapsed any ../ in AbsolutePath, so the prefix check above
            // ran on the normalised path; the raw value is checked for dot segments below.
            if (HasDotSegment(PathPart(value)))
            {
                return null;
            }

            result = origin + absolute.PathAndQuery;
        }
        else
        {
            result = baseOrigin + basePath + "/" + value;
        }

        return HasDotSegment(PathPart(result)) ? null : result;
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer
        {
            AllowDataAttributes = false,
            KeepChildNodes = false
        };

        sanitizer.AllowedTags.Clear();
        foreach (var tag in AllowedTags)
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in AllowedAttributes)
        {
            sanitizer.AllowedAttributes.Add(attribute);
        }

        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowedSchemes.Clear();

        // href is rewritten by RewriteHref in PostProcessDom, which is stricter than the
        // library's scheme check and knows the module base; keeping href out of
        // UriAttributes stops the library from normalising it first.
        sanitizer.UriAttributes.Clear();

        sanitizer.RemovingAttribute += (_, e) =>
        {
            var name = e.Attribute.Name;
            if (name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(ModuleDataAttributePrefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, WidgetModeAttribute, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
            }
        };

        return sanitizer;
    }

    private static (string Origin, string? Path) SplitBase(string moduleBaseHref)
    {
        if (moduleBaseHref.StartsWith('/') && !moduleBaseHref.StartsWith("//", StringComparison.Ordinal))
        {
            return (string.Empty, PathPart(moduleBaseHref).TrimEnd('/'));
        }

        if (Uri.TryCreate(moduleBaseHref, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https")
        {
            return (absolute.GetLeftPart(UriPartial.Authority), absolute.AbsolutePath.TrimEnd('/'));
        }

        return (string.Empty, null);
    }

    private static bool HasScheme(string value)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        var firstDelimiter = value.IndexOfAny(['/', '?', '#']);
        return firstDelimiter < 0 || colon < firstDelimiter;
    }

    private static string PathPart(string value)
    {
        var end = value.IndexOfAny(['?', '#']);
        return end < 0 ? value : value[..end];
    }

    private static bool HasDotSegment(string path)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return true;
        }

        var schemeEnd = decoded.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var pathStart = decoded.IndexOf('/', schemeEnd + 3);
            decoded = pathStart < 0 ? string.Empty : decoded[pathStart..];
        }

        return decoded.Contains('\\', StringComparison.Ordinal)
            || decoded.Split('/').Any(static segment => segment is "." or "..");
    }
}
