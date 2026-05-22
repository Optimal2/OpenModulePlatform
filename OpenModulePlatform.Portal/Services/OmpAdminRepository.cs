// File: OpenModulePlatform.Portal/Services/OmpAdminRepository.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Services;

public sealed partial class OmpAdminRepository
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
            AppWorkerDefinitionCount = await ConditionalScalarIntAsync(conn, "omp.AppWorkerDefinitions", "SELECT COUNT(1) FROM omp.AppWorkerDefinitions;", ct),
            AppInstanceRuntimeStateCount = await ConditionalScalarIntAsync(conn, "omp.AppInstanceRuntimeStates", "SELECT COUNT(1) FROM omp.AppInstanceRuntimeStates;", ct),
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
                DisplayName = rdr.IsDBNull(3) ? rdr.GetString(1) : rdr.GetString(3),
                AppType = rdr.GetString(4),
                Description = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                IsEnabled = rdr.GetBoolean(6),
                SortOrder = rdr.GetInt32(7)
            });
        }
        return rows;
    }


    public async Task<IReadOnlyList<AppWorkerDefinitionRow>> GetAppWorkerDefinitionsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT awd.AppId,
       m.ModuleKey,
       a.AppKey,
       a.DisplayName,
       a.AppType,
       awd.RuntimeKind,
       awd.WorkerTypeKey,
       awd.PluginRelativePath,
       awd.IsEnabled
