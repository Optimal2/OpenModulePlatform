// File: OpenModulePlatform.Portal/Services/PortalModuleDashboardService.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;
using System.Globalization;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Loads small dashboard summaries from optional module schemas.
/// </summary>
public sealed class PortalModuleDashboardService
{
    private const string LogSearchViewPermission = "LogSearch.View";
    private const string LogSearchAdminPermission = "LogSearch.Admin";
    private const string LogSearchAppKey = "log_search_web";
    private const string LogSearchFallbackHref = "/logsearch";
    private const string EArkivCheckerViewPermission = "EArkivChecker.View";
    private const string EArkivCheckerAdminPermission = "EArkivChecker.Admin";
    private const string EArkivCheckerAppKey = "earkiv_checker_web";
    private const string EArkivCheckerFallbackHref = "/earkivchecker";
    private const int DefaultRowCount = 5;

    private readonly SqlConnectionFactory _db;
    private readonly AppCatalogService _catalog;

    public PortalModuleDashboardService(SqlConnectionFactory db, AppCatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<DashboardLogSearchWidget> GetLogSearchWidgetAsync(
        HttpRequest request,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        var href = await ResolveAppHrefAsync(request, permissions, LogSearchAppKey, LogSearchFallbackHref, ct);
        if (!HasAnyPermission(permissions, LogSearchViewPermission, LogSearchAdminPermission))
        {
            return new DashboardLogSearchWidget(href, []);
        }

        return new DashboardLogSearchWidget(href, await LoadRecentLogSearchJobsAsync(DefaultRowCount, ct));
    }

    public async Task<DashboardEArkivCheckerWidget> GetEArkivCheckerWidgetAsync(
        HttpRequest request,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        var href = await ResolveAppHrefAsync(request, permissions, EArkivCheckerAppKey, EArkivCheckerFallbackHref, ct);
        if (!HasAnyPermission(permissions, EArkivCheckerViewPermission, EArkivCheckerAdminPermission))
        {
            return new DashboardEArkivCheckerWidget(href, 0, 0, null, []);
        }

        var summary = await LoadEArkivCheckerSummaryAsync(ct);
        var problems = await LoadEArkivCheckerProblemsAsync(DefaultRowCount, ct);
        return new DashboardEArkivCheckerWidget(
            href,
            summary.TargetCount,
            summary.ProblemCount,
            summary.LastCheckedUtc,
            problems);
    }

    private async Task<IReadOnlyList<DashboardLogSearchJob>> LoadRecentLogSearchJobsAsync(
        int count,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp_log_search.SearchJobs', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS bigint) AS SearchJobId,
           CAST(NULL AS tinyint) AS SearchMode,
           CAST(NULL AS nvarchar(100)) AS PersonIdentifier,
           CAST(NULL AS tinyint) AS Status,
           CAST(NULL AS datetime2(3)) AS RequestedUtc,
           CAST(NULL AS datetime2(3)) AS CompletedUtc,
           CAST(NULL AS int) AS HitCount,
           CAST(NULL AS int) AS ErrorCount
    WHERE 1 = 0;
    RETURN;
END;

SELECT TOP (@count)
       SearchJobId,
       SearchMode,
       PersonIdentifier,
       Status,
       RequestedUtc,
       CompletedUtc,
       HitCount,
       ErrorCount
FROM omp_log_search.SearchJobs
ORDER BY SearchJobId DESC;";

        var rows = new List<DashboardLogSearchJob>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@count", SqlDbType.Int).Value = Math.Clamp(count, 1, 20);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new DashboardLogSearchJob(
                rdr.GetInt64(0),
                rdr.GetByte(1),
                rdr.GetString(2),
                rdr.GetByte(3),
                rdr.GetDateTime(4),
                rdr.IsDBNull(5) ? null : rdr.GetDateTime(5),
                rdr.GetInt32(6),
                rdr.GetInt32(7)));
        }

        return rows;
    }

    private async Task<EArkivCheckerSummary> LoadEArkivCheckerSummaryAsync(CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp_earkiv_checker.ScanTargets', N'U') IS NULL
   OR OBJECT_ID(N'omp_earkiv_checker.TargetStatuses', N'U') IS NULL
BEGIN
    SELECT CAST(0 AS int) AS TargetCount,
           CAST(0 AS int) AS ProblemCount,
           CAST(NULL AS datetime2(3)) AS LastCheckedUtc;
    RETURN;
END;

WITH target_rows AS
(
    SELECT target.TargetId,
           status.Status,
           status.IsOverLimit,
           status.LastCheckedUtc
    FROM omp_earkiv_checker.ScanTargets target
    LEFT JOIN omp_earkiv_checker.TargetStatuses status ON status.TargetId = target.TargetId
    WHERE target.IsEnabled = 1
)
SELECT COUNT(1) AS TargetCount,
       SUM(CASE WHEN COALESCE(Status, 0) IN (2, 3, 4, 5) OR COALESCE(IsOverLimit, 0) = 1 THEN 1 ELSE 0 END) AS ProblemCount,
       MAX(LastCheckedUtc) AS LastCheckedUtc
FROM target_rows;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return new EArkivCheckerSummary(0, 0, null);
        }

        return new EArkivCheckerSummary(
            rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0),
            rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1),
            rdr.IsDBNull(2) ? null : rdr.GetDateTime(2));
    }

    private async Task<IReadOnlyList<DashboardEArkivCheckerProblem>> LoadEArkivCheckerProblemsAsync(
        int count,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp_earkiv_checker.ScanGroups', N'U') IS NULL
   OR OBJECT_ID(N'omp_earkiv_checker.ScanTargets', N'U') IS NULL
   OR OBJECT_ID(N'omp_earkiv_checker.TargetStatuses', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS int) AS TargetId,
           CAST(NULL AS nvarchar(200)) AS GroupDisplayName,
           CAST(NULL AS nvarchar(200)) AS DisplayName,
           CAST(NULL AS nvarchar(1000)) AS Path,
           CAST(NULL AS tinyint) AS Status,
           CAST(NULL AS datetime2(3)) AS LastCheckedUtc,
           CAST(NULL AS bit) AS IsOverLimit,
           CAST(NULL AS nvarchar(500)) AS OverLimitReason,
           CAST(NULL AS nvarchar(max)) AS ErrorMessage
    WHERE 1 = 0;
    RETURN;
END;

SELECT TOP (@count)
       target.TargetId,
       groups.DisplayName AS GroupDisplayName,
       target.DisplayName,
       target.Path,
       status.Status,
       status.LastCheckedUtc,
       status.IsOverLimit,
       status.OverLimitReason,
       status.ErrorMessage
FROM omp_earkiv_checker.ScanTargets target
INNER JOIN omp_earkiv_checker.ScanGroups groups ON groups.GroupId = target.GroupId
LEFT JOIN omp_earkiv_checker.TargetStatuses status ON status.TargetId = target.TargetId
WHERE target.IsEnabled = 1
  AND (COALESCE(status.Status, 0) IN (2, 3, 4, 5) OR COALESCE(status.IsOverLimit, 0) = 1)
ORDER BY CASE COALESCE(status.Status, 0)
             WHEN 5 THEN 1
             WHEN 4 THEN 2
             WHEN 3 THEN 3
             WHEN 2 THEN 4
             ELSE 5
         END,
         status.LastCheckedUtc DESC,
         target.SortOrder,
         target.DisplayName;";

        var rows = new List<DashboardEArkivCheckerProblem>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@count", SqlDbType.Int).Value = Math.Clamp(count, 1, 20);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new DashboardEArkivCheckerProblem(
                rdr.GetInt32(0),
                rdr.GetString(1),
                rdr.GetString(2),
                rdr.GetString(3),
                rdr.IsDBNull(4) ? (byte)0 : rdr.GetByte(4),
                rdr.IsDBNull(5) ? null : rdr.GetDateTime(5),
                !rdr.IsDBNull(6) && Convert.ToBoolean(rdr.GetValue(6), CultureInfo.InvariantCulture),
                rdr.IsDBNull(7) ? null : rdr.GetString(7),
                rdr.IsDBNull(8) ? null : rdr.GetString(8)));
        }

        return rows;
    }

    private async Task<string> ResolveAppHrefAsync(
        HttpRequest request,
        IReadOnlySet<string> permissions,
        string appKey,
        string fallbackHref,
        CancellationToken ct)
    {
        var apps = await _catalog.GetEnabledWebAppsAsync(ct);
        var app = _catalog.FilterByPermissions(apps, permissions)
            .Where(item => string.Equals(item.AppKey, appKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return app is null
            ? fallbackHref
            : AppLinkBuilder.ResolveHref(request, app) ?? fallbackHref;
    }

    private static bool HasAnyPermission(IReadOnlySet<string> permissions, params string[] candidates)
        => candidates.Any(permissions.Contains);

    private sealed record EArkivCheckerSummary(int TargetCount, int ProblemCount, DateTime? LastCheckedUtc);
}
