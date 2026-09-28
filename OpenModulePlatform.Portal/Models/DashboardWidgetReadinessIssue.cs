using OpenModulePlatform.Portal.Options;

namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// One dashboard widget that needs attention: either a stored module-fragment row whose
/// payload no longer parses, or a widget a HostAgent import skipped (<see cref="IsSkippedImport"/>),
/// which has no stored row and therefore a <see cref="WidgetId"/> of 0.
/// </summary>
public sealed record DashboardWidgetReadinessIssue(int WidgetId, string WidgetKey, string WidgetVersion, bool IsEnabled)
{
    /// <summary>The validation message the HostAgent import recorded for a skipped widget.</summary>
    public string? SkipReason { get; init; }

    /// <summary>When a HostAgent import last skipped the widget.</summary>
    public DateTime? SkippedUtc { get; init; }

    public bool IsSkippedImport => SkipReason is not null;
}

/// <summary>
/// Maintenance-page readiness of dashboard widgets: definition issues plus the
/// module-fragment endpoint issue evaluated for the current request.
/// </summary>
public sealed record DashboardWidgetReadiness(
    IReadOnlyList<DashboardWidgetReadinessIssue> DefinitionIssues,
    ModuleFragmentEndpointIssue EndpointIssue);