FROM omp.AppWorkerDefinitions awd
INNER JOIN omp.Apps a ON a.AppId = awd.AppId
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
ORDER BY m.ModuleKey, a.SortOrder, a.AppKey;";

        var rows = new List<AppWorkerDefinitionRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await TableExistsAsync(conn, "omp.AppWorkerDefinitions", ct))
        {
            return rows;
        }

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new AppWorkerDefinitionRow
            {
                AppId = rdr.GetInt32(0),
                ModuleKey = rdr.GetString(1),
                AppKey = rdr.GetString(2),
                DisplayName = rdr.GetString(3),
                AppType = rdr.GetString(4),
                RuntimeKind = rdr.IsDBNull(5) ? string.Empty : rdr.GetString(5),
                WorkerTypeKey = rdr.IsDBNull(6) ? string.Empty : rdr.GetString(6),
                PluginRelativePath = rdr.IsDBNull(7) ? string.Empty : rdr.GetString(7),
                IsEnabled = rdr.GetBoolean(8)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<AppWorkerRuntimeRow>> GetAppWorkerRuntimeAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT ai.AppInstanceId,
       tai.InstanceTemplateAppInstanceId,
       i.InstanceTemplateId,
       i.InstanceKey,
       mi.ModuleInstanceKey,
       a.AppKey,
       ai.AppInstanceKey,
       ai.DisplayName,
       h.HostKey,
       ai.IsAllowed,
       ai.DesiredState,
       awd.RuntimeKind,
       awd.WorkerTypeKey,
       awd.PluginRelativePath,
       CAST(ISNULL(rs.ObservedState, CAST(0 AS tinyint)) AS tinyint),
       rs.ProcessId,
       rs.StartedUtc,
       rs.LastSeenUtc,
       rs.LastExitUtc,
       rs.LastExitCode,
       rs.StatusMessage
FROM omp.AppInstances ai
INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
INNER JOIN omp.AppWorkerDefinitions awd ON awd.AppId = ai.AppId
LEFT JOIN omp.Hosts h ON h.HostId = ai.HostId
LEFT JOIN omp.AppInstanceRuntimeStates rs ON rs.AppInstanceId = ai.AppInstanceId
LEFT JOIN omp.InstanceTemplateModuleInstances tmi
    ON tmi.InstanceTemplateId = i.InstanceTemplateId
   AND tmi.ModuleId = mi.ModuleId
   AND tmi.ModuleInstanceKey = mi.ModuleInstanceKey
LEFT JOIN omp.InstanceTemplateAppInstances tai
    ON tai.InstanceTemplateModuleInstanceId = tmi.InstanceTemplateModuleInstanceId
   AND tai.AppInstanceKey = ai.AppInstanceKey
ORDER BY i.InstanceKey, mi.ModuleInstanceKey, ai.SortOrder, ai.AppInstanceKey;";

        var rows = new List<AppWorkerRuntimeRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await TableExistsAsync(conn, "omp.AppWorkerDefinitions", ct))
        {
            return rows;
        }

        var hasRuntimeTable = await TableExistsAsync(conn, "omp.AppInstanceRuntimeStates", ct);
        var effectiveSql = hasRuntimeTable
            ? sql
            : sql
                .Replace("LEFT JOIN omp.AppInstanceRuntimeStates rs ON rs.AppInstanceId = ai.AppInstanceId", string.Empty)
                .Replace("CAST(ISNULL(rs.ObservedState, CAST(0 AS tinyint)) AS tinyint)", "CAST(0 AS tinyint)")
                .Replace("rs.ProcessId", "CAST(NULL AS int)")
                .Replace("rs.StartedUtc", "CAST(NULL AS datetime2(3))")
                .Replace("rs.LastSeenUtc", "CAST(NULL AS datetime2(3))")
                .Replace("rs.LastExitUtc", "CAST(NULL AS datetime2(3))")
                .Replace("rs.LastExitCode", "CAST(NULL AS int)")
                .Replace("rs.StatusMessage", "CAST(NULL AS nvarchar(500))");

        await using var cmd = new SqlCommand(effectiveSql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new AppWorkerRuntimeRow
            {
                AppInstanceId = rdr.GetGuid(0),
                InstanceTemplateAppInstanceId = rdr.IsDBNull(1) ? null : rdr.GetInt32(1),
                InstanceTemplateId = rdr.IsDBNull(2) ? null : rdr.GetInt32(2),
                InstanceKey = rdr.GetString(3),
                ModuleInstanceKey = rdr.GetString(4),
                AppKey = rdr.GetString(5),
                AppInstanceKey = rdr.GetString(6),
                DisplayName = rdr.GetString(7),
                HostKey = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                IsAllowed = rdr.GetBoolean(9),
                DesiredState = rdr.GetByte(10),
                RuntimeKind = rdr.IsDBNull(11) ? string.Empty : rdr.GetString(11),
                WorkerTypeKey = rdr.IsDBNull(12) ? string.Empty : rdr.GetString(12),
                PluginRelativePath = rdr.IsDBNull(13) ? string.Empty : rdr.GetString(13),
                ObservedState = rdr.GetByte(14),
                ProcessId = rdr.IsDBNull(15) ? null : rdr.GetInt32(15),
                StartedUtc = rdr.IsDBNull(16) ? null : rdr.GetDateTime(16),
                LastSeenUtc = rdr.IsDBNull(17) ? null : rdr.GetDateTime(17),
                LastExitUtc = rdr.IsDBNull(18) ? null : rdr.GetDateTime(18),
                LastExitCode = rdr.IsDBNull(19) ? null : rdr.GetInt32(19),
                StatusMessage = rdr.IsDBNull(20) ? null : rdr.GetString(20)
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
SELECT ar.ArtifactId, m.ModuleKey, a.AppKey, ar.Version, ar.PackageType, ar.TargetName, ar.RelativePath, ar.IsEnabled, ar.CreatedUtc
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
ORDER BY m.ModuleKey, a.AppKey, ar.CreatedUtc DESC, ar.ArtifactId DESC;";

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
                ModuleKey = rdr.GetString(1),
                AppKey = rdr.GetString(2),
                Version = rdr.GetString(3),
                PackageType = rdr.GetString(4),
                TargetName = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                RelativePath = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                IsEnabled = rdr.GetBoolean(7),
                CreatedUtc = rdr.GetDateTime(8)
            });
        }
        return rows;
    }

    public async Task<ModuleDefinitionDocumentRow?> GetAppliedModuleDefinitionDocumentAsync(
        string moduleKey,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) d.ModuleDefinitionDocumentId,
       d.ModuleKey,
       d.DefinitionVersion,
       d.FormatVersion,
       d.DefinitionSha256,
       d.DefinitionJson,
       d.SourceName,
       d.IsApplied,
       d.AppliedUtc,
       d.CreatedUtc,
       d.UpdatedUtc,
       (
           SELECT COUNT(1)
           FROM omp.ModuleDefinitionArtifactCompatibility c
           WHERE c.ModuleDefinitionDocumentId = d.ModuleDefinitionDocumentId
       ) AS CompatibleArtifactSlotCount
