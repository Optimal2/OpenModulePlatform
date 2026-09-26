// File: OpenModulePlatform.Portal/Models/ModuleFragmentWidgets.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// Contract for the generic <c>module-fragment</c> dashboard widget type: a widget
/// whose content is an HTML fragment served by a module's own web app.
/// </summary>
/// <remarks>
/// The Portal owns only the mechanism (lookup, fetch, sanitization, caching and the
/// widget container). Everything module-specific - the data, the markup and the
/// permissions the module enforces on its own endpoint - stays in the module's
/// repository. The configuration is stored as JSON in <c>omp_portal.widgets.payload</c>
/// so the widget type needs no schema change.
/// </remarks>
public static class ModuleFragmentWidget
{
    public const string WidgetType = "module-fragment";

    public const int MaxFragmentPathLength = 400;

    /// <summary>Content widths below this are reported as <c>is-narrow</c>.</summary>
    public const int MediumMinWidth = 360;

    /// <summary>Content widths from this value are reported as <c>is-wide</c>.</summary>
    public const int WideMinWidth = 640;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool IsModuleFragment(string? widgetType)
        => string.Equals(widgetType?.Trim(), WidgetType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Serializes a validated configuration into the stored payload form.
    /// </summary>
    public static string SerializePayload(ModuleFragmentWidgetConfig config)
        => JsonSerializer.Serialize(config, PayloadJsonOptions);

    /// <summary>
    /// Reads the stored payload. Returns null when the payload is missing or no longer
    /// passes the same rules the import applied, so a tampered row never reaches a fetch.
    /// </summary>
    public static ModuleFragmentWidgetConfig? TryParsePayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var config = JsonSerializer.Deserialize<ModuleFragmentWidgetConfig>(payload, PayloadJsonOptions);
            if (config is null
                || string.IsNullOrWhiteSpace(config.AppKey)
                || GetFragmentPathError(config.FragmentPath) is not null)
            {
                return null;
            }

            return config;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns null when <paramref name="fragmentPath"/> is a valid module-relative path,
    /// otherwise a message describing the first rule it breaks.
    /// </summary>
    /// <remarks>
    /// The path is appended to the module's registered web app base, so it must not be
    /// able to leave that base: it has to start with a single slash, must not carry a
    /// scheme, host, backslash, <c>..</c> segment, control character or fragment.
    /// </remarks>
    public static string? GetFragmentPathError(string? fragmentPath)
    {
        if (string.IsNullOrWhiteSpace(fragmentPath))
        {
            return "fragmentPath is required for module-fragment widgets.";
        }

        var path = fragmentPath.Trim();
        if (path.Length > MaxFragmentPathLength)
        {
            return $"fragmentPath must be at most {MaxFragmentPathLength} characters.";
        }

        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
        {
            return "fragmentPath must be a relative path that starts with a single '/'.";
        }

        if (path.Contains('\\', StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)
            || path.Contains('#', StringComparison.Ordinal)
            || path.Contains('@', StringComparison.Ordinal)
            || path.Any(char.IsControl)
            || path.Any(char.IsWhiteSpace))
        {
            return "fragmentPath must not contain a scheme, host, backslash, '#', whitespace or control characters.";
        }

        var pathOnly = path.Split('?', 2)[0];
        var decoded = Uri.UnescapeDataString(pathOnly);
        if (decoded.Contains('\\', StringComparison.Ordinal)
            || decoded.Split('/').Any(static segment => segment is ".." or "."))
        {
            return "fragmentPath must not contain '.' or '..' segments.";
        }

        return null;
    }

    /// <summary>
    /// Maps a widget content width in CSS pixels to the container class the Portal sets.
    /// </summary>
    public static string GetWidthClass(int contentWidth)
        => contentWidth switch
        {
            < MediumMinWidth => "is-narrow",
            < WideMinWidth => "is-medium",
            _ => "is-wide"
        };

    /// <summary>
    /// Maps a module's <c>data-widget-mode</c> suggestion to a container class, or null
    /// when the value is not one of the three supported modes.
    /// </summary>
    public static string? GetModeClass(string? widgetMode)
        => widgetMode?.Trim().ToLowerInvariant() switch
        {
            "narrow" => "is-narrow",
            "medium" => "is-medium",
            "wide" => "is-wide",
            _ => null
        };
}

/// <summary>
/// Stored configuration of one <c>module-fragment</c> widget.
/// </summary>
/// <param name="AppKey">The module web app whose registered base the fragment path is resolved against.</param>
/// <param name="FragmentPath">Module-relative path of the fragment endpoint, for example <c>/widgets/overview</c>.</param>
/// <param name="DefaultWidth">Optional default width in pixels for a newly added widget.</param>
/// <param name="DefaultHeight">Optional default height in pixels for a newly added widget.</param>
public sealed record ModuleFragmentWidgetConfig(
    string AppKey,
    string FragmentPath,
    int? DefaultWidth = null,
    int? DefaultHeight = null);

/// <summary>
/// Result of loading one module fragment for the current user.
/// </summary>
/// <param name="IsLoaded">True when <paramref name="Html"/> holds sanitized module content.</param>
/// <param name="Html">Sanitized fragment HTML, or empty when the placeholder should be shown.</param>
/// <param name="ModeClass">The module's <c>data-widget-mode</c> suggestion mapped to a container class, if any.</param>
public sealed record ModuleFragmentResult(bool IsLoaded, string Html, string? ModeClass)
{
    public static ModuleFragmentResult Unavailable { get; } = new(false, string.Empty, null);
}

/// <summary>
/// View model for the module fragment widget container.
/// </summary>
public sealed record DashboardModuleFragmentWidget(
    int WidgetId,
    int ContentWidth,
    ModuleFragmentResult Result);
