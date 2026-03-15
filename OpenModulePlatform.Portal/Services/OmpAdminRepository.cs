// File: OpenModulePlatform.Portal/Services/OmpAdminRepository.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Services;

public sealed class OmpAdminRepository
{
    private readonly SqlConnectionFactory _db;

    public OmpAdminRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<OverviewMetrics> GetOverviewAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        return new OverviewMetrics
        {
            InstanceCount = await ScalarIntAsync(conn, "SELECT COUNT(1) FROM omp.Instances;", ct),
            ModuleCount = await ScalarIntAsync(conn, "SELECT COUNT(1) FROM omp.Modules;", ct),
            ModuleInstanceCount = await ScalarIntAsync(conn, "SELECT COUNT(1) FROM omp.ModuleInstances;", ct),
            AppCount = await ScalarIntAsync(conn, "SELECT COUNT(1) FROM omp.Apps;", ct),
            AppInstanceCount = await ScalarIntAsync(conn, "SELECT COUNT(1) FROM omp.AppInstances;", ct),
            ArtifactCount = await ScalarIntAsync(conn, "SELECT COUNT(1) FROM omp.Artifacts;", ct),
            HostCount = await ScalarIntAsync(conn, "SELECT COUNT(1) FROM omp.Hosts;", ct),
            InstanceTemplateCount = await ScalarIntAsync(conn, "SELECT COUNT(1) FROM omp.InstanceTemplates;", ct),
            HostTemplateCount = await ScalarIntAsync(conn, "SELECT COUNT(1) FROM omp.HostTemplates;", ct),
            HostDeploymentAssignmentCount = await ScalarIntAsync(conn, "SELECT COUNT(1) FROM omp.HostDeploymentAssignments;", ct),
            HostDeploymentCount = await ScalarIntAsync(conn, "SELECT COUNT(1) FROM omp.HostDeployments;", ct)
        };
    }

    public async Task<IReadOnlyList<InstanceRow>> GetInstancesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT i.InstanceId, i.InstanceKey, i.DisplayName, i.Description, t.TemplateKey, i.IsEnabled, i.CreatedUtc, i.UpdatedUtc
