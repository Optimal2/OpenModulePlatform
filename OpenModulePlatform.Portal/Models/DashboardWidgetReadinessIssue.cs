namespace OpenModulePlatform.Portal.Models;

public sealed record DashboardWidgetReadinessIssue(int WidgetId, string WidgetKey, string WidgetVersion, bool IsEnabled);
