using System.Collections.Concurrent;

namespace OpenModulePlatform.Portal.Services;

internal static class DashboardWidgetPayloadDiagnostics
{
    // Upper bound on remembered widgets; reaching it clears the map, which at worst
    // repeats one warning per invalid widget.
    internal const int MaxTrackedWidgets = 4096;

    // Shared by all render paths and service instances for the lifetime of this process.
    // Maps a widget id to the fingerprint of the definition last warned about, so a
    // changed definition warns again. Only type and payload fingerprint are kept, never
    // payload text.
    private static readonly ConcurrentDictionary<int, string> WarnedDefinitions = new();

    internal static int TrackedWidgetCount => WarnedDefinitions.Count;

    public static void WarnOnce(ILogger logger, int widgetId, string widgetType, string? payload)
    {
        var fingerprint = $"{widgetType}|{(payload is null ? "null" : PortalDashboardService.PayloadFingerprint(payload))}";
        if (WarnedDefinitions.TryGetValue(widgetId, out var warned)
            && string.Equals(warned, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        if (WarnedDefinitions.Count >= MaxTrackedWidgets)
        {
            WarnedDefinitions.Clear();
        }

        // Only the caller that changes the stored fingerprint logs, so concurrent
        // renders of the same definition still produce a single warning.
        var stored = warned is null
            ? WarnedDefinitions.TryAdd(widgetId, fingerprint)
            : WarnedDefinitions.TryUpdate(widgetId, fingerprint, warned);
        if (stored)
        {
            logger.LogWarning(
                "Dashboard widget {WidgetId} has an invalid payload for widget type {WidgetType}. Review its definition in Portal administration; module-fragment repair guidance is available under Maintenance.",
                widgetId, widgetType);
        }
    }

    /// <summary>
    /// Called when a widget's definition renders correctly, so the same widget warns
    /// again if it later breaks, even with a payload it had before.
    /// </summary>
    public static void Forget(int widgetId) => WarnedDefinitions.TryRemove(widgetId, out _);
}
