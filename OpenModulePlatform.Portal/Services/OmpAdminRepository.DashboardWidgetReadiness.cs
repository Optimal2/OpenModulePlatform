using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Services;

public sealed partial class OmpAdminRepository
{
    /// <summary>
    /// Checks stored definitions on every maintenance-page load, including rows imported
    /// before HostAgent understood module-fragment payloads. No scheduled scan is required.
    /// </summary>
    public async Task<IReadOnlyList<DashboardWidgetReadinessIssue>> GetDashboardWidgetReadinessIssuesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT widget_id, widget_key, widget_version, is_enabled, payload
            FROM omp_portal.widgets
            WHERE widget_type = N'module-fragment'
            ORDER BY widget_id;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
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
        return issues;
    }
}