FROM omp.ModuleDefinitionDocuments d
WHERE d.ModuleKey = @ModuleKey
  AND d.IsApplied = 1
ORDER BY d.AppliedUtc DESC, d.UpdatedUtc DESC, d.ModuleDefinitionDocumentId DESC;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ModuleKey", moduleKey);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        return await rdr.ReadAsync(ct)
            ? ReadModuleDefinitionDocumentRow(rdr, includeJson: true)
            : null;
    }

    public async Task<IReadOnlyList<ModuleArtifactPackageRow>> GetModuleArtifactPackagesAsync(
        string moduleKey,
        bool includeAllVersions,
        CancellationToken ct)
    {
        var sql = includeAllVersions
            ? @"
SELECT ar.ArtifactId,
       m.ModuleKey,
       a.AppKey,
       ar.Version,
       ar.PackageType,
       ar.TargetName,
       ar.RelativePath,
       ar.IsEnabled
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
WHERE m.ModuleKey = @ModuleKey
ORDER BY a.AppKey, ar.PackageType, ar.TargetName, ar.CreatedUtc DESC, ar.ArtifactId DESC;"
            : @"
WITH CurrentReferences AS
(
    SELECT tai.DesiredArtifactId AS ArtifactId
    FROM omp.InstanceTemplateAppInstances tai
    WHERE tai.DesiredArtifactId IS NOT NULL

    UNION

    SELECT ai.ArtifactId
    FROM omp.AppInstances ai
    WHERE ai.ArtifactId IS NOT NULL

    UNION

    SELECT wi.ArtifactId
    FROM omp.WorkerInstances wi
    WHERE wi.ArtifactId IS NOT NULL
)
SELECT DISTINCT ar.ArtifactId,
       m.ModuleKey,
       a.AppKey,
       ar.Version,
       ar.PackageType,
       ar.TargetName,
       ar.RelativePath,
       ar.IsEnabled
FROM CurrentReferences refs
INNER JOIN omp.Artifacts ar ON ar.ArtifactId = refs.ArtifactId
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
WHERE m.ModuleKey = @ModuleKey
ORDER BY a.AppKey, ar.PackageType, ar.TargetName, ar.Version;";

        var rows = new List<ModuleArtifactPackageRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ModuleKey", moduleKey);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ModuleArtifactPackageRow
            {
                ArtifactId = rdr.GetInt32(0),
                ModuleKey = rdr.GetString(1),
                AppKey = rdr.GetString(2),
                Version = rdr.GetString(3),
                PackageType = rdr.GetString(4),
                TargetName = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                RelativePath = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                IsEnabled = rdr.GetBoolean(7)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<ModuleDefinitionDocumentRow>> GetModuleDefinitionDocumentsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT d.ModuleDefinitionDocumentId,
       d.ModuleKey,
       d.DefinitionVersion,
       d.FormatVersion,
       d.DefinitionSha256,
       d.SourceName,
       d.IsApplied,
       d.AppliedUtc,
       d.CreatedUtc,
       d.UpdatedUtc,
       COUNT(c.ModuleDefinitionArtifactCompatibilityId) AS CompatibleArtifactSlotCount
FROM omp.ModuleDefinitionDocuments d
LEFT JOIN omp.ModuleDefinitionArtifactCompatibility c
    ON c.ModuleDefinitionDocumentId = d.ModuleDefinitionDocumentId
GROUP BY d.ModuleDefinitionDocumentId,
         d.ModuleKey,
         d.DefinitionVersion,
         d.FormatVersion,
         d.DefinitionSha256,
         d.SourceName,
         d.IsApplied,
         d.AppliedUtc,
         d.CreatedUtc,
         d.UpdatedUtc
ORDER BY d.ModuleKey,
         d.IsApplied DESC,
         d.AppliedUtc DESC,
         d.UpdatedUtc DESC,
         d.ModuleDefinitionDocumentId DESC;";

        var rows = new List<ModuleDefinitionDocumentRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(ReadModuleDefinitionDocumentRow(rdr, includeJson: false));
        }

        return rows;
    }

    public async Task<ModuleDefinitionDocumentRow?> GetModuleDefinitionDocumentAsync(
        int moduleDefinitionDocumentId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT d.ModuleDefinitionDocumentId,
       d.ModuleKey,
       d.DefinitionVersion,
       d.FormatVersion,
       d.DefinitionSha256,
       d.DefinitionJson,
       d.SourceName,
       d.IsApplied,
       d.AppliedUtc,
       d.CreatedUtc,
       d.UpdatedUtc,
       (
           SELECT COUNT(1)
           FROM omp.ModuleDefinitionArtifactCompatibility c
           WHERE c.ModuleDefinitionDocumentId = d.ModuleDefinitionDocumentId
       ) AS CompatibleArtifactSlotCount
FROM omp.ModuleDefinitionDocuments d
WHERE d.ModuleDefinitionDocumentId = @ModuleDefinitionDocumentId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ModuleDefinitionDocumentId", moduleDefinitionDocumentId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return ReadModuleDefinitionDocumentRow(rdr, includeJson: true);
    }

    public async Task<IReadOnlyList<ModuleDefinitionCompatibilityRow>> GetModuleDefinitionCompatibilityAsync(
        int moduleDefinitionDocumentId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT ModuleDefinitionArtifactCompatibilityId,
       AppKey,
       PackageType,
       TargetName,
       RelativePathTemplate,
       MinArtifactVersion,
       MaxArtifactVersion
FROM omp.ModuleDefinitionArtifactCompatibility
WHERE ModuleDefinitionDocumentId = @ModuleDefinitionDocumentId
ORDER BY AppKey, PackageType, TargetName, ModuleDefinitionArtifactCompatibilityId;";

        var rows = new List<ModuleDefinitionCompatibilityRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ModuleDefinitionDocumentId", moduleDefinitionDocumentId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ModuleDefinitionCompatibilityRow
            {
                ModuleDefinitionArtifactCompatibilityId = rdr.GetInt32(0),
                AppKey = rdr.GetString(1),
                PackageType = rdr.GetString(2),
                TargetName = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                RelativePathTemplate = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                MinArtifactVersion = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                MaxArtifactVersion = rdr.IsDBNull(6) ? null : rdr.GetString(6)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<ModuleDefinitionArtifactReferenceRow>> GetCurrentArtifactReferencesForModuleAsync(
        string moduleKey,
        CancellationToken ct)
    {
        const string sql = @"
WITH CurrentReferences AS
(
    SELECT N'Installation template' AS ReferenceKind,
           CONVERT(nvarchar(100), tai.InstanceTemplateAppInstanceId) AS ReferenceKey,
           tai.DesiredArtifactId AS ArtifactId
    FROM omp.InstanceTemplateAppInstances tai
    WHERE tai.DesiredArtifactId IS NOT NULL

    UNION ALL

    SELECT N'App instance',
           CONVERT(nvarchar(100), ai.AppInstanceId),
           ai.ArtifactId
    FROM omp.AppInstances ai
    WHERE ai.ArtifactId IS NOT NULL

    UNION ALL

    SELECT N'Worker runtime',
           CONVERT(nvarchar(100), wi.WorkerInstanceId),
           wi.ArtifactId
    FROM omp.WorkerInstances wi
    WHERE wi.ArtifactId IS NOT NULL
)
SELECT DISTINCT
       r.ReferenceKind,
       r.ReferenceKey,
       ar.ArtifactId,
       a.AppKey,
       ar.Version,
       ar.PackageType,
       ar.TargetName
FROM CurrentReferences r
INNER JOIN omp.Artifacts ar ON ar.ArtifactId = r.ArtifactId
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
WHERE m.ModuleKey = @ModuleKey
ORDER BY a.AppKey, ar.Version, r.ReferenceKind, r.ReferenceKey;";

        var rows = new List<ModuleDefinitionArtifactReferenceRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ModuleKey", moduleKey);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ModuleDefinitionArtifactReferenceRow
            {
                ReferenceKind = rdr.GetString(0),
                ReferenceKey = rdr.GetString(1),
                ArtifactId = rdr.GetInt32(2),
                AppKey = rdr.GetString(3),
                Version = rdr.GetString(4),
                PackageType = rdr.GetString(5),
                TargetName = rdr.IsDBNull(6) ? null : rdr.GetString(6)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<ArtifactConfigurationFileRow>> GetArtifactConfigurationFilesAsync(
        int artifactId,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.ArtifactConfigurationFiles', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS int) AS ArtifactConfigurationFileId,
        CAST(NULL AS int) AS ArtifactId,
        CAST(NULL AS nvarchar(400)) AS RelativePath,
        CAST(NULL AS bit) AS IsEnabled,
        CAST(NULL AS datetime2(3)) AS UpdatedUtc;
    RETURN;
END;

SELECT ArtifactConfigurationFileId,
       ArtifactId,
       RelativePath,
       IsEnabled,
       UpdatedUtc
FROM omp.ArtifactConfigurationFiles
WHERE ArtifactId = @ArtifactId
ORDER BY RelativePath, ArtifactConfigurationFileId;";

        var rows = new List<ArtifactConfigurationFileRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ArtifactId", artifactId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ArtifactConfigurationFileRow
            {
                ArtifactConfigurationFileId = rdr.GetInt32(0),
                ArtifactId = rdr.GetInt32(1),
                RelativePath = rdr.GetString(2),
                IsEnabled = rdr.GetBoolean(3),
                UpdatedUtc = rdr.GetDateTime(4)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<HostRow>> GetHostsAsync(CancellationToken ct)
    {
        var rows = new List<HostRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var hasHostBaseUrl = await HostBaseUrlColumnExistsAsync(conn, ct);
        var hostBaseUrlSelect = hasHostBaseUrl
            ? "h.BaseUrl"
            : "CAST(NULL AS nvarchar(300)) AS BaseUrl";

        var sql = $@"
SELECT h.HostId, i.InstanceKey, h.HostKey, h.DisplayName, {hostBaseUrlSelect}, h.Environment, h.OsFamily, h.OsVersion, h.Architecture,
       h.IsEnabled, h.LastSeenUtc
FROM omp.Hosts h
INNER JOIN omp.Instances i ON i.InstanceId = h.InstanceId
ORDER BY i.InstanceKey, h.HostKey;";

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
                BaseUrl = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                Environment = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                OsFamily = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                OsVersion = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                Architecture = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                IsEnabled = rdr.GetBoolean(9),
                LastSeenUtc = rdr.IsDBNull(10) ? null : rdr.GetDateTime(10)
            });
        }
        return rows;
    }

    private static ModuleDefinitionDocumentRow ReadModuleDefinitionDocumentRow(SqlDataReader rdr, bool includeJson)
    {
        var sourceOffset = includeJson ? 1 : 0;
        return new ModuleDefinitionDocumentRow
        {
            ModuleDefinitionDocumentId = rdr.GetInt32(0),
            ModuleKey = rdr.GetString(1),
            DefinitionVersion = rdr.GetString(2),
            FormatVersion = rdr.GetInt32(3),
            DefinitionSha256 = rdr.GetString(4),
            DefinitionJson = includeJson && !rdr.IsDBNull(5) ? rdr.GetString(5) : null,
            SourceName = rdr.IsDBNull(5 + sourceOffset) ? null : rdr.GetString(5 + sourceOffset),
            IsApplied = rdr.GetBoolean(6 + sourceOffset),
            AppliedUtc = rdr.IsDBNull(7 + sourceOffset) ? null : rdr.GetDateTime(7 + sourceOffset),
            CreatedUtc = rdr.GetDateTime(8 + sourceOffset),
            UpdatedUtc = rdr.GetDateTime(9 + sourceOffset),
            CompatibleArtifactSlotCount = rdr.GetInt32(10 + sourceOffset)
        };
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

    public async Task<InstanceTemplateRow?> GetInstanceTemplateAsync(int instanceTemplateId, CancellationToken ct)
    {
        const string sql = @"
SELECT InstanceTemplateId, TemplateKey, DisplayName, Description, IsEnabled
FROM omp.InstanceTemplates
WHERE InstanceTemplateId = @InstanceTemplateId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@InstanceTemplateId", instanceTemplateId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new InstanceTemplateRow
        {
            InstanceTemplateId = rdr.GetInt32(0),
            TemplateKey = rdr.GetString(1),
            DisplayName = rdr.GetString(2),
            Description = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            IsEnabled = rdr.GetBoolean(4)
        };
    }

    public async Task<IReadOnlyList<InstanceTemplateHostTopologyRow>> GetInstanceTemplateHostsAsync(
        int instanceTemplateId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT ith.InstanceTemplateHostId,
       ith.HostKey,
       ht.TemplateKey,
       ith.DisplayName,
       ith.Environment,
       ith.SortOrder,
       ith.IsEnabled
FROM omp.InstanceTemplateHosts ith
INNER JOIN omp.HostTemplates ht ON ht.HostTemplateId = ith.HostTemplateId
WHERE ith.InstanceTemplateId = @InstanceTemplateId
ORDER BY ith.SortOrder, ith.HostKey;";

        var rows = new List<InstanceTemplateHostTopologyRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@InstanceTemplateId", instanceTemplateId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new InstanceTemplateHostTopologyRow
            {
                InstanceTemplateHostId = rdr.GetInt32(0),
                HostKey = rdr.GetString(1),
                HostTemplateKey = rdr.GetString(2),
                DisplayName = rdr.IsDBNull(3) ? rdr.GetString(1) : rdr.GetString(3),
                Environment = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                SortOrder = rdr.GetInt32(5),
                IsEnabled = rdr.GetBoolean(6)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<InstanceTemplateModuleTopologyRow>> GetInstanceTemplateModulesAsync(
        int instanceTemplateId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT tmi.InstanceTemplateModuleInstanceId,
       m.ModuleKey,
       tmi.ModuleInstanceKey,
       tmi.DisplayName,
       tmi.SortOrder,
       tmi.IsEnabled
FROM omp.InstanceTemplateModuleInstances tmi
INNER JOIN omp.Modules m ON m.ModuleId = tmi.ModuleId
WHERE tmi.InstanceTemplateId = @InstanceTemplateId
ORDER BY tmi.SortOrder, tmi.ModuleInstanceKey;";

        var rows = new List<InstanceTemplateModuleTopologyRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@InstanceTemplateId", instanceTemplateId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new InstanceTemplateModuleTopologyRow
            {
                InstanceTemplateModuleInstanceId = rdr.GetInt32(0),
                ModuleKey = rdr.GetString(1),
                ModuleInstanceKey = rdr.GetString(2),
                DisplayName = rdr.GetString(3),
                SortOrder = rdr.GetInt32(4),
                IsEnabled = rdr.GetBoolean(5)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<InstanceTemplateAppTopologyRow>> GetInstanceTemplateAppsAsync(
        int instanceTemplateId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT tai.InstanceTemplateAppInstanceId,
       tmi.ModuleInstanceKey,
       ith.HostKey,
       a.AppKey,
       a.AppType,
       tai.AppInstanceKey,
       tai.DisplayName,
       tai.RoutePath,
       tai.InstallationName,
       ar.ArtifactId,
       ar.Version,
       tai.IsEnabled,
       tai.IsAllowed,
       tai.DesiredState,
       tai.SortOrder
FROM omp.InstanceTemplateAppInstances tai
INNER JOIN omp.InstanceTemplateModuleInstances tmi
    ON tmi.InstanceTemplateModuleInstanceId = tai.InstanceTemplateModuleInstanceId
INNER JOIN omp.Apps a ON a.AppId = tai.AppId
LEFT JOIN omp.InstanceTemplateHosts ith
    ON ith.InstanceTemplateHostId = tai.InstanceTemplateHostId
LEFT JOIN omp.Artifacts ar ON ar.ArtifactId = tai.DesiredArtifactId
WHERE tmi.InstanceTemplateId = @InstanceTemplateId
ORDER BY tmi.SortOrder, tmi.ModuleInstanceKey, tai.SortOrder, tai.AppInstanceKey;";

        var rows = new List<InstanceTemplateAppTopologyRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@InstanceTemplateId", instanceTemplateId);
        await using (var rdr = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rdr.ReadAsync(ct))
            {
                rows.Add(new InstanceTemplateAppTopologyRow
                {
                    InstanceTemplateAppInstanceId = rdr.GetInt32(0),
                    ModuleInstanceKey = rdr.GetString(1),
                    HostKey = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    AppKey = rdr.GetString(3),
                    AppType = rdr.GetString(4),
                    AppInstanceKey = rdr.GetString(5),
                    DisplayName = rdr.GetString(6),
                    RoutePath = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    InstallationName = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                    ArtifactId = rdr.IsDBNull(9) ? null : rdr.GetInt32(9),
                    ArtifactVersion = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                    IsEnabled = rdr.GetBoolean(11),
                    IsAllowed = rdr.GetBoolean(12),
                    DesiredState = rdr.GetByte(13),
                    SortOrder = rdr.GetInt32(14)
                });
            }
        }

        await PopulateLatestTemplateArtifactUpgradesAsync(conn, instanceTemplateId, rows, ct);

        return rows;
    }

    private static async Task PopulateLatestTemplateArtifactUpgradesAsync(
        SqlConnection conn,
        int instanceTemplateId,
        IReadOnlyList<InstanceTemplateAppTopologyRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var rowsById = rows
            .Where(static row => row.ArtifactId.HasValue && !string.IsNullOrWhiteSpace(row.ArtifactVersion))
            .ToDictionary(static row => row.InstanceTemplateAppInstanceId);
        if (rowsById.Count == 0)
        {
            return;
        }

        const string sql = @"
WITH AppliedDefinitions AS
(
    SELECT ModuleDefinitionDocumentId,
           ModuleKey,
           ROW_NUMBER() OVER
           (
               PARTITION BY ModuleKey
               ORDER BY AppliedUtc DESC, UpdatedUtc DESC, ModuleDefinitionDocumentId DESC
           ) AS rn
    FROM omp.ModuleDefinitionDocuments
    WHERE IsApplied = 1
)
SELECT tai.InstanceTemplateAppInstanceId,
       candidate.ArtifactId,
       candidate.Version,
       compat.MinArtifactVersion,
       compat.MaxArtifactVersion
FROM omp.InstanceTemplateAppInstances tai
INNER JOIN omp.InstanceTemplateModuleInstances tmi
    ON tmi.InstanceTemplateModuleInstanceId = tai.InstanceTemplateModuleInstanceId
INNER JOIN omp.Apps a ON a.AppId = tai.AppId
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
INNER JOIN omp.Artifacts currentArtifact
    ON currentArtifact.ArtifactId = tai.DesiredArtifactId
INNER JOIN omp.Artifacts candidate
    ON candidate.AppId = currentArtifact.AppId
   AND candidate.PackageType = currentArtifact.PackageType
   AND ISNULL(candidate.TargetName, N'') = ISNULL(currentArtifact.TargetName, N'')
   AND candidate.IsEnabled = 1
INNER JOIN AppliedDefinitions d
    ON d.ModuleKey = m.ModuleKey
   AND d.rn = 1
INNER JOIN omp.ModuleDefinitionArtifactCompatibility compat
    ON compat.ModuleDefinitionDocumentId = d.ModuleDefinitionDocumentId
   AND compat.AppKey = a.AppKey
   AND compat.PackageType = candidate.PackageType
   AND ISNULL(compat.TargetName, N'') = ISNULL(candidate.TargetName, N'')
WHERE tmi.InstanceTemplateId = @InstanceTemplateId;";

        var bestByTemplateApp = new Dictionary<int, (int ArtifactId, string Version)>();
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@InstanceTemplateId", instanceTemplateId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var templateAppId = rdr.GetInt32(0);
            if (!rowsById.TryGetValue(templateAppId, out var row)
                || string.IsNullOrWhiteSpace(row.ArtifactVersion))
            {
                continue;
            }

            var candidateVersion = rdr.GetString(2);
            if (CompareArtifactVersions(candidateVersion, row.ArtifactVersion) <= 0)
            {
                continue;
            }

            var minVersion = rdr.IsDBNull(3) ? null : rdr.GetString(3);
            var maxVersion = rdr.IsDBNull(4) ? null : rdr.GetString(4);
            if (!IsVersionInRange(candidateVersion, minVersion, maxVersion))
            {
                continue;
            }

            if (!bestByTemplateApp.TryGetValue(templateAppId, out var currentBest)
                || CompareArtifactVersions(candidateVersion, currentBest.Version) > 0)
            {
                bestByTemplateApp[templateAppId] = (rdr.GetInt32(1), candidateVersion);
            }
        }

        foreach (var (templateAppId, latest) in bestByTemplateApp)
        {
            if (!rowsById.TryGetValue(templateAppId, out var row))
            {
                continue;
            }

            row.LatestArtifactId = latest.ArtifactId;
            row.LatestArtifactVersion = latest.Version;
        }
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

    public async Task<IReadOnlyList<HostAppDeploymentStateRow>> GetHostAppDeploymentStatesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (100)
       h.HostKey,
       ai.AppInstanceKey,
       ai.DisplayName,
       ar.Version,
       ar.PackageType,
       s.DeploymentState,
       s.TargetPath,
       s.RuntimeName,
       s.LastCheckedUtc,
       s.LastAppliedUtc,
       s.LastError
FROM omp.HostAppDeploymentStates s
INNER JOIN omp.Hosts h ON h.HostId = s.HostId
INNER JOIN omp.AppInstances ai ON ai.AppInstanceId = s.AppInstanceId
LEFT JOIN omp.Artifacts ar ON ar.ArtifactId = s.ArtifactId
ORDER BY COALESCE(s.LastCheckedUtc, s.UpdatedUtc, s.CreatedUtc) DESC, h.HostKey, ai.AppInstanceKey;";

        var rows = new List<HostAppDeploymentStateRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostAppDeploymentStateRow
            {
                HostKey = rdr.GetString(0),
                AppInstanceKey = rdr.GetString(1),
                DisplayName = rdr.GetString(2),
                ArtifactVersion = rdr.IsDBNull(3) ? string.Empty : rdr.GetString(3),
                PackageType = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4),
                DeploymentState = rdr.GetByte(5),
                TargetPath = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                RuntimeName = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                LastCheckedUtc = rdr.IsDBNull(8) ? null : rdr.GetDateTime(8),
                LastAppliedUtc = rdr.IsDBNull(9) ? null : rdr.GetDateTime(9),
                LastError = rdr.IsDBNull(10) ? null : rdr.GetString(10)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<HostArtifactStateRow>> GetHostArtifactStatesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (100)
       h.HostKey,
       a.AppKey,
       ar.Version,
       ar.PackageType,
       ar.TargetName,
       s.ProvisioningState,
       s.LocalPath,
       s.LastCheckedUtc,
       s.LastProvisionedUtc,
       s.LastError
FROM omp.HostArtifactStates s
INNER JOIN omp.Hosts h ON h.HostId = s.HostId
INNER JOIN omp.Artifacts ar ON ar.ArtifactId = s.ArtifactId
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
ORDER BY COALESCE(s.LastCheckedUtc, s.UpdatedUtc, s.CreatedUtc) DESC, h.HostKey, a.AppKey, ar.Version DESC;";

        var rows = new List<HostArtifactStateRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostArtifactStateRow
            {
                HostKey = rdr.GetString(0),
                AppKey = rdr.GetString(1),
                ArtifactVersion = rdr.GetString(2),
                PackageType = rdr.GetString(3),
                TargetName = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                ProvisioningState = rdr.GetByte(5),
                LocalPath = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                LastCheckedUtc = rdr.IsDBNull(7) ? null : rdr.GetDateTime(7),
                LastProvisionedUtc = rdr.IsDBNull(8) ? null : rdr.GetDateTime(8),
                LastError = rdr.IsDBNull(9) ? null : rdr.GetString(9)
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

    private static async Task<int> ConditionalScalarIntAsync(SqlConnection conn, string tableName, string sql, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, tableName, ct))
        {
            return 0;
        }

        return await ScalarIntAsync(conn, sql, ct);
    }

    private static async Task<bool> TableExistsAsync(SqlConnection conn, string tableName, CancellationToken ct)
    {
        const string sql = "SELECT CAST(CASE WHEN OBJECT_ID(@TableName, 'U') IS NULL THEN 0 ELSE 1 END AS bit);";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> HostBaseUrlColumnExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = "SELECT CAST(CASE WHEN COL_LENGTH('omp.Hosts', 'BaseUrl') IS NULL THEN 0 ELSE 1 END AS bit);";
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }
}