FROM omp.Instances i
LEFT JOIN omp.InstanceTemplates t ON t.InstanceTemplateId = i.InstanceTemplateId
ORDER BY i.InstanceKey;";

        var rows = new List<InstanceRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new InstanceRow
            {
                InstanceId = rdr.GetGuid(0),
                InstanceKey = rdr.GetString(1),
                DisplayName = rdr.GetString(2),
                Description = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                InstanceTemplateKey = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                IsEnabled = rdr.GetBoolean(5),
                CreatedUtc = rdr.GetDateTime(6),
                UpdatedUtc = rdr.GetDateTime(7)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<ModuleRow>> GetModulesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT ModuleId, ModuleKey, DisplayName, ModuleType, SchemaName, Description, IsEnabled, SortOrder
FROM omp.Modules
ORDER BY SortOrder, ModuleKey;";

        var rows = new List<ModuleRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ModuleRow
            {
                ModuleId = rdr.GetInt32(0),
                ModuleKey = rdr.GetString(1),
                DisplayName = rdr.GetString(2),
                ModuleType = rdr.GetString(3),
                SchemaName = rdr.GetString(4),
                Description = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                IsEnabled = rdr.GetBoolean(6),
                SortOrder = rdr.GetInt32(7)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<ModuleInstanceRow>> GetModuleInstancesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT mi.ModuleInstanceId, i.InstanceKey, m.ModuleKey, mi.ModuleInstanceKey, mi.DisplayName, mi.IsEnabled, mi.SortOrder
FROM omp.ModuleInstances mi
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
INNER JOIN omp.Modules m ON m.ModuleId = mi.ModuleId
ORDER BY i.InstanceKey, mi.SortOrder, mi.ModuleInstanceKey;";

        var rows = new List<ModuleInstanceRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ModuleInstanceRow
            {
                ModuleInstanceId = rdr.GetGuid(0),
                InstanceKey = rdr.GetString(1),
                ModuleKey = rdr.GetString(2),
                ModuleInstanceKey = rdr.GetString(3),
                DisplayName = rdr.GetString(4),
                IsEnabled = rdr.GetBoolean(5),
                SortOrder = rdr.GetInt32(6)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<AppRow>> GetAppsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT a.AppId, m.ModuleKey, a.AppKey, a.DisplayName, a.AppType, a.Description, a.IsEnabled, a.SortOrder
FROM omp.Apps a
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
ORDER BY m.ModuleKey, a.SortOrder, a.AppKey;";

        var rows = new List<AppRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new AppRow
            {
                AppId = rdr.GetInt32(0),
                ModuleKey = rdr.GetString(1),
                AppKey = rdr.GetString(2),
                DisplayName = rdr.GetString(3),
                AppType = rdr.GetString(4),
                Description = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                IsEnabled = rdr.GetBoolean(6),
                SortOrder = rdr.GetInt32(7)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<AppInstanceRow>> GetAppInstancesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT ai.AppInstanceId,
       i.InstanceKey,
       mi.ModuleInstanceKey,
       a.AppKey,
       ai.AppInstanceKey,
       ai.DisplayName,
       a.AppType,
       h.HostKey,
       ai.RoutePath,
       ai.InstallationName,
       ai.ArtifactId,
       ar.Version,
       ai.IsAllowed,
       ai.DesiredState,
       ai.LastSeenUtc,
       ai.VerificationStatus
FROM omp.AppInstances ai
INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
LEFT JOIN omp.Hosts h ON h.HostId = ai.HostId
LEFT JOIN omp.Artifacts ar ON ar.ArtifactId = ai.ArtifactId
ORDER BY i.InstanceKey, mi.ModuleInstanceKey, ai.SortOrder, ai.AppInstanceKey;";

        var rows = new List<AppInstanceRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new AppInstanceRow
            {
                AppInstanceId = rdr.GetGuid(0),
                InstanceKey = rdr.GetString(1),
                ModuleInstanceKey = rdr.GetString(2),
                AppKey = rdr.GetString(3),
                AppInstanceKey = rdr.GetString(4),
                DisplayName = rdr.GetString(5),
                AppType = rdr.GetString(6),
                HostKey = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                RoutePath = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                InstallationName = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                ArtifactId = rdr.IsDBNull(10) ? null : rdr.GetInt32(10),
                ArtifactVersion = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                IsAllowed = rdr.GetBoolean(12),
                DesiredState = rdr.GetByte(13),
                LastSeenUtc = rdr.IsDBNull(14) ? null : rdr.GetDateTime(14),
                VerificationStatus = rdr.GetByte(15)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<ArtifactRow>> GetArtifactsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT ar.ArtifactId, a.AppKey, ar.Version, ar.PackageType, ar.TargetName, ar.RelativePath, ar.IsEnabled, ar.CreatedUtc
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
ORDER BY a.AppKey, ar.CreatedUtc DESC, ar.ArtifactId DESC;";

        var rows = new List<ArtifactRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ArtifactRow
            {
                ArtifactId = rdr.GetInt32(0),
                AppKey = rdr.GetString(1),
                Version = rdr.GetString(2),
                PackageType = rdr.GetString(3),
                TargetName = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                RelativePath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                IsEnabled = rdr.GetBoolean(6),
                CreatedUtc = rdr.GetDateTime(7)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<HostRow>> GetHostsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT h.HostId, i.InstanceKey, h.HostKey, h.DisplayName, h.Environment, h.OsFamily, h.OsVersion, h.Architecture,
       h.IsEnabled, h.LastSeenUtc
FROM omp.Hosts h
INNER JOIN omp.Instances i ON i.InstanceId = h.InstanceId
ORDER BY i.InstanceKey, h.HostKey;";

        var rows = new List<HostRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostRow
            {
                HostId = rdr.GetGuid(0),
                InstanceKey = rdr.GetString(1),
                HostKey = rdr.GetString(2),
                DisplayName = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                Environment = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                OsFamily = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                OsVersion = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                Architecture = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                IsEnabled = rdr.GetBoolean(8),
                LastSeenUtc = rdr.IsDBNull(9) ? null : rdr.GetDateTime(9)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<InstanceTemplateRow>> GetInstanceTemplatesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT InstanceTemplateId, TemplateKey, DisplayName, Description, IsEnabled
FROM omp.InstanceTemplates
ORDER BY TemplateKey;";

        var rows = new List<InstanceTemplateRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new InstanceTemplateRow
            {
                InstanceTemplateId = rdr.GetInt32(0),
                TemplateKey = rdr.GetString(1),
                DisplayName = rdr.GetString(2),
                Description = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                IsEnabled = rdr.GetBoolean(4)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<HostTemplateRow>> GetHostTemplatesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT HostTemplateId, TemplateKey, DisplayName, Description, IsEnabled
FROM omp.HostTemplates
ORDER BY TemplateKey;";

        var rows = new List<HostTemplateRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostTemplateRow
            {
                HostTemplateId = rdr.GetInt32(0),
                TemplateKey = rdr.GetString(1),
                DisplayName = rdr.GetString(2),
                Description = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                IsEnabled = rdr.GetBoolean(4)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<HostDeploymentAssignmentRow>> GetHostDeploymentAssignmentsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT a.HostDeploymentAssignmentId, h.HostKey, t.TemplateKey, a.IsActive, a.AssignedBy, a.AssignedUtc
FROM omp.HostDeploymentAssignments a
INNER JOIN omp.Hosts h ON h.HostId = a.HostId
INNER JOIN omp.HostTemplates t ON t.HostTemplateId = a.HostTemplateId
ORDER BY h.HostKey, a.AssignedUtc DESC;";

        var rows = new List<HostDeploymentAssignmentRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostDeploymentAssignmentRow
            {
                HostDeploymentAssignmentId = rdr.GetInt64(0),
                HostKey = rdr.GetString(1),
                HostTemplateKey = rdr.GetString(2),
                IsActive = rdr.GetBoolean(3),
                AssignedBy = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                AssignedUtc = rdr.GetDateTime(5)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<HostDeploymentRow>> GetHostDeploymentsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT d.HostDeploymentId, h.HostKey, t.TemplateKey, d.Status, d.RequestedBy, d.RequestedUtc, d.StartedUtc, d.CompletedUtc, d.OutcomeMessage
FROM omp.HostDeployments d
INNER JOIN omp.Hosts h ON h.HostId = d.HostId
LEFT JOIN omp.HostTemplates t ON t.HostTemplateId = d.HostTemplateId
ORDER BY d.RequestedUtc DESC, d.HostDeploymentId DESC;";

        var rows = new List<HostDeploymentRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostDeploymentRow
            {
                HostDeploymentId = rdr.GetInt64(0),
                HostKey = rdr.GetString(1),
                HostTemplateKey = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                Status = rdr.GetByte(3),
                RequestedBy = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                RequestedUtc = rdr.GetDateTime(5),
                StartedUtc = rdr.IsDBNull(6) ? null : rdr.GetDateTime(6),
                CompletedUtc = rdr.IsDBNull(7) ? null : rdr.GetDateTime(7),
                OutcomeMessage = rdr.IsDBNull(8) ? null : rdr.GetString(8)
            });
        }
        return rows;
    }

    private static async Task<int> ScalarIntAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is int i ? i : Convert.ToInt32(value ?? 0);
    }
}
