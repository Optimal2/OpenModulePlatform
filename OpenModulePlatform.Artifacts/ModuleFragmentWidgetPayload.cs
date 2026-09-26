using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Stored configuration of one <c>module-fragment</c> dashboard widget.
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
/// Definition rules and stored payload form of the generic <c>module-fragment</c> widget type.
/// </summary>
/// <remarks>
/// A widget definition document names the fragment with <c>appKey</c>, <c>fragmentPath</c>,
/// <c>defaultWidth</c> and <c>defaultHeight</c>; the database stores them as JSON in
/// <c>omp_portal.widgets.payload</c>. Both import paths - the Portal widget import and the
/// HostAgent universal-package import through <see cref="DashboardWidgetPackageReader"/> -
/// normalize through <see cref="NormalizeDefinition"/>, so they write byte-identical payloads
/// and an identical re-import is recognized as unchanged by either path.
/// </remarks>
public static class ModuleFragmentWidgetPayload
{
    public const string WidgetType = "module-fragment";

    public const int MaxFragmentPathLength = 400;

    public const int MaxAppKeyLength = 100;

    // The Portal dashboard's widget size limits; PortalDashboardService uses these constants.
    public const int MinWidth = 160;
    public const int MaxWidth = 1800;
    public const int MinHeight = 96;
    public const int MaxHeight = 1400;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool IsModuleFragment(string? widgetType)
        => string.Equals(widgetType?.Trim(), WidgetType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Serializes a validated configuration into the stored payload form.
    /// </summary>
    public static string Serialize(ModuleFragmentWidgetConfig config)
        => JsonSerializer.Serialize(config, PayloadJsonOptions);

    /// <summary>
    /// Reads a stored payload. Returns null when the payload is missing or no longer passes
    /// the rules the import applied, so a tampered row never reaches a fetch.
    /// </summary>
    public static ModuleFragmentWidgetConfig? TryParse(string? payload)
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
    /// Validates the named fields of a widget definition and returns the payload to store.
    /// </summary>
    /// <remarks>
    /// For a <c>module-fragment</c> widget the definition must use <c>appKey</c> and
    /// <c>fragmentPath</c> and leave <c>payload</c> empty; the result is the normalized JSON.
    /// For every other widget type the fragment fields must be absent and the payload is
    /// returned as given (trimmed, null when empty). Any rule violation throws with the
    /// widget key in the message, so an import fails loudly instead of storing NULL.
    /// </remarks>
    public static string? NormalizeDefinition(
        string widgetKey,
        string widgetType,
        string? payload,
        string? appKey,
        string? fragmentPath,
        int? defaultWidth,
        int? defaultHeight)
    {
        if (!IsModuleFragment(widgetType))
        {
            if (appKey is not null || fragmentPath is not null || defaultWidth is not null || defaultHeight is not null)
            {
                throw new InvalidOperationException(
                    $"Dashboard widget '{widgetKey}': appKey, fragmentPath, defaultWidth and defaultHeight are only valid for widgetType '{WidgetType}'.");
            }

            var text = payload?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        if (!string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidOperationException(
                $"Dashboard widget '{widgetKey}' of type '{WidgetType}' must use appKey and fragmentPath instead of payload.");
        }

        var cleanAppKey = appKey?.Trim();
        if (string.IsNullOrEmpty(cleanAppKey))
        {
            throw new InvalidOperationException(
                $"Dashboard widget '{widgetKey}': appKey is required for {WidgetType} widgets.");
        }

        if (cleanAppKey.Length > MaxAppKeyLength
            || !cleanAppKey.All(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or ':' or '-'))
        {
            throw new InvalidOperationException(
                $"Dashboard widget '{widgetKey}': appKey must be at most {MaxAppKeyLength} characters and may only contain letters, digits, period, underscore, colon, or hyphen.");
        }

        var pathError = GetFragmentPathError(fragmentPath);
        if (pathError is not null)
        {
            throw new InvalidOperationException($"Dashboard widget '{widgetKey}': {pathError}");
        }

        ValidateDefaultSize(widgetKey, "defaultWidth", defaultWidth, MinWidth, MaxWidth);
        ValidateDefaultSize(widgetKey, "defaultHeight", defaultHeight, MinHeight, MaxHeight);

        return Serialize(new ModuleFragmentWidgetConfig(
            cleanAppKey,
            fragmentPath!.Trim(),
            defaultWidth,
            defaultHeight));
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

    private static void ValidateDefaultSize(string widgetKey, string propertyName, int? value, int min, int max)
    {
        if (value is { } size && (size < min || size > max))
        {
            throw new InvalidOperationException(
                $"Dashboard widget '{widgetKey}': {propertyName} must be between {min} and {max}.");
        }
    }
}
