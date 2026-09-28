namespace OpenModulePlatform.Artifacts;

/// <summary>
/// The dashboard widget types this platform version can import and render.
/// </summary>
/// <remarks>
/// Both import paths (through <see cref="ModuleFragmentWidgetPayload.NormalizeDefinition"/>)
/// and the Portal dashboard's orphaned-widget check read this list. A new widget type must be
/// added here, together with its normalization and render path, before any definition of that
/// type can be imported; until then imports reject or skip it. See docs/UNIVERSAL_MODULE_PACKAGES.md.
/// </remarks>
public static class DashboardWidgetTypes
{
    public const string Portal = "portal";

    public const string ModuleFragment = ModuleFragmentWidgetPayload.WidgetType;

    public static IReadOnlyList<string> Known { get; } = [Portal, ModuleFragment];

    public static bool IsKnown(string? widgetType)
    {
        var type = widgetType?.Trim();
        return Known.Any(known => string.Equals(type, known, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPortal(string? widgetType)
        => string.Equals(widgetType?.Trim(), Portal, StringComparison.OrdinalIgnoreCase);
}
