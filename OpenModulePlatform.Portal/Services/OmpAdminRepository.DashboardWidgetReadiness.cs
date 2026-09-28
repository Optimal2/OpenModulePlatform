using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Services;

public sealed partial class OmpAdminRepository
{
    // Written by the HostAgent import (OmpHostArtifactRepository.DashboardWidgetImportSkippedCategory).
    internal const string DashboardWidgetImportSkippedCategory = "DashboardWidgetImportSkipped";

    /// <summary>
    /// Checks stored definitions on every maintenance-page load, including rows imported
    /// before HostAgent understood module-fragment payloads. No scheduled scan is required.
    /// Also returns widgets a HostAgent import skipped as invalid, recorded as open
    /// maintenance findings, until a later import of the same widget key succeeds.
    /// </summary>
    public async Task<IReadOnlyList<DashboardWidgetReadinessIssue>> GetDashboardWidgetReadinessIssuesAsync(CancellationToken ct)
    {
        // A skipped widget is also hidden once a Portal import has stored the same key
        // after the skip, because only HostAgent imports close these findings.
        const string sql = """
            SELECT widget_id, widget_key, widget_version, is_enabled, payload
            FROM omp_portal.widgets
            WHERE widget_type = N'module-fragment'
            ORDER BY widget_id;

            IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NULL
                SELECT TOP (0) CAST(NULL AS nvarchar(1000)) AS TargetIdentifier,
                       CAST(NULL AS nvarchar(max)) AS Detail, CAST(NULL AS datetime2(3)) AS LastSeenUtc;
            ELSE
                SELECT finding.TargetIdentifier, finding.Detail, finding.LastSeenUtc
                FROM omp.MaintenanceFindings finding
                WHERE finding.Category = @category
                  AND finding.Status = 0
                  AND NOT EXISTS (
                      SELECT 1 FROM omp_portal.widgets widget
                      WHERE widget.widget_key = finding.TargetIdentifier
                        AND widget.modified_at > finding.LastSeenUtc)
                ORDER BY finding.LastSeenUtc DESC, finding.MaintenanceFindingId DESC;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@category", System.Data.SqlDbType.NVarChar, 100).Value = DashboardWidgetImportSkippedCategory;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var issues = new List<DashboardWidgetReadinessIssue>();
        while (await reader.ReadAsync(ct))
        {
            var payload = reader.IsDBNull(4) ? null : reader.GetString(4);
            if (ModuleFragmentWidgetPayload.TryParse(payload) is null)
            {
                issues.Add(new DashboardWidgetReadinessIssue(reader.GetInt32(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? "0.0.0" : reader.GetString(2), reader.GetBoolean(3)));
            }
        }

        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            issues.Add(new DashboardWidgetReadinessIssue(0, reader.GetString(0), string.Empty, false)
            {
                SkipReason = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                SkippedUtc = reader.GetDateTime(2)
            });
        }

        return issues;
    }
}
