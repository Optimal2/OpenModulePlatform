// File: OpenModulePlatform.Portal/Models/DashboardModuleWidgets.cs
namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// Compact LogSearch dashboard data rendered by a module-owned Portal widget.
/// </summary>
public sealed record DashboardLogSearchWidget(
    string ModuleHref,
    IReadOnlyList<DashboardLogSearchJob> Jobs);

/// <summary>
/// One recent LogSearch job shown in the dashboard widget.
/// </summary>
public sealed record DashboardLogSearchJob(
    long SearchJobId,
    byte SearchMode,
    string PersonIdentifier,
    byte Status,
    DateTime RequestedUtc,
    DateTime? CompletedUtc,
    int HitCount,
    int ErrorCount);

/// <summary>
/// Compact EArkivChecker dashboard data rendered by a module-owned Portal widget.
/// </summary>
public sealed record DashboardEArkivCheckerWidget(
    string ModuleHref,
    int TargetCount,
    int ProblemCount,
    DateTime? LastCheckedUtc,
    IReadOnlyList<DashboardEArkivCheckerProblem> Problems);

/// <summary>
/// One current EArkivChecker problem target shown in the dashboard widget.
/// </summary>
public sealed record DashboardEArkivCheckerProblem(
    int TargetId,
    string GroupDisplayName,
    string DisplayName,
    string Path,
    byte Status,
    DateTime? LastCheckedUtc,
    bool IsOverLimit,
    string? OverLimitReason,
    string? ErrorMessage);
