// File: OpenModulePlatform.Portal/Models/ModuleFragmentWidgets.cs
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Options;

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
/// so the widget type needs no schema change. The definition rules and the payload form
/// live in <see cref="ModuleFragmentWidgetPayload"/> (OpenModulePlatform.Artifacts) because
/// the HostAgent universal-package import writes the same rows.
/// </remarks>
public static class ModuleFragmentWidget
{
    public const string WidgetType = ModuleFragmentWidgetPayload.WidgetType;

    public const int MaxFragmentPathLength = ModuleFragmentWidgetPayload.MaxFragmentPathLength;

    /// <summary>Content widths below this are reported as <c>is-narrow</c>.</summary>
    public const int MediumMinWidth = 360;

    /// <summary>Content widths from this value are reported as <c>is-wide</c>.</summary>
    public const int WideMinWidth = 640;

    public static bool IsModuleFragment(string? widgetType)
        => ModuleFragmentWidgetPayload.IsModuleFragment(widgetType);

    /// <summary>
    /// Serializes a validated configuration into the stored payload form.
    /// </summary>
    public static string SerializePayload(ModuleFragmentWidgetConfig config)
        => ModuleFragmentWidgetPayload.Serialize(config);

    /// <summary>
    /// Reads the stored payload. Returns null when the payload is missing or no longer
    /// passes the same rules the import applied, so a tampered row never reaches a fetch.
    /// </summary>
    public static ModuleFragmentWidgetConfig? TryParsePayload(string? payload)
        => ModuleFragmentWidgetPayload.TryParse(payload);

    /// <summary>
    /// Returns null when <paramref name="fragmentPath"/> is a valid module-relative path,
    /// otherwise a message describing the first rule it breaks.
    /// </summary>
    public static string? GetFragmentPathError(string? fragmentPath)
        => ModuleFragmentWidgetPayload.GetFragmentPathError(fragmentPath);

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

    /// <summary>Shortest refresh a module can ask for, in seconds.</summary>
    public const int MinRefreshSeconds = 15;

    /// <summary>Longest refresh a module can ask for, in seconds (an hour).</summary>
    public const int MaxRefreshSeconds = 3600;

    /// <summary>
    /// Reads a module's <c>data-widget-refresh</c> value: the least often, in seconds,
    /// the module wants its fragment fetched again. Null when the value is not a
    /// positive whole number; otherwise clamped to the supported range. The dashboard
    /// fetches at the longer of this and the user's own interval, and never when the
    /// user has turned refreshing off.
    /// </summary>
    public static int? GetRefreshSeconds(string? value)
        => int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? Math.Clamp(seconds, MinRefreshSeconds, MaxRefreshSeconds)
            : null;

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
/// Result of loading one module fragment for the current user.
/// </summary>
/// <param name="IsLoaded">True when <paramref name="Html"/> holds sanitized module content.</param>
/// <param name="Html">Sanitized fragment HTML, or empty when the placeholder should be shown.</param>
/// <param name="ModeClass">The module's <c>data-widget-mode</c> suggestion mapped to a container class, if any.</param>
/// <param name="RefreshSeconds">The module's <c>data-widget-refresh</c> request (the least often it wants to be fetched again), if any.</param>
public sealed record ModuleFragmentResult(bool IsLoaded, string Html, string? ModeClass, int? RefreshSeconds = null)
{
    public static ModuleFragmentResult Unavailable { get; } = new(false, string.Empty, null);

    public ModuleFragmentFailureReason FailureReason { get; init; }

    /// <summary>
    /// Why the configured fragment endpoint blocked the request, shown to administrators
    /// only. <see cref="ModuleFragmentEndpointIssue.None"/> for every other outcome.
    /// </summary>
    public ModuleFragmentEndpointIssue EndpointIssue { get; init; }

    public static ModuleFragmentResult EndpointBlocked(ModuleFragmentEndpointIssue issue)
        => Unavailable with { EndpointIssue = issue };
}

public enum ModuleFragmentFailureReason
{
    None,
    Timeout
}

/// <summary>
/// View model for the module fragment widget container.
/// </summary>
/// <param name="ShowAdminDiagnostics">True for Portal administrators: shows why the endpoint blocked the widget.</param>
public sealed record DashboardModuleFragmentWidget(
    int WidgetId,
    int ContentWidth,
    ModuleFragmentResult Result,
    bool ShowAdminDiagnostics = false);
