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
       ht.TemplateKey AS TargetHostTemplateKey,
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
LEFT JOIN omp.HostTemplates ht ON ht.HostTemplateId = ai.TargetHostTemplateId
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
                TargetHostTemplateKey = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                IsAllowed = rdr.GetBoolean(10),
                DesiredState = rdr.GetByte(11),
                RuntimeKind = rdr.IsDBNull(12) ? string.Empty : rdr.GetString(12),
                WorkerTypeKey = rdr.IsDBNull(13) ? string.Empty : rdr.GetString(13),
                PluginRelativePath = rdr.IsDBNull(14) ? string.Empty : rdr.GetString(14),
                ObservedState = rdr.GetByte(15),
                ProcessId = rdr.IsDBNull(16) ? null : rdr.GetInt32(16),
                StartedUtc = rdr.IsDBNull(17) ? null : rdr.GetDateTime(17),
                LastSeenUtc = rdr.IsDBNull(18) ? null : rdr.GetDateTime(18),
                LastExitUtc = rdr.IsDBNull(19) ? null : rdr.GetDateTime(19),
                LastExitCode = rdr.IsDBNull(20) ? null : rdr.GetInt32(20),
                StatusMessage = rdr.IsDBNull(21) ? null : rdr.GetString(21)
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
       ht.TemplateKey AS TargetHostTemplateKey,
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
LEFT JOIN omp.HostTemplates ht ON ht.HostTemplateId = ai.TargetHostTemplateId
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
                TargetHostTemplateKey = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                RoutePath = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                InstallationName = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                ArtifactId = rdr.IsDBNull(11) ? null : rdr.GetInt32(11),
                ArtifactVersion = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                IsAllowed = rdr.GetBoolean(13),
                DesiredState = rdr.GetByte(14),
                LastSeenUtc = rdr.IsDBNull(15) ? null : rdr.GetDateTime(15),
                VerificationStatus = rdr.GetByte(16)
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
       ht.TemplateKey AS TargetHostTemplateKey,
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
LEFT JOIN omp.HostTemplates ht
    ON ht.HostTemplateId = tai.TargetHostTemplateId
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
                    TargetHostTemplateKey = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    AppKey = rdr.GetString(4),
                    AppType = rdr.GetString(5),
                    AppInstanceKey = rdr.GetString(6),
                    DisplayName = rdr.GetString(7),
                    RoutePath = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                    InstallationName = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                    ArtifactId = rdr.IsDBNull(10) ? null : rdr.GetInt32(10),
                    ArtifactVersion = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                    IsEnabled = rdr.GetBoolean(12),
                    IsAllowed = rdr.GetBoolean(13),
                    DesiredState = rdr.GetByte(14),
                    SortOrder = rdr.GetInt32(15)
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
   AND NULLIF(LTRIM(RTRIM(candidate.Sha256)), N'') IS NOT NULL
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

    public async Task<IReadOnlyList<HostDriftSummaryRow>> GetHostDriftSummariesAsync(CancellationToken ct)
    {
        var rows = new List<HostDriftSummaryRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var hasIdentityColumns = await ColumnExistsAsync(conn, "omp.HostAppDeploymentStates", "IdentityCheckStatus", ct);
        var identityWarningExpression = hasIdentityColumns
            ? "state.IdentityCheckStatus IN (N'ManualActionRequired', N'WaitingForPortalAdminApproval')"
            : "1 = 0";

        var sql = $@"
WITH DesiredTemplateApps AS
(
    SELECT h.HostId,
           h.HostKey,
           h.DisplayName AS HostDisplayName,
           h.LastSeenUtc AS HostLastSeenUtc,
           mi.ModuleInstanceId,
           tai.AppId,
           tai.AppInstanceKey,
           tai.DesiredArtifactId,
           desiredArtifact.PackageType AS DesiredPackageType,
           tai.InstanceTemplateHostId,
           tai.TargetHostTemplateId
    FROM omp.Hosts h
    INNER JOIN omp.Instances i ON i.InstanceId = h.InstanceId
    INNER JOIN omp.InstanceTemplates it ON it.InstanceTemplateId = i.InstanceTemplateId
    INNER JOIN omp.InstanceTemplateModuleInstances tmi
        ON tmi.InstanceTemplateId = it.InstanceTemplateId
       AND tmi.IsEnabled = 1
    INNER JOIN omp.ModuleInstances mi
        ON mi.InstanceId = i.InstanceId
       AND mi.ModuleInstanceKey = tmi.ModuleInstanceKey
       AND mi.IsEnabled = 1
    INNER JOIN omp.InstanceTemplateAppInstances tai
        ON tai.InstanceTemplateModuleInstanceId = tmi.InstanceTemplateModuleInstanceId
       AND tai.IsEnabled = 1
       AND tai.IsAllowed = 1
       AND tai.DesiredState = 1
    INNER JOIN omp.Apps app
        ON app.AppId = tai.AppId
       AND app.IsEnabled = 1
    INNER JOIN omp.Artifacts desiredArtifact
        ON desiredArtifact.ArtifactId = tai.DesiredArtifactId
       AND desiredArtifact.IsEnabled = 1
       AND desiredArtifact.PackageType IN (N'web-app', N'service-app')
    LEFT JOIN omp.InstanceTemplateHosts pinnedHost
        ON pinnedHost.InstanceTemplateHostId = tai.InstanceTemplateHostId
       AND pinnedHost.IsEnabled = 1
    WHERE h.IsEnabled = 1
      AND i.IsEnabled = 1
      AND it.IsEnabled = 1
      AND
      (
          (
              tai.InstanceTemplateHostId IS NOT NULL
              AND pinnedHost.HostKey = h.HostKey
              AND EXISTS
              (
                  SELECT 1
                  FROM omp.HostDeploymentAssignments assignment
                  WHERE assignment.HostId = h.HostId
                    AND assignment.HostTemplateId = pinnedHost.HostTemplateId
                    AND assignment.IsActive = 1
              )
          )
          OR
          (
              tai.InstanceTemplateHostId IS NULL
              AND tai.TargetHostTemplateId IS NULL
              AND desiredArtifact.PackageType = N'web-app'
          )
          OR
          (
              tai.InstanceTemplateHostId IS NULL
              AND tai.TargetHostTemplateId IS NOT NULL
              AND EXISTS
              (
                  SELECT 1
                  FROM omp.HostDeploymentAssignments assignment
                  WHERE assignment.HostId = h.HostId
                    AND assignment.HostTemplateId = tai.TargetHostTemplateId
                    AND assignment.IsActive = 1
              )
          )
      )
),
ResolvedApps AS
(
    SELECT desired.HostId,
           desired.DesiredArtifactId,
           desired.DesiredPackageType,
           appInstance.AppInstanceId,
           appInstance.ArtifactId AS MaterializedArtifactId,
           state.ArtifactId AS RuntimeArtifactId,
           state.DeploymentState,
           state.LastCheckedUtc,
           state.LastAppliedUtc,
           state.LastError,
           CAST(CASE WHEN {identityWarningExpression} THEN 1 ELSE 0 END AS bit) AS HasIdentityWarning
    FROM DesiredTemplateApps desired
    LEFT JOIN omp.AppInstances appInstance
        ON appInstance.ModuleInstanceId = desired.ModuleInstanceId
       AND appInstance.AppId = desired.AppId
       AND appInstance.AppInstanceKey = desired.AppInstanceKey
       AND appInstance.IsEnabled = 1
       AND appInstance.IsAllowed = 1
       AND appInstance.DesiredState = 1
       AND
       (
           (
               desired.InstanceTemplateHostId IS NOT NULL
               AND appInstance.HostId = desired.HostId
           )
           OR
           (
               desired.InstanceTemplateHostId IS NULL
               AND appInstance.HostId IS NULL
               AND ISNULL(appInstance.TargetHostTemplateId, -1) = ISNULL(desired.TargetHostTemplateId, -1)
           )
       )
    LEFT JOIN omp.HostAppDeploymentStates state
        ON state.HostId = desired.HostId
       AND state.AppInstanceId = appInstance.AppInstanceId
),
Aggregated AS
(
    SELECT HostId,
           COUNT(1) AS DesiredAppCount,
           SUM(CASE WHEN AppInstanceId IS NOT NULL
                         AND DeploymentState = 2
                         AND ISNULL(RuntimeArtifactId, -1) = ISNULL(DesiredArtifactId, -1)
                         AND LastError IS NULL
                         AND HasIdentityWarning = 0 THEN 1 ELSE 0 END) AS InSyncAppCount,
           SUM(CASE WHEN AppInstanceId IS NULL
                         OR ISNULL(MaterializedArtifactId, -1) <> ISNULL(DesiredArtifactId, -1) THEN 1 ELSE 0 END) AS MaterializationPendingCount,
           SUM(CASE WHEN AppInstanceId IS NOT NULL
                         AND DesiredPackageType IN (N'web-app', N'service-app')
                         AND RuntimeArtifactId IS NULL THEN 1 ELSE 0 END) AS MissingRuntimeCount,
           SUM(CASE WHEN RuntimeArtifactId IS NOT NULL
                         AND ISNULL(RuntimeArtifactId, -1) <> ISNULL(DesiredArtifactId, -1) THEN 1 ELSE 0 END) AS VersionMismatchCount,
           SUM(CASE WHEN AppInstanceId IS NULL
                         OR (DesiredPackageType IN (N'web-app', N'service-app') AND RuntimeArtifactId IS NULL)
                         OR DeploymentState = 0
                         OR ISNULL(RuntimeArtifactId, -1) <> ISNULL(DesiredArtifactId, -1) THEN 1 ELSE 0 END) AS PendingAppCount,
           SUM(CASE WHEN DeploymentState = 1 THEN 1 ELSE 0 END) AS RunningAppCount,
           SUM(CASE WHEN DeploymentState = 3
                         OR LastError IS NOT NULL THEN 1 ELSE 0 END) AS FailedAppCount,
           SUM(CASE WHEN DeploymentState = 4
                         OR HasIdentityWarning = 1 THEN 1 ELSE 0 END) AS WarningAppCount,
           MAX(LastCheckedUtc) AS LastCheckedUtc,
           MAX(LastAppliedUtc) AS LastAppliedUtc
    FROM ResolvedApps
    GROUP BY HostId
)
SELECT h.HostId,
       h.HostKey,
       h.DisplayName,
       h.LastSeenUtc,
       ISNULL(aggregated.DesiredAppCount, 0) AS DesiredAppCount,
       ISNULL(aggregated.InSyncAppCount, 0) AS InSyncAppCount,
       ISNULL(aggregated.MaterializationPendingCount, 0) AS MaterializationPendingCount,
       ISNULL(aggregated.MissingRuntimeCount, 0) AS MissingRuntimeCount,
       ISNULL(aggregated.VersionMismatchCount, 0) AS VersionMismatchCount,
       ISNULL(aggregated.PendingAppCount, 0) AS PendingAppCount,
       ISNULL(aggregated.RunningAppCount, 0) AS RunningAppCount,
       ISNULL(aggregated.FailedAppCount, 0) AS FailedAppCount,
       ISNULL(aggregated.WarningAppCount, 0) AS WarningAppCount,
       aggregated.LastCheckedUtc,
       aggregated.LastAppliedUtc,
       desiredArtifact.Version AS HostAgentDesiredVersion,
       runtimeState.Version AS HostAgentCurrentVersion,
       runtimeState.LastSeenUtc AS HostAgentLastSeenUtc,
       CAST(CASE WHEN desiredArtifact.Version IS NOT NULL
                      AND ISNULL(runtimeState.Version, N'') <> desiredArtifact.Version THEN 1 ELSE 0 END AS bit) AS HostAgentUpgradePending
FROM omp.Hosts h
LEFT JOIN Aggregated aggregated ON aggregated.HostId = h.HostId
LEFT JOIN omp.HostAgentDesiredStates desiredState ON desiredState.HostId = h.HostId
LEFT JOIN omp.Artifacts desiredArtifact ON desiredArtifact.ArtifactId = desiredState.ArtifactId
OUTER APPLY
(
    SELECT TOP (1) runtime.*
    FROM omp.HostAgentRuntimeStates runtime
    WHERE runtime.HostId = h.HostId
    ORDER BY runtime.IsActive DESC,
             COALESCE(runtime.LastSeenUtc, runtime.UpdatedUtc, runtime.CreatedUtc) DESC,
             runtime.ServiceName
) runtimeState
WHERE h.IsEnabled = 1
ORDER BY h.HostKey;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostDriftSummaryRow
            {
                HostId = rdr.GetGuid(0),
                HostKey = rdr.GetString(1),
                DisplayName = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                HostLastSeenUtc = rdr.IsDBNull(3) ? null : rdr.GetDateTime(3),
                DesiredAppCount = rdr.GetInt32(4),
                InSyncAppCount = rdr.GetInt32(5),
                MaterializationPendingCount = rdr.GetInt32(6),
                MissingRuntimeCount = rdr.GetInt32(7),
                VersionMismatchCount = rdr.GetInt32(8),
                PendingAppCount = rdr.GetInt32(9),
                RunningAppCount = rdr.GetInt32(10),
                FailedAppCount = rdr.GetInt32(11),
                WarningAppCount = rdr.GetInt32(12),
                LastCheckedUtc = rdr.IsDBNull(13) ? null : rdr.GetDateTime(13),
                LastAppliedUtc = rdr.IsDBNull(14) ? null : rdr.GetDateTime(14),
                HostAgentDesiredVersion = rdr.IsDBNull(15) ? null : rdr.GetString(15),
                HostAgentCurrentVersion = rdr.IsDBNull(16) ? null : rdr.GetString(16),
                HostAgentLastSeenUtc = rdr.IsDBNull(17) ? null : rdr.GetDateTime(17),
                HostAgentUpgradePending = rdr.GetBoolean(18)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<HostDriftDetailRow>> GetHostDriftDetailsAsync(CancellationToken ct)
    {
        var rows = new List<HostDriftDetailRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var hasIdentityColumns = await ColumnExistsAsync(conn, "omp.HostAppDeploymentStates", "IdentityCheckStatus", ct);
        var identityWarningExpression = hasIdentityColumns
            ? "state.IdentityCheckStatus IN (N'ManualActionRequired', N'WaitingForPortalAdminApproval')"
            : "1 = 0";
        var identityStatusSelect = hasIdentityColumns
            ? "state.IdentityCheckStatus"
            : "CAST(NULL AS nvarchar(40))";

        var sql = $@"
WITH DesiredTemplateApps AS
(
    SELECT h.HostId,
           h.HostKey,
           mi.ModuleInstanceId,
           tmi.ModuleInstanceKey,
           app.AppKey,
           tai.AppId,
           tai.AppInstanceKey,
           tai.DisplayName AS AppDisplayName,
           tai.DesiredArtifactId,
           desiredArtifact.Version AS DesiredArtifactVersion,
           desiredArtifact.PackageType AS DesiredPackageType,
           desiredArtifact.TargetName AS DesiredTargetName,
           tai.InstanceTemplateHostId,
           tai.TargetHostTemplateId,
           pinnedHost.HostKey AS PinnedHostKey,
           targetHostTemplate.TemplateKey AS TargetHostTemplateKey
    FROM omp.Hosts h
    INNER JOIN omp.Instances i ON i.InstanceId = h.InstanceId
    INNER JOIN omp.InstanceTemplates it ON it.InstanceTemplateId = i.InstanceTemplateId
    INNER JOIN omp.InstanceTemplateModuleInstances tmi
        ON tmi.InstanceTemplateId = it.InstanceTemplateId
       AND tmi.IsEnabled = 1
    INNER JOIN omp.ModuleInstances mi
        ON mi.InstanceId = i.InstanceId
       AND mi.ModuleInstanceKey = tmi.ModuleInstanceKey
       AND mi.IsEnabled = 1
    INNER JOIN omp.InstanceTemplateAppInstances tai
        ON tai.InstanceTemplateModuleInstanceId = tmi.InstanceTemplateModuleInstanceId
       AND tai.IsEnabled = 1
       AND tai.IsAllowed = 1
       AND tai.DesiredState = 1
    INNER JOIN omp.Apps app
        ON app.AppId = tai.AppId
       AND app.IsEnabled = 1
    INNER JOIN omp.Artifacts desiredArtifact
        ON desiredArtifact.ArtifactId = tai.DesiredArtifactId
       AND desiredArtifact.IsEnabled = 1
       AND desiredArtifact.PackageType IN (N'web-app', N'service-app')
    LEFT JOIN omp.InstanceTemplateHosts pinnedHost
        ON pinnedHost.InstanceTemplateHostId = tai.InstanceTemplateHostId
       AND pinnedHost.IsEnabled = 1
    LEFT JOIN omp.HostTemplates targetHostTemplate ON targetHostTemplate.HostTemplateId = tai.TargetHostTemplateId
    WHERE h.IsEnabled = 1
      AND i.IsEnabled = 1
      AND it.IsEnabled = 1
      AND
      (
          (
              tai.InstanceTemplateHostId IS NOT NULL
              AND pinnedHost.HostKey = h.HostKey
              AND EXISTS
              (
                  SELECT 1
                  FROM omp.HostDeploymentAssignments assignment
                  WHERE assignment.HostId = h.HostId
                    AND assignment.HostTemplateId = pinnedHost.HostTemplateId
                    AND assignment.IsActive = 1
              )
          )
          OR
          (
              tai.InstanceTemplateHostId IS NULL
              AND tai.TargetHostTemplateId IS NULL
              AND desiredArtifact.PackageType = N'web-app'
          )
          OR
          (
              tai.InstanceTemplateHostId IS NULL
              AND tai.TargetHostTemplateId IS NOT NULL
              AND EXISTS
              (
                  SELECT 1
                  FROM omp.HostDeploymentAssignments assignment
                  WHERE assignment.HostId = h.HostId
                    AND assignment.HostTemplateId = tai.TargetHostTemplateId
                    AND assignment.IsActive = 1
              )
          )
      )
),
ResolvedApps AS
(
    SELECT desired.HostId,
           desired.HostKey,
           desired.ModuleInstanceKey,
           desired.AppKey,
           desired.AppInstanceKey,
           desired.AppDisplayName,
           desired.DesiredArtifactVersion,
           desired.DesiredArtifactId,
           desired.DesiredPackageType,
           desired.DesiredTargetName,
           desired.PinnedHostKey,
           desired.TargetHostTemplateKey,
           appInstance.AppInstanceId,
           appInstance.ArtifactId AS MaterializedArtifactId,
           materializedArtifact.Version AS MaterializedArtifactVersion,
           state.ArtifactId AS RuntimeArtifactId,
           runtimeArtifact.Version AS RuntimeArtifactVersion,
           state.DeploymentState,
           state.LastCheckedUtc,
           state.LastAppliedUtc,
           state.LastError,
           {identityStatusSelect} AS IdentityCheckStatus,
           CAST(CASE WHEN {identityWarningExpression} THEN 1 ELSE 0 END AS bit) AS HasIdentityWarning
    FROM DesiredTemplateApps desired
    LEFT JOIN omp.AppInstances appInstance
        ON appInstance.ModuleInstanceId = desired.ModuleInstanceId
       AND appInstance.AppId = desired.AppId
       AND appInstance.AppInstanceKey = desired.AppInstanceKey
       AND appInstance.IsEnabled = 1
       AND appInstance.IsAllowed = 1
       AND appInstance.DesiredState = 1
       AND
       (
           (
               desired.InstanceTemplateHostId IS NOT NULL
               AND appInstance.HostId = desired.HostId
           )
           OR
           (
               desired.InstanceTemplateHostId IS NULL
               AND appInstance.HostId IS NULL
               AND ISNULL(appInstance.TargetHostTemplateId, -1) = ISNULL(desired.TargetHostTemplateId, -1)
           )
       )
    LEFT JOIN omp.Artifacts materializedArtifact ON materializedArtifact.ArtifactId = appInstance.ArtifactId
    LEFT JOIN omp.HostAppDeploymentStates state
        ON state.HostId = desired.HostId
       AND state.AppInstanceId = appInstance.AppInstanceId
    LEFT JOIN omp.Artifacts runtimeArtifact ON runtimeArtifact.ArtifactId = state.ArtifactId
),
Classified AS
(
    SELECT *,
           CASE
               WHEN AppInstanceId IS NULL THEN N'materialization pending'
               WHEN ISNULL(MaterializedArtifactId, -1) <> ISNULL(DesiredArtifactId, -1) THEN N'materialization pending'
               WHEN RuntimeArtifactId IS NULL THEN N'missing runtime'
               WHEN ISNULL(RuntimeArtifactId, -1) <> ISNULL(DesiredArtifactId, -1) THEN N'version mismatch'
               WHEN DeploymentState = 0 THEN N'Pending'
               WHEN DeploymentState = 1 THEN N'Running'
               WHEN DeploymentState = 3 OR LastError IS NOT NULL THEN N'Failed'
               WHEN DeploymentState = 4 OR HasIdentityWarning = 1 THEN N'Warning'
               ELSE N'In sync'
           END AS DriftReason,
           COALESCE(PinnedHostKey, TargetHostTemplateKey, N'Any host') AS Placement
    FROM ResolvedApps
)
SELECT HostId,
       HostKey,
       DriftReason,
       ModuleInstanceKey,
       AppKey,
       AppInstanceKey,
       AppDisplayName,
       DesiredArtifactVersion,
       MaterializedArtifactVersion,
       RuntimeArtifactVersion,
       DesiredPackageType,
       DesiredTargetName,
       Placement,
       DeploymentState,
       LastCheckedUtc,
       LastAppliedUtc,
       LastError,
       IdentityCheckStatus
FROM Classified
WHERE DriftReason <> N'In sync'
ORDER BY HostKey,
         CASE DriftReason
             WHEN N'Failed' THEN 0
             WHEN N'Running' THEN 1
             WHEN N'materialization pending' THEN 2
             WHEN N'missing runtime' THEN 3
             WHEN N'version mismatch' THEN 4
             WHEN N'Pending' THEN 5
             WHEN N'Warning' THEN 6
             ELSE 7
         END,
         ModuleInstanceKey,
         AppInstanceKey;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostDriftDetailRow
            {
                HostId = rdr.GetGuid(0),
                HostKey = rdr.GetString(1),
                DriftReason = rdr.GetString(2),
                ModuleInstanceKey = rdr.GetString(3),
                AppKey = rdr.GetString(4),
                AppInstanceKey = rdr.GetString(5),
                AppDisplayName = rdr.GetString(6),
                DesiredArtifactVersion = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                MaterializedArtifactVersion = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                RuntimeArtifactVersion = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                DesiredPackageType = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                DesiredTargetName = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                Placement = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                DeploymentState = rdr.IsDBNull(13) ? null : rdr.GetByte(13),
                LastCheckedUtc = rdr.IsDBNull(14) ? null : rdr.GetDateTime(14),
                LastAppliedUtc = rdr.IsDBNull(15) ? null : rdr.GetDateTime(15),
                LastError = rdr.IsDBNull(16) ? null : rdr.GetString(16),
                IdentityCheckStatus = rdr.IsDBNull(17) ? null : rdr.GetString(17)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<HostAppDeploymentStateRow>> GetHostAppDeploymentStatesAsync(CancellationToken ct)
    {
        var rows = new List<HostAppDeploymentStateRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var hasIdentityColumns = await ColumnExistsAsync(conn, "omp.HostAppDeploymentStates", "IdentityCheckStatus", ct);
        var identityColumns = hasIdentityColumns
            ? @",
       s.CredentialAutomationMode,
       s.DesiredRuntimeIdentity,
       s.ActualRuntimeIdentity,
       s.IdentityCheckStatus,
       s.IdentityRepairRequestedUtc,
       s.IdentityRepairRequestedBy"
            : @",
       CAST(NULL AS nvarchar(40)) AS CredentialAutomationMode,
       CAST(NULL AS nvarchar(256)) AS DesiredRuntimeIdentity,
       CAST(NULL AS nvarchar(256)) AS ActualRuntimeIdentity,
       CAST(NULL AS nvarchar(40)) AS IdentityCheckStatus,
       CAST(NULL AS datetime2(3)) AS IdentityRepairRequestedUtc,
       CAST(NULL AS nvarchar(256)) AS IdentityRepairRequestedBy";

        var sql = $@"
SELECT TOP (100)
       s.HostId,
       s.AppInstanceId,
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
       {identityColumns}
FROM omp.HostAppDeploymentStates s
INNER JOIN omp.Hosts h ON h.HostId = s.HostId
INNER JOIN omp.AppInstances ai ON ai.AppInstanceId = s.AppInstanceId
LEFT JOIN omp.Artifacts ar ON ar.ArtifactId = s.ArtifactId
ORDER BY COALESCE(s.LastCheckedUtc, s.UpdatedUtc, s.CreatedUtc) DESC, h.HostKey, ai.AppInstanceKey;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostAppDeploymentStateRow
            {
                HostId = rdr.GetGuid(0),
                AppInstanceId = rdr.GetGuid(1),
                HostKey = rdr.GetString(2),
                AppInstanceKey = rdr.GetString(3),
                DisplayName = rdr.GetString(4),
                ArtifactVersion = rdr.IsDBNull(5) ? string.Empty : rdr.GetString(5),
                PackageType = rdr.IsDBNull(6) ? string.Empty : rdr.GetString(6),
                DeploymentState = rdr.GetByte(7),
                TargetPath = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                RuntimeName = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                LastCheckedUtc = rdr.IsDBNull(10) ? null : rdr.GetDateTime(10),
                LastAppliedUtc = rdr.IsDBNull(11) ? null : rdr.GetDateTime(11),
                LastError = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                CredentialAutomationMode = rdr.IsDBNull(13) ? null : rdr.GetString(13),
                DesiredRuntimeIdentity = rdr.IsDBNull(14) ? null : rdr.GetString(14),
                ActualRuntimeIdentity = rdr.IsDBNull(15) ? null : rdr.GetString(15),
                IdentityCheckStatus = rdr.IsDBNull(16) ? null : rdr.GetString(16),
                IdentityRepairRequestedUtc = rdr.IsDBNull(17) ? null : rdr.GetDateTime(17),
                IdentityRepairRequestedBy = rdr.IsDBNull(18) ? null : rdr.GetString(18)
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
       s.LastError,
       CAST(CASE WHEN desired.ArtifactId IS NULL THEN 0 ELSE 1 END AS bit) AS IsCurrentlyDesired
FROM omp.HostArtifactStates s
INNER JOIN omp.Hosts h ON h.HostId = s.HostId
INNER JOIN omp.Artifacts ar ON ar.ArtifactId = s.ArtifactId
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
OUTER APPLY
(
    SELECT TOP (1) desiredArtifacts.ArtifactId
    FROM
    (
        SELECT arApp.ArtifactId
        FROM omp.AppInstances ai
        INNER JOIN omp.Artifacts arApp ON arApp.ArtifactId = ai.ArtifactId
        WHERE arApp.ArtifactId = s.ArtifactId
          AND ai.IsEnabled = 1
          AND ai.IsAllowed = 1
          AND arApp.IsEnabled = 1
          AND
          (
              ai.HostId = s.HostId
              OR (ai.HostId IS NULL AND ai.TargetHostTemplateId IS NULL)
              OR
              (
                  ai.HostId IS NULL
                  AND ai.TargetHostTemplateId IS NOT NULL
                  AND EXISTS
                  (
                      SELECT 1
                      FROM omp.HostDeploymentAssignments hda
                      WHERE hda.HostId = s.HostId
                        AND hda.HostTemplateId = ai.TargetHostTemplateId
                        AND hda.IsActive = 1
                  )
              )
          )

        UNION ALL

        SELECT arWorker.ArtifactId
        FROM omp.WorkerInstances wi
        INNER JOIN omp.AppInstances ai ON ai.AppInstanceId = wi.AppInstanceId
        INNER JOIN omp.Artifacts arWorker ON arWorker.ArtifactId = COALESCE(wi.ArtifactId, ai.ArtifactId)
        WHERE arWorker.ArtifactId = s.ArtifactId
          AND ai.IsEnabled = 1
          AND ai.IsAllowed = 1
          AND wi.IsEnabled = 1
          AND wi.IsAllowed = 1
          AND arWorker.IsEnabled = 1
          AND
          (
              wi.HostId = s.HostId
              OR (wi.HostId IS NULL AND ai.HostId = s.HostId)
              OR
              (
                  wi.HostId IS NULL
                  AND ai.HostId IS NULL
                  AND ai.TargetHostTemplateId IS NOT NULL
                  AND EXISTS
                  (
                      SELECT 1
                      FROM omp.HostDeploymentAssignments hda
                      WHERE hda.HostId = s.HostId
                        AND hda.HostTemplateId = ai.TargetHostTemplateId
                        AND hda.IsActive = 1
                  )
              )
          )

        UNION ALL

        SELECT arRequirement.ArtifactId
        FROM omp.HostArtifactRequirements har
        INNER JOIN omp.Artifacts arRequirement ON arRequirement.ArtifactId = har.ArtifactId
        WHERE arRequirement.ArtifactId = s.ArtifactId
          AND har.HostId = s.HostId
          AND har.IsEnabled = 1
          AND arRequirement.IsEnabled = 1
    ) desiredArtifacts
) desired
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
                LastError = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                IsCurrentlyDesired = rdr.GetBoolean(10)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<HostAgentUpgradeRow>> GetHostAgentUpgradeRowsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT h.HostId,
       h.HostKey,
       h.DisplayName,
       desired.ArtifactId AS DesiredArtifactId,
       desiredArtifact.Version AS DesiredVersion,
       desiredArtifact.RelativePath AS DesiredRelativePath,
       desired.ServiceNamePrefix,
       desired.InstallRoot,
       CAST(ISNULL(desired.IsEnabled, 0) AS bit) AS DesiredIsEnabled,
       desired.UpdatedUtc AS DesiredUpdatedUtc,
       runtimeState.ServiceName AS CurrentServiceName,
       runtimeState.Version AS CurrentVersion,
       runtimeState.InstallPath AS CurrentInstallPath,
       runtimeState.RuntimeMode,
       CAST(ISNULL(runtimeState.IsActive, 0) AS bit) AS CurrentIsActive,
       runtimeState.TakeoverFromServiceName,
       runtimeState.LastSeenUtc,
       runtimeState.StatusMessage,
       desiredService.DesiredServiceName AS TargetServiceName,
       targetRuntimeState.RuntimeMode AS TargetRuntimeMode,
       CAST(ISNULL(targetRuntimeState.IsActive, 0) AS bit) AS TargetIsActive,
       targetRuntimeState.LastSeenUtc AS TargetRuntimeLastSeenUtc,
       targetRuntimeState.StatusMessage AS TargetRuntimeStatusMessage
FROM omp.Hosts h
LEFT JOIN omp.HostAgentDesiredStates desired ON desired.HostId = h.HostId
LEFT JOIN omp.Artifacts desiredArtifact ON desiredArtifact.ArtifactId = desired.ArtifactId
OUTER APPLY
(
    SELECT TOP (1) r.*
    FROM omp.HostAgentRuntimeStates r
    WHERE r.HostId = h.HostId
    ORDER BY r.IsActive DESC, COALESCE(r.LastSeenUtc, r.UpdatedUtc, r.CreatedUtc) DESC, r.ServiceName
) runtimeState
OUTER APPLY
(
    SELECT
        CASE
            WHEN runtimeState.ServiceName IS NULL THEN NULL
            WHEN NULLIF(runtimeState.Version, N'') IS NOT NULL
             AND runtimeState.ServiceName LIKE N'%.' + runtimeState.Version
                THEN LEFT(runtimeState.ServiceName, LEN(runtimeState.ServiceName) - LEN(runtimeState.Version) - 1)
            ELSE runtimeState.ServiceName
        END AS CurrentServicePrefix
) currentService
OUTER APPLY
(
    SELECT
        CASE
            WHEN desiredArtifact.Version IS NULL THEN NULL
            WHEN NULLIF(COALESCE(NULLIF(desired.ServiceNamePrefix, N''), currentService.CurrentServicePrefix), N'') IS NULL THEN NULL
            ELSE CONCAT(COALESCE(NULLIF(desired.ServiceNamePrefix, N''), currentService.CurrentServicePrefix), N'.', desiredArtifact.Version)
        END AS DesiredServiceName
) desiredService
LEFT JOIN omp.HostAgentRuntimeStates targetRuntimeState
    ON targetRuntimeState.HostId = h.HostId
   AND targetRuntimeState.ServiceName = desiredService.DesiredServiceName
WHERE h.IsEnabled = 1
ORDER BY h.HostKey;";

        var rows = new List<HostAgentUpgradeRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostAgentUpgradeRow
            {
                HostId = rdr.GetGuid(0),
                HostKey = rdr.GetString(1),
                DisplayName = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                DesiredArtifactId = rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
                DesiredVersion = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                DesiredRelativePath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                ServiceNamePrefix = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                InstallRoot = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                DesiredIsEnabled = rdr.GetBoolean(8),
                DesiredUpdatedUtc = rdr.IsDBNull(9) ? null : rdr.GetDateTime(9),
                CurrentServiceName = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                CurrentVersion = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                CurrentInstallPath = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                RuntimeMode = rdr.IsDBNull(13) ? null : rdr.GetString(13),
                CurrentIsActive = rdr.GetBoolean(14),
                TakeoverFromServiceName = rdr.IsDBNull(15) ? null : rdr.GetString(15),
                RuntimeLastSeenUtc = rdr.IsDBNull(16) ? null : rdr.GetDateTime(16),
                RuntimeStatusMessage = rdr.IsDBNull(17) ? null : rdr.GetString(17),
                TargetServiceName = rdr.IsDBNull(18) ? null : rdr.GetString(18),
                TargetRuntimeMode = rdr.IsDBNull(19) ? null : rdr.GetString(19),
                TargetIsActive = rdr.GetBoolean(20),
                TargetRuntimeLastSeenUtc = rdr.IsDBNull(21) ? null : rdr.GetDateTime(21),
                TargetRuntimeStatusMessage = rdr.IsDBNull(22) ? null : rdr.GetString(22)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<HostAgentArtifactOption>> GetHostAgentArtifactOptionsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT ar.ArtifactId,
       ar.Version,
       ar.RelativePath,
       ar.CreatedUtc
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
WHERE m.ModuleKey = N'omp_core'
  AND a.AppKey = N'omp_hostagent'
  AND ar.PackageType = N'host-agent'
  AND ar.TargetName = N'omp-hostagent'
  AND ar.IsEnabled = 1
ORDER BY ar.CreatedUtc DESC, ar.ArtifactId DESC;";

        var rows = new List<HostAgentArtifactOption>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostAgentArtifactOption
            {
                ArtifactId = rdr.GetInt32(0),
                Version = rdr.GetString(1),
                RelativePath = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                CreatedUtc = rdr.GetDateTime(3)
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

    private static async Task<bool> ColumnExistsAsync(SqlConnection conn, string tableName, string columnName, CancellationToken ct)
    {
        const string sql = "SELECT CAST(CASE WHEN COL_LENGTH(@TableName, @ColumnName) IS NULL THEN 0 ELSE 1 END AS bit);";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        cmd.Parameters.AddWithValue("@ColumnName", columnName);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> HostBaseUrlColumnExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = "SELECT CAST(CASE WHEN COL_LENGTH('omp.Hosts', 'BaseUrl') IS NULL THEN 0 ELSE 1 END AS bit);";
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }
}
