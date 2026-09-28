using System.Collections.Concurrent;

namespace OpenModulePlatform.Portal.Services;

internal static class DashboardWidgetPayloadDiagnostics
{
    // Shared by all render paths and service instances for the lifetime of this process.
    private static readonly ConcurrentDictionary<int, byte> WarnedWidgetIds = new();

    public static void WarnOnce(ILogger logger, int widgetId, string widgetType)
    {
        if (WarnedWidgetIds.TryAdd(widgetId, 0))
        {
            logger.LogWarning(
                "Dashboard widget {WidgetId} has an invalid payload for widget type {WidgetType}. Review its definition in Portal administration; module-fragment repair guidance is available under Maintenance.",
                widgetId, widgetType);
        }
    }
}
