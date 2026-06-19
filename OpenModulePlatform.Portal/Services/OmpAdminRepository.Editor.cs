// File: OpenModulePlatform.Portal/Services/OmpAdminRepository.Editor.cs
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// CRUD and lookup methods that back the editable Portal admin pages.
/// This partial keeps write-oriented admin logic separate from the read-only list pages in <c>OmpAdminRepository.cs</c>.
/// </summary>
public sealed partial class OmpAdminRepository
{
    private const string BootstrapPortalAdminPrincipalPlaceholder = "__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__";

    public async Task RequestServiceIdentityRepairAsync(
        Guid hostId,
        Guid appInstanceId,
        string requestedBy,
        CancellationToken ct)
    {
        const string sql = @"
IF COL_LENGTH(N'omp.HostAppDeploymentStates', N'IdentityRepairRequestedUtc') IS NULL
BEGIN
    THROW 53310, N'Host app deployment identity repair columns are not installed. Run module definition SQL repair first.', 1;
END;

UPDATE s
SET IdentityRepairRequestedUtc = SYSUTCDATETIME(),
    IdentityRepairRequestedBy = @requestedBy,
    IdentityCheckStatus = N'RepairRequested',
    UpdatedUtc = SYSUTCDATETIME()
FROM omp.HostAppDeploymentStates s
INNER JOIN omp.AppInstances ai ON ai.AppInstanceId = s.AppInstanceId
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
WHERE s.HostId = @hostId
  AND s.AppInstanceId = @appInstanceId
  AND a.AppType = N'ServiceApp';

IF @@ROWCOUNT = 0
BEGIN
    THROW 53311, N'The selected service-app deployment state was not found.', 1;
END;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@requestedBy", string.IsNullOrWhiteSpace(requestedBy) ? (object)DBNull.Value : requestedBy.Trim());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Lookup helpers used by edit forms
    // -------------------------------------------------------------------------

    public Task<IReadOnlyList<OptionItem>> GetInstanceTemplateOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(InstanceTemplateId AS nvarchar(50)),
       TemplateKey + N' - ' + DisplayName
FROM omp.InstanceTemplates
ORDER BY TemplateKey;",
            ct);

    public Task<IReadOnlyList<OptionItem>> GetInstanceOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(InstanceId AS nvarchar(50)),
       InstanceKey + N' - ' + DisplayName
FROM omp.Instances
ORDER BY InstanceKey;",
            ct);

    public Task<IReadOnlyList<OptionItem>> GetModuleOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(ModuleId AS nvarchar(50)),
       ModuleKey + N' - ' + DisplayName
FROM omp.Modules
ORDER BY SortOrder, ModuleKey;",
            ct);

    public Task<IReadOnlyList<OptionItem>> GetModuleInstanceOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(mi.ModuleInstanceId AS nvarchar(50)),
       i.InstanceKey + N' / ' + mi.ModuleInstanceKey + N' - ' + mi.DisplayName
FROM omp.ModuleInstances mi
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
ORDER BY i.InstanceKey, mi.SortOrder, mi.ModuleInstanceKey;",
            ct);

    public Task<IReadOnlyList<OptionItem>> GetHostOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(h.HostId AS nvarchar(50)),
       i.InstanceKey + N' / ' + h.HostKey + COALESCE(N' - ' + h.DisplayName, N'')
FROM omp.Hosts h
INNER JOIN omp.Instances i ON i.InstanceId = h.InstanceId
ORDER BY i.InstanceKey, h.HostKey;",
            ct);

    public Task<IReadOnlyList<OptionItem>> GetHostTemplateOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(HostTemplateId AS nvarchar(50)),
       TemplateKey + N' - ' + COALESCE(NULLIF(DisplayName, N''), TemplateKey)
FROM omp.HostTemplates
ORDER BY TemplateKey;",
            ct);

    public Task<IReadOnlyList<OptionItem>> GetAppOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(a.AppId AS nvarchar(50)),
       m.ModuleKey + N' / ' + a.AppKey + N' - ' + a.DisplayName
FROM omp.Apps a
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
ORDER BY m.ModuleKey, a.SortOrder, a.AppKey;",
            ct);

    public async Task<IReadOnlyList<ArtifactAppOption>> GetArtifactAppOptionsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT a.AppId,
       m.ModuleKey,
       a.AppKey,
       m.ModuleKey + N' / ' + a.AppKey + N' - ' + a.DisplayName
FROM omp.Apps a
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
ORDER BY m.ModuleKey, a.SortOrder, a.AppKey;";

        var rows = new List<ArtifactAppOption>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            rows.Add(
                new ArtifactAppOption
                {
                    AppId = rdr.GetInt32(0),
                    ModuleKey = rdr.GetString(1),
                    AppKey = rdr.GetString(2),
                    Label = rdr.GetString(3)
                });
        }

        return rows;
    }

    public async Task<ArtifactCompatibilitySlot> RequireCompatibleArtifactSlotAsync(
        int appId,
        string version,
        string packageType,
        string? targetName,
        CancellationToken ct)
    {
        const string contextSql = @"
SELECT TOP (1)
       m.ModuleKey,
       a.AppKey,
       d.ModuleDefinitionDocumentId,
       d.DefinitionVersion
FROM omp.Apps a
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
LEFT JOIN omp.ModuleDefinitionDocuments d
    ON d.ModuleKey = m.ModuleKey
   AND d.IsApplied = 1
WHERE a.AppId = @AppId
  AND a.IsEnabled = 1
  AND m.IsEnabled = 1
ORDER BY d.AppliedUtc DESC, d.UpdatedUtc DESC, d.ModuleDefinitionDocumentId DESC;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        string moduleKey;
        string appKey;
        int? moduleDefinitionDocumentId;
        string? definitionVersion;

        await using (var context = new SqlCommand(contextSql, conn))
        {
            Add(context, "@AppId", appId);
            await using var rdr = await context.ExecuteReaderAsync(ct);
            if (!await rdr.ReadAsync(ct))
            {
                throw new InvalidOperationException("The selected app was not found.");
            }

            moduleKey = rdr.GetString(0);
            appKey = rdr.GetString(1);
            moduleDefinitionDocumentId = rdr.IsDBNull(2) ? null : rdr.GetInt32(2);
            definitionVersion = rdr.IsDBNull(3) ? null : rdr.GetString(3);
        }

        if (moduleDefinitionDocumentId is null || string.IsNullOrWhiteSpace(definitionVersion))
        {
            throw new InvalidOperationException(
                $"Module '{moduleKey}' has no applied module definition. Apply the module definition before importing artifacts for app '{appKey}'.");
        }

        const string slotSql = @"
SELECT AppKey,
       PackageType,
       TargetName,
       RelativePathTemplate,
       MinArtifactVersion,
       MaxArtifactVersion
FROM omp.ModuleDefinitionArtifactCompatibility
WHERE ModuleDefinitionDocumentId = @ModuleDefinitionDocumentId
  AND AppKey = @AppKey
  AND PackageType = @PackageType
  AND ((TargetName = @TargetName) OR (TargetName IS NULL AND @TargetName IS NULL))
ORDER BY ModuleDefinitionArtifactCompatibilityId;";

        var slots = new List<ArtifactCompatibilitySlot>();
        await using (var slotCommand = new SqlCommand(slotSql, conn))
        {
            Add(slotCommand, "@ModuleDefinitionDocumentId", moduleDefinitionDocumentId.Value);
            Add(slotCommand, "@AppKey", appKey);
            Add(slotCommand, "@PackageType", packageType);
            Add(slotCommand, "@TargetName", string.IsNullOrWhiteSpace(targetName) ? null : targetName.Trim());

            await using var rdr = await slotCommand.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                slots.Add(
                    new ArtifactCompatibilitySlot
                    {
                        ModuleKey = moduleKey,
                        DefinitionVersion = definitionVersion,
                        AppKey = rdr.GetString(0),
                        PackageType = rdr.GetString(1),
                        TargetName = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                        RelativePathTemplate = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                        MinArtifactVersion = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                        MaxArtifactVersion = rdr.IsDBNull(5) ? null : rdr.GetString(5)
                    });
            }
        }

        if (slots.Count == 0)
        {
            throw new InvalidOperationException(
                $"Module definition '{moduleKey}' version {definitionVersion} does not allow artifacts for app '{appKey}', package type '{packageType}', and target '{targetName}'.");
        }

        var compatible = slots.FirstOrDefault(slot =>
            IsVersionInRange(version, slot.MinArtifactVersion, slot.MaxArtifactVersion));
        if (compatible is null)
        {
            throw new InvalidOperationException(
                $"Artifact version {version} is not compatible with module definition '{moduleKey}' version {definitionVersion}. " +
                $"Allowed range: {FormatArtifactVersionRanges(slots)}.");
        }

        return compatible;
    }

    // -------------------------------------------------------------------------
    // App worker-definition editing
    // -------------------------------------------------------------------------

    public async Task<AppWorkerDefinitionEditData?> GetAppWorkerDefinitionAsync(int appId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await TableExistsAsync(conn, "omp.AppWorkerDefinitions", ct))
        {
            return null;
        }

        const string sql = @"
SELECT AppId,
       RuntimeKind,
       WorkerTypeKey,
       PluginRelativePath,
       IsEnabled
FROM omp.AppWorkerDefinitions
WHERE AppId = @AppId;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AppId", appId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new AppWorkerDefinitionEditData
        {
            AppId = rdr.GetInt32(0),
            RuntimeKind = rdr.GetString(1),
            WorkerTypeKey = rdr.GetString(2),
            PluginRelativePath = rdr.GetString(3),
            IsEnabled = rdr.GetBoolean(4)
        };
    }

    public async Task<int> SaveAppWorkerDefinitionAsync(AppWorkerDefinitionEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await TableExistsAsync(conn, "omp.AppWorkerDefinitions", ct))
        {
            throw new InvalidOperationException("Database schema missing omp.AppWorkerDefinitions. Run sql/1-setup-openmoduleplatform.sql and sql/2-initialize-openmoduleplatform.sql.");
        }

        const string updateSql = @"
UPDATE omp.AppWorkerDefinitions
SET RuntimeKind = @RuntimeKind,
    WorkerTypeKey = @WorkerTypeKey,
    PluginRelativePath = @PluginRelativePath,
    IsEnabled = @IsEnabled,
    UpdatedUtc = SYSUTCDATETIME()
WHERE AppId = @AppId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindAppWorkerDefinition(update, input);
        var affected = await update.ExecuteNonQueryAsync(ct);
        if (affected > 0)
        {
            return input.AppId;
        }

        const string insertSql = @"
INSERT INTO omp.AppWorkerDefinitions
(
    AppId,
    RuntimeKind,
    WorkerTypeKey,
    PluginRelativePath,
    IsEnabled
)
VALUES
(
    @AppId,
    @RuntimeKind,
    @WorkerTypeKey,
    @PluginRelativePath,
    @IsEnabled
);";

        await using var insert = new SqlCommand(insertSql, conn);
        BindAppWorkerDefinition(insert, input);
        await insert.ExecuteNonQueryAsync(ct);
        return input.AppId;
    }

    public async Task DeleteAppWorkerDefinitionAsync(int appId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await TableExistsAsync(conn, "omp.AppWorkerDefinitions", ct))
        {
            throw new InvalidOperationException("Database schema missing omp.AppWorkerDefinitions. Run sql/1-setup-openmoduleplatform.sql and sql/2-initialize-openmoduleplatform.sql.");
        }

        await using var cmd = new SqlCommand("DELETE FROM omp.AppWorkerDefinitions WHERE AppId = @Id;", conn);
        Add(cmd, "@Id", appId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteManualWorkerRuntimeAppInstanceAsync(Guid appInstanceId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        var hasAppInstanceRuntimeStates = await TableExistsAsync(conn, "omp.AppInstanceRuntimeStates", ct);
        var hasHostAppDeploymentStates = await TableExistsAsync(conn, "omp.HostAppDeploymentStates", ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        var templateAppInstanceId = await GetTemplateAppInstanceIdAsync(conn, tx, appInstanceId, ct);
        if (templateAppInstanceId.HasValue)
        {
            throw new InvalidOperationException(
                "This worker runtime row is managed by an instance template. Remove or disable the desired template app instead and let HostAgent materialize the change.");
        }

        var observedState = await GetWorkerRuntimeObservedStateAsync(conn, tx, appInstanceId, ct);
        if (!observedState.HasValue)
        {
            throw new InvalidOperationException("The worker runtime row was not found.");
        }

        if (observedState.Value is 1 or 2 or 3)
        {
            throw new InvalidOperationException("Stop the worker runtime before deleting the runtime row.");
        }

        if (hasAppInstanceRuntimeStates)
        {
            await ExecuteNonQueryAsync(
                conn,
                tx,
                "DELETE FROM omp.AppInstanceRuntimeStates WHERE AppInstanceId = @Id;",
                appInstanceId,
                ct);
        }

        if (hasHostAppDeploymentStates)
        {
            await ExecuteNonQueryAsync(
                conn,
                tx,
                "DELETE FROM omp.HostAppDeploymentStates WHERE AppInstanceId = @Id;",
                appInstanceId,
                ct);
        }

        await ExecuteNonQueryAsync(
            conn,
            tx,
            "DELETE FROM omp.AppInstances WHERE AppInstanceId = @Id;",
            appInstanceId,
            ct);

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ArtifactSelectionOption>> GetArtifactOptionsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT CAST(ar.ArtifactId AS nvarchar(50)),
       ar.AppId,
       a.AppKey + N' / ' + ar.Version + N' / ' + ar.PackageType
       + COALESCE(N' / ' + ar.TargetName, N'')
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
ORDER BY a.AppKey, ar.CreatedUtc DESC, ar.ArtifactId DESC;";

        var rows = new List<ArtifactSelectionOption>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            rows.Add(
                new ArtifactSelectionOption
                {
                    Value = rdr.GetString(0),
                    AppId = rdr.GetInt32(1),
                    Label = rdr.GetString(2)
                });
        }

        return rows;
    }

    public Task<IReadOnlyList<OptionItem>> GetInstanceTemplateModuleOptionsAsync(
        int instanceTemplateId,
        CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(tmi.InstanceTemplateModuleInstanceId AS nvarchar(50)),
       m.ModuleKey + N' / ' + tmi.ModuleInstanceKey + N' - ' + tmi.DisplayName
FROM omp.InstanceTemplateModuleInstances tmi
INNER JOIN omp.Modules m ON m.ModuleId = tmi.ModuleId
WHERE tmi.InstanceTemplateId = @InstanceTemplateId
ORDER BY tmi.SortOrder, tmi.ModuleInstanceKey;",
            ct,
            cmd => Add(cmd, "@InstanceTemplateId", instanceTemplateId));

    public Task<IReadOnlyList<OptionItem>> GetInstanceTemplateHostOptionsAsync(
        int instanceTemplateId,
        CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(ith.InstanceTemplateHostId AS nvarchar(50)),
       ith.HostKey + N' - ' + ith.DisplayName
FROM omp.InstanceTemplateHosts ith
WHERE ith.InstanceTemplateId = @InstanceTemplateId
ORDER BY ith.SortOrder, ith.HostKey;",
            ct,
            cmd => Add(cmd, "@InstanceTemplateId", instanceTemplateId));

    public async Task<TemplateManagedAppInstanceInfo?> GetTemplateManagedAppInstanceInfoAsync(
        Guid appInstanceId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       tai.InstanceTemplateAppInstanceId,
       it.InstanceTemplateId,
       it.TemplateKey,
       it.DisplayName
FROM omp.AppInstances ai
INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
INNER JOIN omp.InstanceTemplates it ON it.InstanceTemplateId = i.InstanceTemplateId
INNER JOIN omp.InstanceTemplateModuleInstances tmi
    ON tmi.InstanceTemplateId = i.InstanceTemplateId
   AND tmi.ModuleId = mi.ModuleId
   AND tmi.ModuleInstanceKey = mi.ModuleInstanceKey
INNER JOIN omp.InstanceTemplateAppInstances tai
    ON tai.InstanceTemplateModuleInstanceId = tmi.InstanceTemplateModuleInstanceId
   AND tai.AppInstanceKey = ai.AppInstanceKey
WHERE ai.AppInstanceId = @AppInstanceId
ORDER BY tai.InstanceTemplateAppInstanceId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@AppInstanceId", appInstanceId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new TemplateManagedAppInstanceInfo
        {
            InstanceTemplateAppInstanceId = rdr.GetInt32(0),
            InstanceTemplateId = rdr.GetInt32(1),
            InstanceTemplateKey = rdr.GetString(2),
            InstanceTemplateDisplayName = rdr.GetString(3)
        };
    }

    public async Task<InstanceTemplateAppInstanceEditData?> GetInstanceTemplateAppInstanceAsync(
        int instanceTemplateAppInstanceId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT tai.InstanceTemplateAppInstanceId,
       tmi.InstanceTemplateId,
       tai.InstanceTemplateModuleInstanceId,
       tai.InstanceTemplateHostId,
       tai.TargetHostTemplateId,
       tai.AppId,
       tai.AppInstanceKey,
       tai.DisplayName,
       tai.Description,
       tai.RoutePath,
       tai.PublicUrl,
       tai.InstallPath,
       tai.InstallationName,
       tai.DesiredArtifactId,
       tai.DesiredConfigId,
       tai.ExpectedLogin,
       tai.ExpectedClientHostName,
       tai.ExpectedClientIp,
       tai.IsEnabled,
       tai.IsAllowed,
       tai.DesiredState,
       tai.SortOrder
FROM omp.InstanceTemplateAppInstances tai
INNER JOIN omp.InstanceTemplateModuleInstances tmi
    ON tmi.InstanceTemplateModuleInstanceId = tai.InstanceTemplateModuleInstanceId
WHERE tai.InstanceTemplateAppInstanceId = @InstanceTemplateAppInstanceId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@InstanceTemplateAppInstanceId", instanceTemplateAppInstanceId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new InstanceTemplateAppInstanceEditData
        {
            InstanceTemplateAppInstanceId = rdr.GetInt32(0),
            InstanceTemplateId = rdr.GetInt32(1),
            InstanceTemplateModuleInstanceId = rdr.GetInt32(2),
            InstanceTemplateHostId = rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
            TargetHostTemplateId = rdr.IsDBNull(4) ? null : rdr.GetInt32(4),
            AppId = rdr.GetInt32(5),
            AppInstanceKey = rdr.GetString(6),
            DisplayName = rdr.GetString(7),
            Description = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            RoutePath = rdr.IsDBNull(9) ? null : rdr.GetString(9),
            PublicUrl = rdr.IsDBNull(10) ? null : rdr.GetString(10),
            InstallPath = rdr.IsDBNull(11) ? null : rdr.GetString(11),
            InstallationName = rdr.IsDBNull(12) ? null : rdr.GetString(12),
            DesiredArtifactId = rdr.IsDBNull(13) ? null : rdr.GetInt32(13),
            DesiredConfigId = rdr.IsDBNull(14) ? null : rdr.GetInt32(14),
            ExpectedLogin = rdr.IsDBNull(15) ? null : rdr.GetString(15),
            ExpectedClientHostName = rdr.IsDBNull(16) ? null : rdr.GetString(16),
            ExpectedClientIp = rdr.IsDBNull(17) ? null : rdr.GetString(17),
            IsEnabled = rdr.GetBoolean(18),
            IsAllowed = rdr.GetBoolean(19),
            DesiredState = rdr.GetByte(20),
            SortOrder = rdr.GetInt32(21)
        };
    }

    public async Task<int> SaveInstanceTemplateAppInstanceAsync(
        InstanceTemplateAppInstanceEditData input,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.InstanceTemplateAppInstanceId == 0)
        {
            const string insertSql = @"
INSERT INTO omp.InstanceTemplateAppInstances
(
    InstanceTemplateModuleInstanceId,
    InstanceTemplateHostId,
    TargetHostTemplateId,
    AppId,
    AppInstanceKey,
    DisplayName,
    Description,
    RoutePath,
    PublicUrl,
    InstallPath,
    InstallationName,
    DesiredArtifactId,
    DesiredConfigId,
    ExpectedLogin,
    ExpectedClientHostName,
    ExpectedClientIp,
    DesiredState,
    SortOrder,
    IsEnabled,
    IsAllowed
)
VALUES
(
    @InstanceTemplateModuleInstanceId,
    @InstanceTemplateHostId,
    @TargetHostTemplateId,
    @AppId,
    @AppInstanceKey,
    @DisplayName,
    @Description,
    @RoutePath,
    @PublicUrl,
    @InstallPath,
    @InstallationName,
    @DesiredArtifactId,
    @DesiredConfigId,
    @ExpectedLogin,
    @ExpectedClientHostName,
    @ExpectedClientIp,
    @DesiredState,
    @SortOrder,
    @IsEnabled,
    @IsAllowed
);
SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindInstanceTemplateAppInstance(insert, input, includePrimaryKey: false);
            input.InstanceTemplateAppInstanceId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
            return input.InstanceTemplateAppInstanceId;
        }

        const string updateSql = @"
UPDATE omp.InstanceTemplateAppInstances
SET InstanceTemplateModuleInstanceId = @InstanceTemplateModuleInstanceId,
    InstanceTemplateHostId = @InstanceTemplateHostId,
    TargetHostTemplateId = @TargetHostTemplateId,
    AppId = @AppId,
    AppInstanceKey = @AppInstanceKey,
    DisplayName = @DisplayName,
    Description = @Description,
    RoutePath = @RoutePath,
    PublicUrl = @PublicUrl,
    InstallPath = @InstallPath,
    InstallationName = @InstallationName,
    DesiredArtifactId = @DesiredArtifactId,
    DesiredConfigId = @DesiredConfigId,
    ExpectedLogin = @ExpectedLogin,
    ExpectedClientHostName = @ExpectedClientHostName,
    ExpectedClientIp = @ExpectedClientIp,
    DesiredState = @DesiredState,
    SortOrder = @SortOrder,
    IsEnabled = @IsEnabled,
    IsAllowed = @IsAllowed,
    UpdatedUtc = SYSUTCDATETIME()
WHERE InstanceTemplateAppInstanceId = @InstanceTemplateAppInstanceId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindInstanceTemplateAppInstance(update, input, includePrimaryKey: true);
        await update.ExecuteNonQueryAsync(ct);
        return input.InstanceTemplateAppInstanceId;
    }

    public Task DeleteInstanceTemplateAppInstanceAsync(int instanceTemplateAppInstanceId, CancellationToken ct)
        => DeleteAsync(
            "DELETE FROM omp.InstanceTemplateAppInstances WHERE InstanceTemplateAppInstanceId = @Id;",
            instanceTemplateAppInstanceId,
            ct);

    public async Task<string> UpgradeInstanceTemplateAppArtifactAsync(
        int instanceTemplateAppInstanceId,
        int artifactId,
        CancellationToken ct)
    {
        const string contextSql = @"
SELECT currentArtifact.Version,
       targetArtifact.Version,
       targetArtifact.AppId,
       targetArtifact.PackageType,
       targetArtifact.TargetName
FROM omp.InstanceTemplateAppInstances tai
INNER JOIN omp.Artifacts currentArtifact
    ON currentArtifact.ArtifactId = tai.DesiredArtifactId
INNER JOIN omp.Artifacts targetArtifact
    ON targetArtifact.ArtifactId = @ArtifactId
   AND targetArtifact.AppId = currentArtifact.AppId
   AND targetArtifact.PackageType = currentArtifact.PackageType
   AND ISNULL(targetArtifact.TargetName, N'') = ISNULL(currentArtifact.TargetName, N'')
   AND targetArtifact.IsEnabled = 1
WHERE tai.InstanceTemplateAppInstanceId = @InstanceTemplateAppInstanceId;";

        string currentVersion;
        string targetVersion;
        int targetAppId;
        string packageType;
        string? targetName;

        await using (var conn = _db.Create())
        {
            await conn.OpenAsync(ct);
            await using var context = new SqlCommand(contextSql, conn);
            Add(context, "@InstanceTemplateAppInstanceId", instanceTemplateAppInstanceId);
            Add(context, "@ArtifactId", artifactId);
            await using var rdr = await context.ExecuteReaderAsync(ct);
            if (!await rdr.ReadAsync(ct))
            {
                throw new InvalidOperationException("The selected artifact is not a valid upgrade for this desired app row.");
            }

            currentVersion = rdr.GetString(0);
            targetVersion = rdr.GetString(1);
            targetAppId = rdr.GetInt32(2);
            packageType = rdr.GetString(3);
            targetName = rdr.IsDBNull(4) ? null : rdr.GetString(4);
        }

        if (CompareArtifactVersions(targetVersion, currentVersion) <= 0)
        {
            throw new InvalidOperationException("The selected artifact is not newer than the current desired artifact.");
        }

        await RequireCompatibleArtifactSlotAsync(targetAppId, targetVersion, packageType, targetName, ct);

        const string updateSql = @"
UPDATE omp.InstanceTemplateAppInstances
SET DesiredArtifactId = @ArtifactId,
    UpdatedUtc = SYSUTCDATETIME()
WHERE InstanceTemplateAppInstanceId = @InstanceTemplateAppInstanceId;";

        await using (var conn = _db.Create())
        {
            await conn.OpenAsync(ct);
            await using var update = new SqlCommand(updateSql, conn);
            Add(update, "@InstanceTemplateAppInstanceId", instanceTemplateAppInstanceId);
            Add(update, "@ArtifactId", artifactId);
            await update.ExecuteNonQueryAsync(ct);
        }

        return targetVersion;
    }

    public async Task<InstanceTemplateHostEditData?> GetInstanceTemplateHostAsync(
        int instanceTemplateHostId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT InstanceTemplateHostId,
       InstanceTemplateId,
       HostTemplateId,
       HostKey,
       DisplayName,
       Environment,
       SortOrder,
       IsEnabled
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateHostId = @InstanceTemplateHostId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@InstanceTemplateHostId", instanceTemplateHostId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new InstanceTemplateHostEditData
        {
            InstanceTemplateHostId = rdr.GetInt32(0),
            InstanceTemplateId = rdr.GetInt32(1),
            HostTemplateId = rdr.GetInt32(2),
            HostKey = rdr.GetString(3),
            DisplayName = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4),
            Environment = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            SortOrder = rdr.GetInt32(6),
            IsEnabled = rdr.GetBoolean(7)
        };
    }

    public async Task<int> SaveInstanceTemplateHostAsync(
        InstanceTemplateHostEditData input,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.InstanceTemplateHostId == 0)
        {
            const string insertSql = @"
INSERT INTO omp.InstanceTemplateHosts
(
    InstanceTemplateId,
    HostTemplateId,
    HostKey,
    DisplayName,
    Environment,
    SortOrder,
    IsEnabled
)
VALUES
(
    @InstanceTemplateId,
    @HostTemplateId,
    @HostKey,
    @DisplayName,
    @Environment,
    @SortOrder,
    @IsEnabled
);
SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindInstanceTemplateHost(insert, input, includePrimaryKey: false);
            input.InstanceTemplateHostId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
            return input.InstanceTemplateHostId;
        }

        const string updateSql = @"
UPDATE omp.InstanceTemplateHosts
SET HostTemplateId = @HostTemplateId,
    HostKey = @HostKey,
    DisplayName = @DisplayName,
    Environment = @Environment,
    SortOrder = @SortOrder,
    IsEnabled = @IsEnabled,
    UpdatedUtc = SYSUTCDATETIME()
WHERE InstanceTemplateHostId = @InstanceTemplateHostId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindInstanceTemplateHost(update, input, includePrimaryKey: true);
        await update.ExecuteNonQueryAsync(ct);
        return input.InstanceTemplateHostId;
    }

    public async Task DeleteInstanceTemplateHostAsync(int instanceTemplateHostId, CancellationToken ct)
    {
        const string sql = @"
DECLARE @InstanceTemplateId int;
DECLARE @HostKey nvarchar(128);
DECLARE @InstanceId uniqueidentifier;

SELECT @InstanceTemplateId = InstanceTemplateId,
       @HostKey = HostKey
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateHostId = @Id;

DELETE FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateHostId = @Id;

SELECT TOP (1) @InstanceId = InstanceId
FROM omp.Instances
WHERE InstanceTemplateId = @InstanceTemplateId
ORDER BY CreatedUtc, InstanceId;

IF @InstanceId IS NOT NULL
   AND @HostKey IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.InstanceTemplateHosts
       WHERE InstanceTemplateId = @InstanceTemplateId
         AND HostKey = @HostKey
         AND IsEnabled = 1
   )
BEGIN
    UPDATE omp.Hosts
    SET IsEnabled = 0,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceId = @InstanceId
      AND HostKey = @HostKey;

    UPDATE assignment
    SET IsActive = 0
    FROM omp.HostDeploymentAssignments assignment
    INNER JOIN omp.Hosts host ON host.HostId = assignment.HostId
    WHERE host.InstanceId = @InstanceId
      AND host.HostKey = @HostKey;

    UPDATE desired
    SET IsEnabled = 0,
        UpdatedUtc = SYSUTCDATETIME()
    FROM omp.HostAgentDesiredStates desired
    INNER JOIN omp.Hosts host ON host.HostId = desired.HostId
    WHERE host.InstanceId = @InstanceId
      AND host.HostKey = @HostKey;
END";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = new SqlCommand(sql, conn, tx);
            Add(cmd, "@Id", instanceTemplateHostId);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<InstanceTemplateModuleEditData?> GetInstanceTemplateModuleAsync(
        int instanceTemplateModuleInstanceId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT InstanceTemplateModuleInstanceId,
       InstanceTemplateId,
       ModuleId,
       ModuleInstanceKey,
       DisplayName,
       Description,
       SortOrder,
       IsEnabled
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateModuleInstanceId = @InstanceTemplateModuleInstanceId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@InstanceTemplateModuleInstanceId", instanceTemplateModuleInstanceId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new InstanceTemplateModuleEditData
        {
            InstanceTemplateModuleInstanceId = rdr.GetInt32(0),
            InstanceTemplateId = rdr.GetInt32(1),
            ModuleId = rdr.GetInt32(2),
            ModuleInstanceKey = rdr.GetString(3),
            DisplayName = rdr.GetString(4),
            Description = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            SortOrder = rdr.GetInt32(6),
            IsEnabled = rdr.GetBoolean(7)
        };
    }

    public async Task<int> SaveInstanceTemplateModuleAsync(
        InstanceTemplateModuleEditData input,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.InstanceTemplateModuleInstanceId == 0)
        {
            const string insertSql = @"
INSERT INTO omp.InstanceTemplateModuleInstances
(
    InstanceTemplateId,
    ModuleId,
    ModuleInstanceKey,
    DisplayName,
    Description,
    SortOrder,
    IsEnabled
)
VALUES
(
    @InstanceTemplateId,
    @ModuleId,
    @ModuleInstanceKey,
    @DisplayName,
    @Description,
    @SortOrder,
    @IsEnabled
);
SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindInstanceTemplateModule(insert, input, includePrimaryKey: false);
            input.InstanceTemplateModuleInstanceId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
            return input.InstanceTemplateModuleInstanceId;
        }

        const string updateSql = @"
UPDATE omp.InstanceTemplateModuleInstances
SET ModuleId = @ModuleId,
    ModuleInstanceKey = @ModuleInstanceKey,
    DisplayName = @DisplayName,
    Description = @Description,
    SortOrder = @SortOrder,
    IsEnabled = @IsEnabled,
    UpdatedUtc = SYSUTCDATETIME()
WHERE InstanceTemplateModuleInstanceId = @InstanceTemplateModuleInstanceId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindInstanceTemplateModule(update, input, includePrimaryKey: true);
        await update.ExecuteNonQueryAsync(ct);
        return input.InstanceTemplateModuleInstanceId;
    }

    public Task DeleteInstanceTemplateModuleAsync(
        int instanceTemplateModuleInstanceId,
        CancellationToken ct)
        => DeleteAsync(
            "DELETE FROM omp.InstanceTemplateModuleInstances WHERE InstanceTemplateModuleInstanceId = @Id;",
            instanceTemplateModuleInstanceId,
            ct);

    // -------------------------------------------------------------------------
    // Instance editing
    // -------------------------------------------------------------------------

    public async Task<InstanceEditData?> GetInstanceAsync(Guid instanceId, CancellationToken ct)
    {
        const string sql = @"
SELECT InstanceId,
       InstanceKey,
       DisplayName,
       Description,
       InstanceTemplateId,
       IsEnabled
FROM omp.Instances
WHERE InstanceId = @InstanceId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new InstanceEditData
        {
            InstanceId = rdr.GetGuid(0),
            InstanceKey = rdr.GetString(1),
            DisplayName = rdr.GetString(2),
            Description = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            InstanceTemplateId = rdr.IsDBNull(4) ? null : rdr.GetInt32(4),
            IsEnabled = rdr.GetBoolean(5)
        };
    }

    public async Task<Guid> SaveInstanceAsync(InstanceEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.InstanceId == Guid.Empty)
        {
            input.InstanceId = Guid.NewGuid();

            const string insertSql = @"
INSERT INTO omp.Instances
(
    InstanceId,
    InstanceKey,
    DisplayName,
    Description,
    InstanceTemplateId,
    IsEnabled
)
VALUES
(
    @InstanceId,
    @InstanceKey,
    @DisplayName,
    @Description,
    @InstanceTemplateId,
    @IsEnabled
);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindInstance(insert, input);
            await insert.ExecuteNonQueryAsync(ct);
            return input.InstanceId;
        }

        const string updateSql = @"
UPDATE omp.Instances
SET InstanceKey = @InstanceKey,
    DisplayName = @DisplayName,
    Description = @Description,
    InstanceTemplateId = @InstanceTemplateId,
    IsEnabled = @IsEnabled,
    UpdatedUtc = SYSUTCDATETIME()
WHERE InstanceId = @InstanceId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindInstance(update, input);
        await update.ExecuteNonQueryAsync(ct);
        return input.InstanceId;
    }

    public Task DeleteInstanceAsync(Guid instanceId, CancellationToken ct)
        => DeleteAsync("DELETE FROM omp.Instances WHERE InstanceId = @Id;", instanceId, ct);

    // -------------------------------------------------------------------------
    // Host editing
    // -------------------------------------------------------------------------

    public async Task<HostEditData?> GetHostAsync(Guid hostId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var hasHostBaseUrl = await HostBaseUrlColumnExistsAsync(conn, ct);
        var hostBaseUrlSelect = hasHostBaseUrl
            ? "BaseUrl"
            : "CAST(NULL AS nvarchar(300)) AS BaseUrl";

        var sql = $@"
SELECT HostId,
       InstanceId,
       HostKey,
       DisplayName,
       {hostBaseUrlSelect},
       Environment,
       OsFamily,
       OsVersion,
       Architecture,
       IsEnabled
FROM omp.Hosts
WHERE HostId = @HostId;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@HostId", hostId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new HostEditData
        {
            HostId = rdr.GetGuid(0),
            InstanceId = rdr.GetGuid(1),
            HostKey = rdr.GetString(2),
            DisplayName = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            BaseUrl = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            Environment = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            OsFamily = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            OsVersion = rdr.IsDBNull(7) ? null : rdr.GetString(7),
            Architecture = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            IsEnabled = rdr.GetBoolean(9)
        };
    }

    public async Task<Guid> SaveHostAsync(HostEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var hasHostBaseUrl = await HostBaseUrlColumnExistsAsync(conn, ct);
        if (!hasHostBaseUrl && !string.IsNullOrWhiteSpace(input.BaseUrl))
        {
            throw new InvalidOperationException("Database schema missing omp.Hosts.BaseUrl. Run sql/1-setup-openmoduleplatform.sql and sql/2-initialize-openmoduleplatform.sql.");
        }

        var hostBaseUrlInsertColumns = hasHostBaseUrl
            ? ",\n    BaseUrl"
            : string.Empty;
        var hostBaseUrlInsertValues = hasHostBaseUrl
            ? ",\n    @BaseUrl"
            : string.Empty;
        var hostBaseUrlUpdate = hasHostBaseUrl
            ? ",\n    BaseUrl = @BaseUrl"
            : string.Empty;

        if (input.HostId == Guid.Empty)
        {
            input.HostId = Guid.NewGuid();

            var insertSql = $@"
INSERT INTO omp.Hosts
(
    HostId,
    InstanceId,
    HostKey,
    DisplayName{hostBaseUrlInsertColumns},
    Environment,
    OsFamily,
    OsVersion,
    Architecture,
    IsEnabled
)
VALUES
(
    @HostId,
    @InstanceId,
    @HostKey,
    @DisplayName{hostBaseUrlInsertValues},
    @Environment,
    @OsFamily,
    @OsVersion,
    @Architecture,
    @IsEnabled
);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindHost(insert, input, hasHostBaseUrl);
            await insert.ExecuteNonQueryAsync(ct);
            return input.HostId;
        }

        var updateSql = $@"
UPDATE omp.Hosts
SET InstanceId = @InstanceId,
    HostKey = @HostKey,
    DisplayName = @DisplayName{hostBaseUrlUpdate},
    Environment = @Environment,
    OsFamily = @OsFamily,
    OsVersion = @OsVersion,
    Architecture = @Architecture,
    IsEnabled = @IsEnabled,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostId = @HostId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindHost(update, input, hasHostBaseUrl);
        await update.ExecuteNonQueryAsync(ct);
        return input.HostId;
    }

    public async Task DeleteHostAsync(Guid hostId, CancellationToken ct)
    {
        const string sql = @"
DELETE FROM omp.HostAppDeploymentStates
WHERE HostId = @HostId;

DELETE FROM omp.HostArtifactStates
WHERE HostId = @HostId;

DELETE FROM omp.HostArtifactRequirements
WHERE HostId = @HostId;

DELETE FROM omp.HostDeploymentAssignments
WHERE HostId = @HostId;

DELETE FROM omp.HostDeployments
WHERE HostId = @HostId;

UPDATE omp.WorkerInstances
SET HostId = NULL,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostId = @HostId;

UPDATE omp.AppInstances
SET HostId = NULL,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostId = @HostId;

DELETE FROM omp.Hosts
WHERE HostId = @HostId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@HostId", hostId);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Module definition editing
    // -------------------------------------------------------------------------

    public async Task<ModuleEditData?> GetModuleAsync(int moduleId, CancellationToken ct)
    {
        const string sql = @"
SELECT ModuleId,
       ModuleKey,
       DisplayName,
       ModuleType,
       SchemaName,
       Description,
       IsEnabled,
       SortOrder
FROM omp.Modules
WHERE ModuleId = @ModuleId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ModuleId", moduleId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ModuleEditData
        {
            ModuleId = rdr.GetInt32(0),
            ModuleKey = rdr.GetString(1),
            DisplayName = rdr.GetString(2),
            ModuleType = rdr.GetString(3),
            SchemaName = rdr.GetString(4),
            Description = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            IsEnabled = rdr.GetBoolean(6),
            SortOrder = rdr.GetInt32(7)
        };
    }

    public async Task<int> SaveModuleAsync(ModuleEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.ModuleId == 0)
        {
            const string insertSql = @"
INSERT INTO omp.Modules
(
    ModuleKey,
    DisplayName,
    ModuleType,
    SchemaName,
    Description,
    IsEnabled,
    SortOrder
)
VALUES
(
    @ModuleKey,
    @DisplayName,
    @ModuleType,
    @SchemaName,
    @Description,
    @IsEnabled,
    @SortOrder
);
SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindModule(insert, input, includePrimaryKey: false);
            input.ModuleId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
            return input.ModuleId;
        }

        const string updateSql = @"
UPDATE omp.Modules
SET ModuleKey = @ModuleKey,
    DisplayName = @DisplayName,
    ModuleType = @ModuleType,
    SchemaName = @SchemaName,
    Description = @Description,
    IsEnabled = @IsEnabled,
    SortOrder = @SortOrder,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ModuleId = @ModuleId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindModule(update, input, includePrimaryKey: true);
        await update.ExecuteNonQueryAsync(ct);
        return input.ModuleId;
    }

    public Task DeleteModuleAsync(int moduleId, CancellationToken ct)
        => DeleteAsync("DELETE FROM omp.Modules WHERE ModuleId = @Id;", moduleId, ct);

    // -------------------------------------------------------------------------
    // Module-instance editing
    // -------------------------------------------------------------------------

    public async Task<ModuleInstanceEditData?> GetModuleInstanceAsync(Guid moduleInstanceId, CancellationToken ct)
    {
        const string sql = @"
SELECT ModuleInstanceId,
       InstanceId,
       ModuleId,
       ModuleInstanceKey,
       DisplayName,
       Description,
       IsEnabled,
       SortOrder
FROM omp.ModuleInstances
WHERE ModuleInstanceId = @ModuleInstanceId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ModuleInstanceId", moduleInstanceId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ModuleInstanceEditData
        {
            ModuleInstanceId = rdr.GetGuid(0),
            InstanceId = rdr.GetGuid(1),
            ModuleId = rdr.GetInt32(2),
            ModuleInstanceKey = rdr.GetString(3),
            DisplayName = rdr.GetString(4),
            Description = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            IsEnabled = rdr.GetBoolean(6),
            SortOrder = rdr.GetInt32(7)
        };
    }

    public async Task<Guid> SaveModuleInstanceAsync(ModuleInstanceEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.ModuleInstanceId == Guid.Empty)
        {
            input.ModuleInstanceId = Guid.NewGuid();

            const string insertSql = @"
INSERT INTO omp.ModuleInstances
(
    ModuleInstanceId,
    InstanceId,
    ModuleId,
    ModuleInstanceKey,
    DisplayName,
    Description,
    IsEnabled,
    SortOrder
)
VALUES
(
    @ModuleInstanceId,
    @InstanceId,
    @ModuleId,
    @ModuleInstanceKey,
    @DisplayName,
    @Description,
    @IsEnabled,
    @SortOrder
);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindModuleInstance(insert, input);
            await insert.ExecuteNonQueryAsync(ct);
            return input.ModuleInstanceId;
        }

        const string updateSql = @"
UPDATE omp.ModuleInstances
SET InstanceId = @InstanceId,
    ModuleId = @ModuleId,
    ModuleInstanceKey = @ModuleInstanceKey,
    DisplayName = @DisplayName,
    Description = @Description,
    IsEnabled = @IsEnabled,
    SortOrder = @SortOrder,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ModuleInstanceId = @ModuleInstanceId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindModuleInstance(update, input);
        await update.ExecuteNonQueryAsync(ct);
        return input.ModuleInstanceId;
    }

    public Task DeleteModuleInstanceAsync(Guid moduleInstanceId, CancellationToken ct)
        => DeleteAsync(
            "DELETE FROM omp.ModuleInstances WHERE ModuleInstanceId = @Id;",
            moduleInstanceId,
            ct);

    // -------------------------------------------------------------------------
    // App-definition editing
    // -------------------------------------------------------------------------

    public async Task<AppEditData?> GetAppAsync(int appId, CancellationToken ct)
    {
        const string sql = @"
SELECT AppId,
       ModuleId,
       AppKey,
       DisplayName,
       AppType,
       AllowMultipleActiveInstances,
       Description,
       IsEnabled,
       SortOrder
FROM omp.Apps
WHERE AppId = @AppId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AppId", appId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new AppEditData
        {
            AppId = rdr.GetInt32(0),
            ModuleId = rdr.GetInt32(1),
            AppKey = rdr.GetString(2),
            DisplayName = rdr.GetString(3),
            AppType = rdr.GetString(4),
            AllowMultipleActiveInstances = rdr.GetBoolean(5),
            Description = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            IsEnabled = rdr.GetBoolean(7),
            SortOrder = rdr.GetInt32(8)
        };
    }

    public async Task<int> SaveAppAsync(AppEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.AppId == 0)
        {
            const string insertSql = @"
INSERT INTO omp.Apps
(
    ModuleId,
    AppKey,
    DisplayName,
    AppType,
    AllowMultipleActiveInstances,
    Description,
    IsEnabled,
    SortOrder
)
VALUES
(
    @ModuleId,
    @AppKey,
    @DisplayName,
    @AppType,
    @AllowMultipleActiveInstances,
    @Description,
    @IsEnabled,
    @SortOrder
);
SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindApp(insert, input, includePrimaryKey: false);
            input.AppId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
            return input.AppId;
        }

        const string updateSql = @"
UPDATE omp.Apps
SET ModuleId = @ModuleId,
    AppKey = @AppKey,
    DisplayName = @DisplayName,
    AppType = @AppType,
    AllowMultipleActiveInstances = @AllowMultipleActiveInstances,
    Description = @Description,
    IsEnabled = @IsEnabled,
    SortOrder = @SortOrder,
    UpdatedUtc = SYSUTCDATETIME()
WHERE AppId = @AppId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindApp(update, input, includePrimaryKey: true);
        await update.ExecuteNonQueryAsync(ct);
        return input.AppId;
    }

    public Task DeleteAppAsync(int appId, CancellationToken ct)
        => DeleteAsync("DELETE FROM omp.Apps WHERE AppId = @Id;", appId, ct);

    // -------------------------------------------------------------------------
    // Artifact editing
    // -------------------------------------------------------------------------

    public async Task<ArtifactEditData?> GetArtifactAsync(int artifactId, CancellationToken ct)
    {
        const string sql = @"
SELECT ar.ArtifactId,
       ar.AppId,
       m.ModuleKey,
       a.AppKey,
       ar.Version,
       ar.PackageType,
       ar.TargetName,
       ar.RelativePath,
       ar.Sha256,
       ar.IsEnabled
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
WHERE ar.ArtifactId = @ArtifactId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ArtifactId", artifactId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ArtifactEditData
        {
            ArtifactId = rdr.GetInt32(0),
            AppId = rdr.GetInt32(1),
            ModuleKey = rdr.GetString(2),
            AppKey = rdr.GetString(3),
            Version = rdr.GetString(4),
            PackageType = rdr.GetString(5),
            TargetName = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            RelativePath = rdr.IsDBNull(7) ? null : rdr.GetString(7),
            Sha256 = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            IsEnabled = rdr.GetBoolean(9)
        };
    }

    public async Task<int> SaveArtifactAsync(ArtifactEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.ArtifactId == 0)
        {
            const string insertSql = @"
INSERT INTO omp.Artifacts
(
    AppId,
    Version,
    PackageType,
    TargetName,
    RelativePath,
    Sha256,
    IsEnabled
)
VALUES
(
    @AppId,
    @Version,
    @PackageType,
    @TargetName,
    @RelativePath,
    @Sha256,
    @IsEnabled
);
SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindArtifact(insert, input, includePrimaryKey: false);
            input.ArtifactId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
            return input.ArtifactId;
        }

        const string updateSql = @"
UPDATE omp.Artifacts
SET AppId = @AppId,
    Version = @Version,
    PackageType = @PackageType,
    TargetName = @TargetName,
    RelativePath = @RelativePath,
    Sha256 = @Sha256,
    IsEnabled = @IsEnabled,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ArtifactId = @ArtifactId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindArtifact(update, input, includePrimaryKey: true);
        await update.ExecuteNonQueryAsync(ct);
        return input.ArtifactId;
    }

    public async Task DeleteArtifactAsync(int artifactId, CancellationToken ct)
    {
        const string sql = @"
DELETE FROM omp.HostAppDeploymentStates
WHERE ArtifactId = @ArtifactId;

DELETE FROM omp.HostAgentRuntimeStates
WHERE ArtifactId = @ArtifactId;

DELETE FROM omp.HostArtifactStates
WHERE ArtifactId = @ArtifactId;

DELETE FROM omp.HostArtifactRequirements
WHERE ArtifactId = @ArtifactId;

DELETE FROM omp.ArtifactConfigurationFiles
WHERE ArtifactId = @ArtifactId;

DELETE FROM omp.Artifacts
WHERE ArtifactId = @ArtifactId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            await using var cmd = new SqlCommand(sql, conn, tx);
            Add(cmd, "@ArtifactId", artifactId);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Module definition document editing
    // -------------------------------------------------------------------------

    public async Task<ModuleDefinitionSaveResult> SaveModuleDefinitionDocumentAsync(
        ModuleDefinitionDocumentEditData input,
        bool replaceExisting,
        CancellationToken ct)
    {
        const string findSql = @"
SELECT ModuleDefinitionDocumentId,
       DefinitionSha256
FROM omp.ModuleDefinitionDocuments
WHERE ModuleKey = @ModuleKey
  AND DefinitionVersion = @DefinitionVersion;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            int? existingId = null;
            string? existingSha256 = null;
            await using (var find = new SqlCommand(findSql, conn, tx))
            {
                Add(find, "@ModuleKey", input.ModuleKey);
                Add(find, "@DefinitionVersion", input.DefinitionVersion);
                await using var rdr = await find.ExecuteReaderAsync(ct);
                if (await rdr.ReadAsync(ct))
                {
                    existingId = rdr.GetInt32(0);
                    existingSha256 = rdr.GetString(1);
                }
            }

            var isIdentical = existingId.HasValue
                && string.Equals(existingSha256, input.DefinitionSha256, StringComparison.OrdinalIgnoreCase);

            if (existingId.HasValue && !replaceExisting && !isIdentical)
            {
                throw new InvalidOperationException(
                    "A module definition with the same module key and version already exists, but the uploaded JSON is different. Confirm replacement or use a new definition version.");
            }

            var documentId = existingId ?? await InsertModuleDefinitionDocumentAsync(conn, tx, input, ct);
            if (existingId.HasValue && (replaceExisting || !isIdentical))
            {
                await UpdateModuleDefinitionDocumentAsync(conn, tx, documentId, input, ct);
            }

            await ReplaceModuleDefinitionCompatibilityAsync(conn, tx, documentId, input.CompatibleArtifacts, ct);
            await tx.CommitAsync(ct);

            return new ModuleDefinitionSaveResult
            {
                ModuleDefinitionDocumentId = documentId,
                Created = !existingId.HasValue,
                Replaced = existingId.HasValue && replaceExisting && !isIdentical,
                WasIdentical = existingId.HasValue && isIdentical
            };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<ModuleDefinitionApplyResult> ApplyModuleDefinitionDocumentAsync(
        int moduleDefinitionDocumentId,
        bool allowTemporaryIncompatibleArtifacts,
        CancellationToken ct)
    {
        var incompatibleReferences = await GetIncompatibleArtifactReferencesForModuleDefinitionAsync(
            moduleDefinitionDocumentId,
            ct);

        if (incompatibleReferences.Count > 0 && !allowTemporaryIncompatibleArtifacts)
        {
            return new ModuleDefinitionApplyResult
            {
                Applied = false,
                IncompatibleReferences = incompatibleReferences
            };
        }

        const string sql = @"
DECLARE @DefinitionJson nvarchar(max);
DECLARE @ModuleKey nvarchar(100);
DECLARE @ModuleDisplayName nvarchar(200);
DECLARE @ModuleType nvarchar(50);
DECLARE @SchemaName nvarchar(128);
DECLARE @Description nvarchar(500);
DECLARE @SortOrder int;
DECLARE @IsEnabled bit;
DECLARE @ModuleId int;

SELECT @DefinitionJson = DefinitionJson,
       @ModuleKey = ModuleKey
FROM omp.ModuleDefinitionDocuments
WHERE ModuleDefinitionDocumentId = @ModuleDefinitionDocumentId;

IF @DefinitionJson IS NULL
BEGIN
    THROW 53230, N'Module definition document was not found.', 1;
END;

SELECT @ModuleDisplayName = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.displayName'), N''),
       @ModuleType = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.moduleType'), N''),
       @SchemaName = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.schemaName'), N''),
       @Description = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.description'), N''),
       @SortOrder = TRY_CONVERT(int, JSON_VALUE(@DefinitionJson, N'$.module.sortOrder')),
       @IsEnabled = TRY_CONVERT(bit, JSON_VALUE(@DefinitionJson, N'$.module.isEnabled'));

IF @ModuleDisplayName IS NOT NULL
BEGIN
    MERGE omp.Modules AS target
    USING
    (
        SELECT @ModuleKey AS ModuleKey,
               @ModuleDisplayName AS DisplayName,
               COALESCE(@ModuleType, N'WebAppModule') AS ModuleType,
               COALESCE(@SchemaName, @ModuleKey) AS SchemaName,
               @Description AS Description,
               COALESCE(@SortOrder, 0) AS SortOrder,
               COALESCE(@IsEnabled, CONVERT(bit, 1)) AS IsEnabled
    ) AS source
    ON target.ModuleKey = source.ModuleKey
    WHEN MATCHED THEN
        UPDATE SET DisplayName = source.DisplayName,
                   ModuleType = source.ModuleType,
                   SchemaName = source.SchemaName,
                   Description = source.Description,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(ModuleKey, DisplayName, ModuleType, SchemaName, Description, SortOrder, IsEnabled)
        VALUES(source.ModuleKey, source.DisplayName, source.ModuleType, source.SchemaName, source.Description, source.SortOrder, source.IsEnabled);
END;

SELECT @ModuleId = ModuleId
FROM omp.Modules
WHERE ModuleKey = @ModuleKey;

IF @ModuleId IS NOT NULL
BEGIN
    ;WITH AppRows AS
    (
        SELECT AppKey,
               COALESCE(NULLIF(DisplayName, N''), AppKey) AS DisplayName,
               COALESCE(NULLIF(AppType, N''), N'WebApp') AS AppType,
               COALESCE(AllowMultipleActiveInstances, CONVERT(bit, 0)) AS AllowMultipleActiveInstances,
               NULLIF(Description, N'') AS Description,
               COALESCE(SortOrder, 0) AS SortOrder,
               COALESCE(IsEnabled, CONVERT(bit, 1)) AS IsEnabled
        FROM OPENJSON(@DefinitionJson, N'$.apps')
        WITH
        (
            AppKey nvarchar(100) N'$.appKey',
            DisplayName nvarchar(200) N'$.displayName',
            AppType nvarchar(50) N'$.appType',
            AllowMultipleActiveInstances bit N'$.allowMultipleActiveInstances',
            Description nvarchar(500) N'$.description',
            SortOrder int N'$.sortOrder',
            IsEnabled bit N'$.isEnabled'
        )
        WHERE AppKey IS NOT NULL
    )
    MERGE omp.Apps AS target
    USING AppRows AS source
    ON target.ModuleId = @ModuleId
    AND target.AppKey = source.AppKey
    WHEN MATCHED THEN
        UPDATE SET DisplayName = source.DisplayName,
                   AppType = source.AppType,
                   AllowMultipleActiveInstances = source.AllowMultipleActiveInstances,
                   Description = source.Description,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(ModuleId, AppKey, DisplayName, AppType, AllowMultipleActiveInstances, Description, SortOrder, IsEnabled)
        VALUES(@ModuleId, source.AppKey, source.DisplayName, source.AppType, source.AllowMultipleActiveInstances, source.Description, source.SortOrder, source.IsEnabled);
END;

IF @ModuleId IS NOT NULL
BEGIN
    ;WITH RequiredModuleInstances AS
    (
        SELECT COALESCE(NULLIF(InstanceKey, N''), N'default') AS InstanceKey,
               COALESCE(NULLIF(ModuleInstanceKey, N''), @ModuleKey) AS ModuleInstanceKey,
               COALESCE(NULLIF(DisplayName, N''), @ModuleDisplayName, @ModuleKey) AS DisplayName,
               NULLIF(Description, N'') AS Description,
               COALESCE(SortOrder, @SortOrder, 0) AS SortOrder,
               COALESCE(IsEnabled, CONVERT(bit, 1)) AS IsEnabled
        FROM OPENJSON(@DefinitionJson, N'$.integrity.requiredOmpRows.moduleInstances')
        WITH
        (
            InstanceKey nvarchar(100) N'$.instanceKey',
            ModuleInstanceKey nvarchar(100) N'$.moduleInstanceKey',
            DisplayName nvarchar(200) N'$.displayName',
            Description nvarchar(500) N'$.description',
            SortOrder int N'$.sortOrder',
            IsEnabled bit N'$.isEnabled'
        )
        WHERE ModuleInstanceKey IS NOT NULL
    ),
    ResolvedModuleInstances AS
    (
        SELECT instance.InstanceId,
               @ModuleId AS ModuleId,
               source.ModuleInstanceKey,
               source.DisplayName,
               source.Description,
               source.SortOrder,
               source.IsEnabled
        FROM RequiredModuleInstances source
        INNER JOIN omp.Instances instance ON instance.InstanceKey = source.InstanceKey
    )
    MERGE omp.ModuleInstances AS target
    USING ResolvedModuleInstances AS source
    ON target.InstanceId = source.InstanceId
    AND target.ModuleInstanceKey = source.ModuleInstanceKey
    WHEN MATCHED THEN
        UPDATE SET ModuleId = source.ModuleId,
                   DisplayName = source.DisplayName,
                   Description = source.Description,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(ModuleInstanceId, InstanceId, ModuleId, ModuleInstanceKey, DisplayName, Description, IsEnabled, SortOrder)
        VALUES(NEWID(), source.InstanceId, source.ModuleId, source.ModuleInstanceKey, source.DisplayName, source.Description, source.IsEnabled, source.SortOrder);

    ;WITH RequiredTemplateModuleInstances AS
    (
        SELECT COALESCE(NULLIF(InstanceTemplateKey, N''), N'default') AS InstanceTemplateKey,
               COALESCE(NULLIF(ModuleInstanceKey, N''), @ModuleKey) AS ModuleInstanceKey,
               COALESCE(NULLIF(DisplayName, N''), @ModuleDisplayName, @ModuleKey) AS DisplayName,
               NULLIF(Description, N'') AS Description,
               COALESCE(SortOrder, @SortOrder, 0) AS SortOrder,
               COALESCE(IsEnabled, CONVERT(bit, 1)) AS IsEnabled
        FROM OPENJSON(@DefinitionJson, N'$.integrity.requiredOmpRows.instanceTemplateModuleInstances')
        WITH
        (
            InstanceTemplateKey nvarchar(100) N'$.instanceTemplateKey',
            ModuleInstanceKey nvarchar(100) N'$.moduleInstanceKey',
            DisplayName nvarchar(200) N'$.displayName',
            Description nvarchar(500) N'$.description',
            SortOrder int N'$.sortOrder',
            IsEnabled bit N'$.isEnabled'
        )
        WHERE ModuleInstanceKey IS NOT NULL
    ),
    ResolvedTemplateModuleInstances AS
    (
        SELECT template.InstanceTemplateId,
               @ModuleId AS ModuleId,
               source.ModuleInstanceKey,
               source.DisplayName,
               source.Description,
               source.SortOrder,
               source.IsEnabled
        FROM RequiredTemplateModuleInstances source
        INNER JOIN omp.InstanceTemplates template ON template.TemplateKey = source.InstanceTemplateKey
    )
    MERGE omp.InstanceTemplateModuleInstances AS target
    USING ResolvedTemplateModuleInstances AS source
    ON target.InstanceTemplateId = source.InstanceTemplateId
    AND target.ModuleInstanceKey = source.ModuleInstanceKey
    WHEN MATCHED THEN
        UPDATE SET ModuleId = source.ModuleId,
                   DisplayName = source.DisplayName,
                   Description = source.Description,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(InstanceTemplateId, ModuleId, ModuleInstanceKey, DisplayName, Description, SortOrder, IsEnabled)
        VALUES(source.InstanceTemplateId, source.ModuleId, source.ModuleInstanceKey, source.DisplayName, source.Description, source.SortOrder, source.IsEnabled);

    ;WITH RequiredAppInstances AS
    (
        SELECT COALESCE(NULLIF(InstanceKey, N''), N'default') AS InstanceKey,
               COALESCE(NULLIF(ModuleInstanceKey, N''), @ModuleKey) AS ModuleInstanceKey,
               AppInstanceKey,
               COALESCE(NULLIF(AppKey, N''), AppInstanceKey) AS AppKey,
               COALESCE(NULLIF(DisplayName, N''), AppInstanceKey) AS DisplayName,
               NULLIF(Description, N'') AS Description,
               NULLIF(RoutePath, N'') AS RoutePath,
               NULLIF(PublicUrl, N'') AS PublicUrl,
               NULLIF(InstallPath, N'') AS InstallPath,
               NULLIF(InstallationName, N'') AS InstallationName,
               NULLIF(HostBinding, N'') AS HostBinding,
               NULLIF(HostKey, N'') AS HostKey,
               NULLIF(COALESCE(TargetHostTemplateKey, HostTemplateKey), N'') AS TargetHostTemplateKey,
               NULLIF(PackageType, N'') AS PackageType,
               NULLIF(TargetName, N'') AS TargetName,
               COALESCE(DesiredState, CONVERT(tinyint, 1)) AS DesiredState,
               COALESCE(SortOrder, 0) AS SortOrder,
               COALESCE(IsEnabled, CONVERT(bit, 1)) AS IsEnabled,
               COALESCE(IsAllowed, CONVERT(bit, 1)) AS IsAllowed
        FROM OPENJSON(@DefinitionJson, N'$.integrity.requiredOmpRows.appInstances')
        WITH
        (
            InstanceKey nvarchar(100) N'$.instanceKey',
            ModuleInstanceKey nvarchar(100) N'$.moduleInstanceKey',
            AppInstanceKey nvarchar(100) N'$.appInstanceKey',
            AppKey nvarchar(100) N'$.appKey',
            DisplayName nvarchar(200) N'$.displayName',
            Description nvarchar(500) N'$.description',
            RoutePath nvarchar(256) N'$.routePath',
            PublicUrl nvarchar(500) N'$.publicUrl',
            InstallPath nvarchar(500) N'$.installPath',
            InstallationName nvarchar(150) N'$.installationName',
            HostBinding nvarchar(100) N'$.hostBinding',
            HostKey nvarchar(128) N'$.hostKey',
            TargetHostTemplateKey nvarchar(100) N'$.targetHostTemplateKey',
            HostTemplateKey nvarchar(100) N'$.hostTemplateKey',
            PackageType nvarchar(50) N'$.packageType',
            TargetName nvarchar(100) N'$.targetName',
            DesiredState tinyint N'$.desiredState',
            SortOrder int N'$.sortOrder',
            IsEnabled bit N'$.isEnabled',
            IsAllowed bit N'$.isAllowed'
        )
        WHERE AppInstanceKey IS NOT NULL
    ),
    ResolvedAppInstances AS
    (
        SELECT moduleInstance.ModuleInstanceId,
               host.HostId,
               targetTemplate.HostTemplateId AS TargetHostTemplateId,
               app.AppId,
               source.AppInstanceKey,
               COALESCE(NULLIF(source.DisplayName, source.AppInstanceKey), app.DisplayName) AS DisplayName,
               source.Description,
               source.RoutePath,
               source.PublicUrl,
               source.InstallPath,
               source.InstallationName,
               source.PackageType,
               source.TargetName,
               source.DesiredState,
               source.SortOrder,
               source.IsEnabled,
               source.IsAllowed,
               CASE
                   WHEN app.AppType IN (N'Portal', N'WebApp') THEN N'web-app'
                   WHEN app.AppType = N'ServiceApp' THEN N'service-app'
                   WHEN app.AppType = N'Worker' THEN N'worker'
                   WHEN app.AppType = N'HostAgent' THEN N'host-agent'
                   WHEN app.AppType = N'WorkerHost' THEN N'worker-host'
                   ELSE NULL
               END AS DefaultPackageType
        FROM RequiredAppInstances source
        INNER JOIN omp.Instances instance ON instance.InstanceKey = source.InstanceKey
        INNER JOIN omp.ModuleInstances moduleInstance
            ON moduleInstance.InstanceId = instance.InstanceId
           AND moduleInstance.ModuleInstanceKey = source.ModuleInstanceKey
        INNER JOIN omp.Apps app ON app.ModuleId = @ModuleId AND app.AppKey = source.AppKey
        LEFT JOIN omp.Hosts host
            ON host.InstanceId = instance.InstanceId
           AND host.HostKey = source.HostKey
        LEFT JOIN omp.HostTemplates targetTemplate ON targetTemplate.TemplateKey = source.TargetHostTemplateKey
        WHERE (source.HostKey IS NULL OR host.HostId IS NOT NULL)
          AND (source.TargetHostTemplateKey IS NULL OR targetTemplate.HostTemplateId IS NOT NULL)
          AND (source.HostBinding IS NULL
               OR source.HostBinding = N'host-neutral'
               OR source.HostKey IS NOT NULL
               OR source.TargetHostTemplateKey IS NOT NULL)
    ),
    AppInstancesWithArtifact AS
    (
        SELECT source.*,
               artifact.ArtifactId
        FROM ResolvedAppInstances source
        OUTER APPLY
        (
            SELECT TOP (1) item.ArtifactId
            FROM omp.Artifacts item
            WHERE item.AppId = source.AppId
              AND item.IsEnabled = 1
              AND item.PackageType = COALESCE(source.PackageType, source.DefaultPackageType)
              AND (source.TargetName IS NULL OR item.TargetName = source.TargetName)
            ORDER BY
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 4)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 3)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 2)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 1)), 0) DESC,
                item.Version DESC,
                item.ArtifactId DESC
        ) artifact
    )
    MERGE omp.AppInstances AS target
    USING AppInstancesWithArtifact AS source
    ON target.ModuleInstanceId = source.ModuleInstanceId
    AND target.AppInstanceKey = source.AppInstanceKey
    WHEN MATCHED THEN
        UPDATE SET HostId = source.HostId,
                   TargetHostTemplateId = source.TargetHostTemplateId,
                   AppId = source.AppId,
                   DisplayName = source.DisplayName,
                   Description = source.Description,
                   RoutePath = source.RoutePath,
                   PublicUrl = source.PublicUrl,
                   InstallPath = source.InstallPath,
                   InstallationName = source.InstallationName,
                   ArtifactId = COALESCE(source.ArtifactId, target.ArtifactId),
                   DesiredState = source.DesiredState,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   IsAllowed = source.IsAllowed,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(AppInstanceId, ModuleInstanceId, HostId, TargetHostTemplateId, AppId, AppInstanceKey, DisplayName, Description, RoutePath, PublicUrl, InstallPath, InstallationName, ArtifactId, IsEnabled, IsAllowed, DesiredState, SortOrder)
        VALUES(NEWID(), source.ModuleInstanceId, source.HostId, source.TargetHostTemplateId, source.AppId, source.AppInstanceKey, source.DisplayName, source.Description, source.RoutePath, source.PublicUrl, source.InstallPath, source.InstallationName, source.ArtifactId, source.IsEnabled, source.IsAllowed, source.DesiredState, source.SortOrder);

    ;WITH RequiredTemplateAppInstances AS
    (
        SELECT COALESCE(NULLIF(InstanceTemplateKey, N''), N'default') AS InstanceTemplateKey,
               COALESCE(NULLIF(ModuleInstanceKey, N''), @ModuleKey) AS ModuleInstanceKey,
               AppInstanceKey,
               COALESCE(NULLIF(AppKey, N''), AppInstanceKey) AS AppKey,
               COALESCE(NULLIF(DisplayName, N''), AppInstanceKey) AS DisplayName,
               NULLIF(Description, N'') AS Description,
               NULLIF(RoutePath, N'') AS RoutePath,
               NULLIF(PublicUrl, N'') AS PublicUrl,
               NULLIF(InstallPath, N'') AS InstallPath,
               NULLIF(InstallationName, N'') AS InstallationName,
               NULLIF(HostBinding, N'') AS HostBinding,
               NULLIF(HostKey, N'') AS HostKey,
               NULLIF(COALESCE(TargetHostTemplateKey, HostTemplateKey), N'') AS TargetHostTemplateKey,
               NULLIF(PackageType, N'') AS PackageType,
               NULLIF(TargetName, N'') AS TargetName,
               COALESCE(DesiredState, CONVERT(tinyint, 1)) AS DesiredState,
               COALESCE(SortOrder, 0) AS SortOrder,
               COALESCE(IsEnabled, CONVERT(bit, 1)) AS IsEnabled,
               COALESCE(IsAllowed, CONVERT(bit, 1)) AS IsAllowed
        FROM OPENJSON(@DefinitionJson, N'$.integrity.requiredOmpRows.instanceTemplateAppInstances')
        WITH
        (
            InstanceTemplateKey nvarchar(100) N'$.instanceTemplateKey',
            ModuleInstanceKey nvarchar(100) N'$.moduleInstanceKey',
            AppInstanceKey nvarchar(100) N'$.appInstanceKey',
            AppKey nvarchar(100) N'$.appKey',
            DisplayName nvarchar(200) N'$.displayName',
            Description nvarchar(500) N'$.description',
            RoutePath nvarchar(256) N'$.routePath',
            PublicUrl nvarchar(500) N'$.publicUrl',
            InstallPath nvarchar(500) N'$.installPath',
            InstallationName nvarchar(150) N'$.installationName',
            HostBinding nvarchar(100) N'$.hostBinding',
            HostKey nvarchar(128) N'$.hostKey',
            TargetHostTemplateKey nvarchar(100) N'$.targetHostTemplateKey',
            HostTemplateKey nvarchar(100) N'$.hostTemplateKey',
            PackageType nvarchar(50) N'$.packageType',
            TargetName nvarchar(100) N'$.targetName',
            DesiredState tinyint N'$.desiredState',
            SortOrder int N'$.sortOrder',
            IsEnabled bit N'$.isEnabled',
            IsAllowed bit N'$.isAllowed'
        )
        WHERE AppInstanceKey IS NOT NULL
    ),
    ResolvedTemplateAppInstances AS
    (
        SELECT templateModule.InstanceTemplateModuleInstanceId,
               templateHost.InstanceTemplateHostId,
               targetTemplate.HostTemplateId AS TargetHostTemplateId,
               app.AppId,
               source.AppInstanceKey,
               COALESCE(NULLIF(source.DisplayName, source.AppInstanceKey), app.DisplayName) AS DisplayName,
               source.Description,
               source.RoutePath,
               source.PublicUrl,
               source.InstallPath,
               source.InstallationName,
               source.PackageType,
               source.TargetName,
               source.DesiredState,
               source.SortOrder,
               source.IsEnabled,
               source.IsAllowed,
               CASE
                   WHEN app.AppType IN (N'Portal', N'WebApp') THEN N'web-app'
                   WHEN app.AppType = N'ServiceApp' THEN N'service-app'
                   WHEN app.AppType = N'Worker' THEN N'worker'
                   WHEN app.AppType = N'HostAgent' THEN N'host-agent'
                   WHEN app.AppType = N'WorkerHost' THEN N'worker-host'
                   ELSE NULL
               END AS DefaultPackageType
        FROM RequiredTemplateAppInstances source
        INNER JOIN omp.InstanceTemplates template ON template.TemplateKey = source.InstanceTemplateKey
        INNER JOIN omp.InstanceTemplateModuleInstances templateModule
            ON templateModule.InstanceTemplateId = template.InstanceTemplateId
           AND templateModule.ModuleInstanceKey = source.ModuleInstanceKey
        INNER JOIN omp.Apps app ON app.ModuleId = @ModuleId AND app.AppKey = source.AppKey
        LEFT JOIN omp.InstanceTemplateHosts templateHost
            ON templateHost.InstanceTemplateId = template.InstanceTemplateId
           AND templateHost.HostKey = source.HostKey
        LEFT JOIN omp.HostTemplates targetTemplate ON targetTemplate.TemplateKey = source.TargetHostTemplateKey
        WHERE (source.HostKey IS NULL OR templateHost.InstanceTemplateHostId IS NOT NULL)
          AND (source.TargetHostTemplateKey IS NULL OR targetTemplate.HostTemplateId IS NOT NULL)
          AND (source.HostBinding IS NULL
               OR source.HostBinding = N'host-neutral'
               OR source.HostKey IS NOT NULL
               OR source.TargetHostTemplateKey IS NOT NULL)
    ),
    TemplateAppInstancesWithArtifact AS
    (
        SELECT source.*,
               artifact.ArtifactId
        FROM ResolvedTemplateAppInstances source
        OUTER APPLY
        (
            SELECT TOP (1) item.ArtifactId
            FROM omp.Artifacts item
            WHERE item.AppId = source.AppId
              AND item.IsEnabled = 1
              AND item.PackageType = COALESCE(source.PackageType, source.DefaultPackageType)
              AND (source.TargetName IS NULL OR item.TargetName = source.TargetName)
            ORDER BY
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 4)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 3)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 2)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 1)), 0) DESC,
                item.Version DESC,
                item.ArtifactId DESC
        ) artifact
    )
    MERGE omp.InstanceTemplateAppInstances AS target
    USING TemplateAppInstancesWithArtifact AS source
    ON target.InstanceTemplateModuleInstanceId = source.InstanceTemplateModuleInstanceId
    AND target.AppInstanceKey = source.AppInstanceKey
    WHEN MATCHED THEN
        UPDATE SET InstanceTemplateHostId = source.InstanceTemplateHostId,
                   TargetHostTemplateId = source.TargetHostTemplateId,
                   AppId = source.AppId,
                   DisplayName = source.DisplayName,
                   Description = source.Description,
                   RoutePath = source.RoutePath,
                   PublicUrl = source.PublicUrl,
                   InstallPath = source.InstallPath,
                   InstallationName = source.InstallationName,
                   DesiredArtifactId = COALESCE(source.ArtifactId, target.DesiredArtifactId),
                   DesiredState = source.DesiredState,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   IsAllowed = source.IsAllowed,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(InstanceTemplateModuleInstanceId, InstanceTemplateHostId, TargetHostTemplateId, AppId, AppInstanceKey, DisplayName, Description, RoutePath, PublicUrl, InstallPath, InstallationName, DesiredArtifactId, DesiredState, SortOrder, IsEnabled, IsAllowed)
        VALUES(source.InstanceTemplateModuleInstanceId, source.InstanceTemplateHostId, source.TargetHostTemplateId, source.AppId, source.AppInstanceKey, source.DisplayName, source.Description, source.RoutePath, source.PublicUrl, source.InstallPath, source.InstallationName, source.ArtifactId, source.DesiredState, source.SortOrder, source.IsEnabled, source.IsAllowed);
END;

UPDATE omp.ModuleDefinitionDocuments
SET IsApplied = CASE WHEN ModuleDefinitionDocumentId = @ModuleDefinitionDocumentId THEN 1 ELSE 0 END,
    AppliedUtc = CASE WHEN ModuleDefinitionDocumentId = @ModuleDefinitionDocumentId THEN SYSUTCDATETIME() ELSE AppliedUtc END,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ModuleKey = @ModuleKey;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            await using var cmd = new SqlCommand(sql, conn, tx);
            Add(cmd, "@ModuleDefinitionDocumentId", moduleDefinitionDocumentId);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return new ModuleDefinitionApplyResult
        {
            Applied = true,
            IncompatibleReferences = incompatibleReferences
        };
    }

    public async Task<IReadOnlyList<ModuleDefinitionIntegritySummaryRow>> GetModuleDefinitionIntegritySummariesAsync(
        CancellationToken ct)
    {
        var rows = await GetModuleDefinitionDocumentsAsync(ct);
        var appliedRows = rows
            .Where(static row => row.IsApplied)
            .ToList();

        var summaries = new List<ModuleDefinitionIntegritySummaryRow>(rows.Count);
        foreach (var row in rows)
        {
            if (!row.IsApplied)
            {
                summaries.Add(new ModuleDefinitionIntegritySummaryRow
                {
                    ModuleDefinitionDocumentId = row.ModuleDefinitionDocumentId,
                    ModuleKey = row.ModuleKey,
                    DefinitionVersion = row.DefinitionVersion,
                    IsApplied = false,
                    OverallStatus = "neutral",
                    OverallStatusLabel = "Stored",
                    SummaryLabel = "Stored definition",
                    Messages = ["Stored definitions are kept for review and are not included in active integrity checks."]
                });
                continue;
            }

            var definition = await GetModuleDefinitionDocumentAsync(row.ModuleDefinitionDocumentId, ct)
                ?? throw new InvalidOperationException("Module definition document was not found.");
            var missingMetadata = await GetMissingModuleDefinitionMetadataAsync(definition, ct);
            var sqlChecks = await GetModuleDefinitionSqlChecksAsync(row.ModuleDefinitionDocumentId, ct);
            var dependencyChecks = GetModuleDefinitionDependencyChecks(definition, appliedRows);
            var incompatibleReferences = await GetIncompatibleArtifactReferencesForModuleDefinitionAsync(
                row.ModuleDefinitionDocumentId,
                ct);

            summaries.Add(BuildModuleDefinitionIntegritySummary(
                definition,
                missingMetadata,
                sqlChecks,
                dependencyChecks,
                incompatibleReferences));
        }

        return summaries;
    }

    public async Task<ModuleDefinitionSqlRepairResult> ExecuteAppliedModuleDefinitionSqlRepairsAsync(CancellationToken ct)
    {
        var rows = await GetModuleDefinitionDocumentsAsync(ct);
        var executed = 0;
        var remainingProblems = new List<ModuleDefinitionSqlCheckRow>();

        foreach (var row in rows.Where(static item => item.IsApplied))
        {
            var result = await ExecuteModuleDefinitionSqlRepairsAsync(row.ModuleDefinitionDocumentId, ct);
            executed += result.ExecutedCount;
            remainingProblems.AddRange(result.RemainingProblems);
        }

        return new ModuleDefinitionSqlRepairResult
        {
            ExecutedCount = executed,
            RemainingProblems = remainingProblems
        };
    }

    public async Task<IReadOnlyList<ModuleDefinitionArtifactReferenceRow>> GetIncompatibleArtifactReferencesForModuleDefinitionAsync(
        int moduleDefinitionDocumentId,
        CancellationToken ct)
    {
        var definition = await GetModuleDefinitionDocumentAsync(moduleDefinitionDocumentId, ct)
            ?? throw new InvalidOperationException("Module definition document was not found.");
        var slots = await GetModuleDefinitionCompatibilityAsync(moduleDefinitionDocumentId, ct);
        var references = await GetCurrentArtifactReferencesForModuleAsync(definition.ModuleKey, ct);

        return references
            .Where(reference => !slots.Any(slot => IsCompatibleArtifactReference(reference, slot)))
            .ToList();
    }

    public async Task<IReadOnlyList<ModuleDefinitionSqlCheckRow>> GetModuleDefinitionSqlChecksAsync(
        int moduleDefinitionDocumentId,
        CancellationToken ct)
    {
        var definition = await GetModuleDefinitionDocumentAsync(moduleDefinitionDocumentId, ct)
            ?? throw new InvalidOperationException("Module definition document was not found.");
        if (string.IsNullOrWhiteSpace(definition.DefinitionJson))
        {
            return [];
        }

        var scripts = ReadPortableSqlScripts(definition.DefinitionJson);
        if (scripts.Count == 0)
        {
            return [];
        }

        var requiredObjects = ReadRequiredDatabaseObjects(definition.DefinitionJson);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var missingByScriptKey = await GetMissingRequiredObjectsByScriptKeyAsync(
            conn,
            scripts,
            requiredObjects,
            ct);
        var latestExecutions = await GetLatestModuleDefinitionSqlExecutionsAsync(
            conn,
            moduleDefinitionDocumentId,
            ct);

        var orderedScripts = scripts
            .OrderBy(static item => item.Order)
            .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var validationNeedsRepair = false;
        var validationBlocked = false;
        var rows = new List<ModuleDefinitionSqlCheckRow>(scripts.Count);
        foreach (var script in orderedScripts.Where(IsValidationScript))
        {
            var validationRow = await BuildModuleDefinitionValidationSqlCheckAsync(conn, script, ct);
            validationNeedsRepair = validationNeedsRepair || validationRow.NeedsExecution;
            validationBlocked = validationBlocked || ModuleDefinitionSqlNeedsReview(validationRow.Status);
            rows.Add(validationRow);
        }

        var hasValidationScripts = rows.Count > 0;
        var validationCanDriveRepair = validationNeedsRepair && !validationBlocked;
        var validationReportsHealthy = hasValidationScripts && !validationNeedsRepair && !validationBlocked;
        foreach (var script in orderedScripts.Where(static item => !IsValidationScript(item)))
        {
            var sqlText = ResolvePortableSqlText(script);
            var scriptSha256 = ComputeTextSha256(sqlText ?? string.Empty);
            var suppliedSha256 = NullIfWhiteSpace(script.Sha256);
            var hasInlineSql = !string.IsNullOrWhiteSpace(sqlText);
            var hashMatches = suppliedSha256 is null || string.Equals(suppliedSha256, scriptSha256, StringComparison.OrdinalIgnoreCase);
            var isIdempotent = string.Equals(script.Execution, "idempotent", StringComparison.OrdinalIgnoreCase);
            var safety = hasInlineSql ? ValidateSafeModuleDefinitionSql(sqlText!) : "The script has no embedded SQL content.";
            var isSafe = safety is null;
            var latest = latestExecutions.GetValueOrDefault((script.Key, scriptSha256));
            var missing = missingByScriptKey.GetValueOrDefault(script.Key) ?? [];
            var hasSuccessfulExecution = string.Equals(latest?.Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

            string status;
            string message;
            var needsExecution = false;
            if (!hasInlineSql)
            {
                status = "Not executable";
                message = "The module definition only references an external SQL file. Embed SQL content before Portal can execute repairs.";
            }
            else if (!hashMatches)
            {
                status = "Invalid content hash";
                message = "The embedded SQL content does not match the declared SHA-256 hash.";
            }
            else if (!isIdempotent)
            {
                status = "Blocked";
                message = "Only idempotent module definition SQL scripts can be executed from Portal.";
            }
            else if (!isSafe)
            {
                status = "Blocked";
                message = safety!;
            }
            else if (validationCanDriveRepair)
            {
                status = "Needs repair";
                message = "A module-specific validation script reports that this module needs repair.";
                needsExecution = true;
            }
            else if (missing.Count > 0)
            {
                status = "Needs repair";
                message = "One or more required database objects for this script are missing.";
                needsExecution = true;
            }
            else if (string.Equals(latest?.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                status = "Failed";
                message = latest?.ErrorMessage ?? "The last execution failed.";
                needsExecution = true;
            }
            else if (!validationReportsHealthy && !hasSuccessfulExecution)
            {
                status = "Not recorded";
                message = "No successful execution has been recorded for this script content.";
                needsExecution = true;
            }
            else if (validationReportsHealthy && !hasSuccessfulExecution)
            {
                status = "OK";
                message = "Module-specific validation reports healthy state; a separate repair execution record is not required.";
            }
            else
            {
                status = "OK";
                message = "The script content has a successful execution record and declared objects are present.";
            }

            // Reaching a branch that sets needsExecution means the script has already passed the inline SQL,
            // hash, idempotency, and safety gates above. That also applies to platform-core definitions:
            // the bootstrapper owns first install, while Portal can apply embedded idempotent repair SQL.
            var canExecute = needsExecution;

            rows.Add(new ModuleDefinitionSqlCheckRow
            {
                Key = script.Key,
                Phase = script.Phase,
                Scope = script.Scope,
                Order = script.Order,
                Execution = script.Execution,
                Path = script.Path,
                Source = script.Source,
                ScriptSha256 = scriptSha256,
                IsValidation = false,
                HasInlineSql = hasInlineSql,
                IsSafe = isSafe && isIdempotent && hashMatches,
                HasSuccessfulExecution = hasSuccessfulExecution,
                NeedsExecution = needsExecution,
                CanExecute = canExecute,
                Status = status,
                StatusMessage = message,
                LastCompletedUtc = latest?.CompletedUtc,
                LastExecutionStatus = latest?.Status,
                LastErrorMessage = latest?.ErrorMessage,
                MissingRequiredObjects = missing
            });
        }

        return rows;
    }

    public async Task<ModuleDefinitionSqlRepairResult> ExecuteModuleDefinitionSqlRepairsAsync(
        int moduleDefinitionDocumentId,
        CancellationToken ct)
    {
        var definition = await GetModuleDefinitionDocumentAsync(moduleDefinitionDocumentId, ct)
            ?? throw new InvalidOperationException("Module definition document was not found.");
        if (string.IsNullOrWhiteSpace(definition.DefinitionJson))
        {
            return new ModuleDefinitionSqlRepairResult();
        }

        var scripts = ReadPortableSqlScripts(definition.DefinitionJson)
            .OrderBy(static item => item.Order)
            .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var checks = await GetModuleDefinitionSqlChecksAsync(moduleDefinitionDocumentId, ct);
        var repairableKeys = checks
            .Where(static item => item.NeedsExecution && item.CanExecute)
            .Select(static item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (repairableKeys.Count == 0)
        {
            return new ModuleDefinitionSqlRepairResult
            {
                RemainingProblems = checks.Where(static item => item.NeedsExecution).ToList()
            };
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await AcquireModuleDefinitionSqlExecutionLockAsync(conn, ct);

        var executed = 0;
        foreach (var script in scripts.Where(script => !IsValidationScript(script) && repairableKeys.Contains(script.Key)))
        {
            var originalSqlText = ResolvePortableSqlText(script);
            if (string.IsNullOrWhiteSpace(originalSqlText))
            {
                continue;
            }

            var scriptSha256 = ComputeTextSha256(originalSqlText);
            var sqlText = await PatchBootstrapPortalAdminPrincipalAsync(conn, originalSqlText, ct);
            var safety = ValidateSafeModuleDefinitionSql(sqlText);
            if (safety is not null)
            {
                throw new InvalidOperationException($"Script '{script.Key}' was blocked: {safety}");
            }

            var executionId = await InsertModuleDefinitionSqlExecutionAsync(
                conn,
                moduleDefinitionDocumentId,
                script,
                scriptSha256,
                ct);

            try
            {
                await ExecuteSqlBatchesAsync(conn, sqlText, ct);
                await CompleteModuleDefinitionSqlExecutionAsync(conn, executionId, "Succeeded", null, ct);
                executed++;
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                await CompleteModuleDefinitionSqlExecutionAsync(conn, executionId, "Failed", ex.Message, ct);
                throw new InvalidOperationException($"Module definition SQL script '{script.Key}' failed: {ex.Message}", ex);
            }
        }

        var refreshed = await GetModuleDefinitionSqlChecksAsync(moduleDefinitionDocumentId, ct);
        return new ModuleDefinitionSqlRepairResult
        {
            ExecutedCount = executed,
            RemainingProblems = refreshed.Where(static item => item.NeedsExecution).ToList()
        };
    }

    public async Task<ArtifactConfigurationFileEditData?> GetArtifactConfigurationFileAsync(
        int artifactConfigurationFileId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT ArtifactConfigurationFileId,
       ArtifactId,
       RelativePath,
       FileContent,
       IsEnabled
FROM omp.ArtifactConfigurationFiles
WHERE ArtifactConfigurationFileId = @ArtifactConfigurationFileId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ArtifactConfigurationFileId", artifactConfigurationFileId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ArtifactConfigurationFileEditData
        {
            ArtifactConfigurationFileId = rdr.GetInt32(0),
            ArtifactId = rdr.GetInt32(1),
            RelativePath = rdr.GetString(2),
            FileContent = rdr.GetString(3),
            IsEnabled = rdr.GetBoolean(4)
        };
    }

    public async Task<IReadOnlyList<ArtifactConfigurationFileEditData>> GetArtifactConfigurationFileContentsAsync(
        int artifactId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT ArtifactConfigurationFileId,
       ArtifactId,
       RelativePath,
       FileContent,
       IsEnabled
FROM omp.ArtifactConfigurationFiles
WHERE ArtifactId = @ArtifactId
  AND IsEnabled = 1
ORDER BY RelativePath, ArtifactConfigurationFileId;";

        var rows = new List<ArtifactConfigurationFileEditData>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ArtifactId", artifactId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ArtifactConfigurationFileEditData
            {
                ArtifactConfigurationFileId = rdr.GetInt32(0),
                ArtifactId = rdr.GetInt32(1),
                RelativePath = rdr.GetString(2),
                FileContent = rdr.GetString(3),
                IsEnabled = rdr.GetBoolean(4)
            });
        }

        return rows;
    }

    public async Task<int> SaveArtifactConfigurationFileAsync(
        ArtifactConfigurationFileEditData input,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.ArtifactConfigurationFileId == 0)
        {
            const string insertSql = @"
INSERT INTO omp.ArtifactConfigurationFiles
(
    ArtifactId,
    RelativePath,
    FileContent,
    IsEnabled
)
VALUES
(
    @ArtifactId,
    @RelativePath,
    @FileContent,
    @IsEnabled
);
SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindArtifactConfigurationFile(insert, input, includePrimaryKey: false);
            input.ArtifactConfigurationFileId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
            return input.ArtifactConfigurationFileId;
        }

        const string updateSql = @"
UPDATE omp.ArtifactConfigurationFiles
SET ArtifactId = @ArtifactId,
    RelativePath = @RelativePath,
    FileContent = @FileContent,
    IsEnabled = @IsEnabled,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ArtifactConfigurationFileId = @ArtifactConfigurationFileId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindArtifactConfigurationFile(update, input, includePrimaryKey: true);
        await update.ExecuteNonQueryAsync(ct);
        return input.ArtifactConfigurationFileId;
    }

    public Task DeleteArtifactConfigurationFileAsync(
        int artifactConfigurationFileId,
        CancellationToken ct)
        => DeleteAsync(
            "DELETE FROM omp.ArtifactConfigurationFiles WHERE ArtifactConfigurationFileId = @Id;",
            artifactConfigurationFileId,
            ct);

    public async Task<ArtifactConfigurationFileCopyResult?> CopyConfigurationFilesFromLatestPreviousArtifactAsync(
        int artifactId,
        int appId,
        string packageType,
        string? targetName,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @SourceArtifactId int;
DECLARE @SourceVersion nvarchar(50);
DECLARE @CopiedCount int = 0;

SELECT TOP (1)
       @SourceArtifactId = source.ArtifactId,
       @SourceVersion = source.Version
FROM omp.Artifacts source
WHERE source.ArtifactId <> @ArtifactId
  AND source.AppId = @AppId
  AND source.PackageType = @PackageType
  AND ((source.TargetName = @TargetName) OR (source.TargetName IS NULL AND @TargetName IS NULL))
  AND source.IsEnabled = 1
  AND EXISTS
  (
      SELECT 1
      FROM omp.ArtifactConfigurationFiles sourceFile
      WHERE sourceFile.ArtifactId = source.ArtifactId
  )
ORDER BY source.CreatedUtc DESC, source.ArtifactId DESC;

IF @SourceArtifactId IS NOT NULL
BEGIN
    INSERT INTO omp.ArtifactConfigurationFiles
    (
        ArtifactId,
        RelativePath,
        FileContent,
        IsEnabled
    )
    SELECT @ArtifactId,
           sourceFile.RelativePath,
           sourceFile.FileContent,
           sourceFile.IsEnabled
    FROM omp.ArtifactConfigurationFiles sourceFile
    WHERE sourceFile.ArtifactId = @SourceArtifactId
      AND NOT EXISTS
      (
          SELECT 1
          FROM omp.ArtifactConfigurationFiles targetFile
          WHERE targetFile.ArtifactId = @ArtifactId
            AND targetFile.RelativePath = sourceFile.RelativePath
      );

    SET @CopiedCount = @@ROWCOUNT;
END;

SELECT @SourceArtifactId AS SourceArtifactId,
       @SourceVersion AS SourceVersion,
       @CopiedCount AS CopiedCount;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ArtifactId", artifactId);
        Add(cmd, "@AppId", appId);
        Add(cmd, "@PackageType", packageType);
        Add(cmd, "@TargetName", targetName);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct) || rdr.IsDBNull(0))
        {
            return null;
        }

        return new ArtifactConfigurationFileCopyResult
        {
            SourceArtifactId = rdr.GetInt32(0),
            SourceVersion = rdr.GetString(1),
            CopiedCount = rdr.GetInt32(2)
        };
    }

    public async Task<int> ReplaceArtifactConfigurationFilesAsync(
        int artifactId,
        IReadOnlyList<ArtifactPackageConfigurationFile> configurationFiles,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using (var delete = new SqlCommand(
            "DELETE FROM omp.ArtifactConfigurationFiles WHERE ArtifactId = @ArtifactId;",
            conn,
            tx))
        {
            Add(delete, "@ArtifactId", artifactId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        const string insertSql = @"
INSERT INTO omp.ArtifactConfigurationFiles
(
    ArtifactId,
    RelativePath,
    FileContent,
    IsEnabled
)
VALUES
(
    @ArtifactId,
    @RelativePath,
    @FileContent,
    1
);";

        foreach (var configurationFile in configurationFiles)
        {
            await using var insert = new SqlCommand(insertSql, conn, tx);
            Add(insert, "@ArtifactId", artifactId);
            Add(insert, "@RelativePath", configurationFile.RelativePath);
            Add(insert, "@FileContent", configurationFile.FileContent);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return configurationFiles.Count;
    }

    public async Task<ArtifactDuplicateInfo?> FindArtifactBySha256Async(string sha256, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       ar.ArtifactId,
       ar.AppId,
       a.AppKey,
       ar.Version,
       ar.PackageType,
       ar.TargetName,
       ar.RelativePath,
       ar.Sha256
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
WHERE ar.Sha256 = @Sha256
ORDER BY ar.ArtifactId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@Sha256", sha256);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ArtifactDuplicateInfo
        {
            ArtifactId = rdr.GetInt32(0),
            AppId = rdr.GetInt32(1),
            AppKey = rdr.GetString(2),
            Version = rdr.GetString(3),
            PackageType = rdr.GetString(4),
            TargetName = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            RelativePath = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            Sha256 = rdr.IsDBNull(7) ? null : rdr.GetString(7)
        };
    }

    public async Task<ArtifactDuplicateInfo?> FindArtifactByIdentityAsync(
        int appId,
        string version,
        string packageType,
        string? targetName,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       ar.ArtifactId,
       ar.AppId,
       a.AppKey,
       ar.Version,
       ar.PackageType,
       ar.TargetName,
       ar.RelativePath,
       ar.Sha256
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
WHERE ar.AppId = @AppId
  AND ar.Version = @Version
  AND ar.PackageType = @PackageType
  AND ((ar.TargetName = @TargetName) OR (ar.TargetName IS NULL AND @TargetName IS NULL))
ORDER BY ar.ArtifactId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@AppId", appId);
        Add(cmd, "@Version", version);
        Add(cmd, "@PackageType", packageType);
        Add(cmd, "@TargetName", targetName);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ArtifactDuplicateInfo
        {
            ArtifactId = rdr.GetInt32(0),
            AppId = rdr.GetInt32(1),
            AppKey = rdr.GetString(2),
            Version = rdr.GetString(3),
            PackageType = rdr.GetString(4),
            TargetName = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            RelativePath = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            Sha256 = rdr.IsDBNull(7) ? null : rdr.GetString(7)
        };
    }

    public async Task<ArtifactApplicationResult> ApplyArtifactToMatchingApplicationsAsync(
        int artifactId,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var target = await ReadArtifactAutoApplyTargetAsync(conn, artifactId, ct);
        if (target is null || !IsArtifactPackageCompatibleWithAppType(target.PackageType, target.AppType))
        {
            return new ArtifactApplicationResult();
        }

        var templateAppRowsUpdated = await ApplyArtifactToIntRowsAsync(
            conn,
            artifactId,
            target.Version,
            @"
SELECT tai.InstanceTemplateAppInstanceId,
       currentArtifact.Version
FROM omp.InstanceTemplateAppInstances tai
LEFT JOIN omp.Artifacts currentArtifact
    ON currentArtifact.ArtifactId = tai.DesiredArtifactId
WHERE tai.AppId = @AppId
  AND tai.IsEnabled = 1
  AND ISNULL(tai.DesiredArtifactId, -1) <> @ArtifactId;",
            @"
UPDATE omp.InstanceTemplateAppInstances
SET DesiredArtifactId = @ArtifactId,
    UpdatedUtc = SYSUTCDATETIME()
WHERE InstanceTemplateAppInstanceId = @RowId;",
            target.AppId,
            ct);

        var appInstanceRowsUpdated = await ApplyArtifactToGuidRowsAsync(
            conn,
            artifactId,
            target.Version,
            @"
SELECT ai.AppInstanceId,
       currentArtifact.Version
FROM omp.AppInstances ai
LEFT JOIN omp.Artifacts currentArtifact
    ON currentArtifact.ArtifactId = ai.ArtifactId
WHERE ai.AppId = @AppId
  AND ai.IsEnabled = 1
  AND ISNULL(ai.ArtifactId, -1) <> @ArtifactId;",
            @"
UPDATE omp.AppInstances
SET ArtifactId = @ArtifactId,
    UpdatedUtc = SYSUTCDATETIME()
WHERE AppInstanceId = @RowId;",
            target.AppId,
            ct);

        var workerInstanceRowsUpdated = await ApplyArtifactToGuidRowsAsync(
            conn,
            artifactId,
            target.Version,
            @"
SELECT wi.WorkerInstanceId,
       currentArtifact.Version
FROM omp.WorkerInstances wi
INNER JOIN omp.AppInstances ai
    ON ai.AppInstanceId = wi.AppInstanceId
LEFT JOIN omp.Artifacts currentArtifact
    ON currentArtifact.ArtifactId = wi.ArtifactId
WHERE ai.AppId = @AppId
  AND wi.IsEnabled = 1
  AND wi.ArtifactId IS NOT NULL
  AND wi.ArtifactId <> @ArtifactId;",
            @"
UPDATE omp.WorkerInstances
SET ArtifactId = @ArtifactId,
    UpdatedUtc = SYSUTCDATETIME()
WHERE WorkerInstanceId = @RowId;",
            target.AppId,
            ct);

        var hostAgentDesiredRowsUpdated = target.PackageType.Equals("host-agent", StringComparison.OrdinalIgnoreCase)
            ? await ApplyHostAgentArtifactToForwardRowsAsync(conn, artifactId, target.Version, ct)
            : 0;

        return new ArtifactApplicationResult
        {
            TemplateAppRowsUpdated = templateAppRowsUpdated,
            AppInstanceRowsUpdated = appInstanceRowsUpdated,
            WorkerInstanceRowsUpdated = workerInstanceRowsUpdated,
            HostAgentDesiredRowsUpdated = hostAgentDesiredRowsUpdated
        };
    }

    private async Task<ArtifactAutoApplyTarget?> ReadArtifactAutoApplyTargetAsync(
        SqlConnection conn,
        int artifactId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT ArtifactId,
       AppId,
       Version,
       PackageType,
       app.AppType
FROM omp.Artifacts artifact
INNER JOIN omp.Apps app
    ON app.AppId = artifact.AppId
WHERE artifact.ArtifactId = @ArtifactId
  AND artifact.IsEnabled = 1
  AND app.IsEnabled = 1;";

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ArtifactId", artifactId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ArtifactAutoApplyTarget(
            rdr.GetInt32(0),
            rdr.GetInt32(1),
            rdr.GetString(2),
            rdr.GetString(3),
            rdr.GetString(4));
    }

    private static async Task<int> ApplyArtifactToIntRowsAsync(
        SqlConnection conn,
        int artifactId,
        string targetVersion,
        string selectSql,
        string updateSql,
        int appId,
        CancellationToken ct)
    {
        var rowIds = new List<int>();
        await using (var select = new SqlCommand(selectSql, conn))
        {
            select.Parameters.AddWithValue("@ArtifactId", artifactId);
            select.Parameters.AddWithValue("@AppId", appId);
            await using var rdr = await select.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var currentVersion = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                if (ShouldApplyImportedArtifact(targetVersion, currentVersion))
                {
                    rowIds.Add(rdr.GetInt32(0));
                }
            }
        }

        var updated = 0;
        foreach (var rowId in rowIds)
        {
            await using var update = new SqlCommand(updateSql, conn);
            update.Parameters.AddWithValue("@ArtifactId", artifactId);
            update.Parameters.AddWithValue("@RowId", rowId);
            updated += await update.ExecuteNonQueryAsync(ct);
        }

        return updated;
    }

    private static async Task<int> ApplyArtifactToGuidRowsAsync(
        SqlConnection conn,
        int artifactId,
        string targetVersion,
        string selectSql,
        string updateSql,
        int appId,
        CancellationToken ct)
    {
        var rowIds = new List<Guid>();
        await using (var select = new SqlCommand(selectSql, conn))
        {
            select.Parameters.AddWithValue("@ArtifactId", artifactId);
            select.Parameters.AddWithValue("@AppId", appId);
            await using var rdr = await select.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var currentVersion = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                if (ShouldApplyImportedArtifact(targetVersion, currentVersion))
                {
                    rowIds.Add(rdr.GetGuid(0));
                }
            }
        }

        var updated = 0;
        foreach (var rowId in rowIds)
        {
            await using var update = new SqlCommand(updateSql, conn);
            update.Parameters.AddWithValue("@ArtifactId", artifactId);
            update.Parameters.AddWithValue("@RowId", rowId);
            updated += await update.ExecuteNonQueryAsync(ct);
        }

        return updated;
    }

    private static bool IsArtifactPackageCompatibleWithAppType(string packageType, string appType)
        => (packageType.Equals("web-app", StringComparison.OrdinalIgnoreCase)
                && (appType.Equals("Portal", StringComparison.OrdinalIgnoreCase)
                    || appType.Equals("WebApp", StringComparison.OrdinalIgnoreCase)))
            || (packageType.Equals("service-app", StringComparison.OrdinalIgnoreCase)
                && appType.Equals("ServiceApp", StringComparison.OrdinalIgnoreCase))
            || (packageType.Equals("worker", StringComparison.OrdinalIgnoreCase)
                && appType.Equals("Worker", StringComparison.OrdinalIgnoreCase))
            || (packageType.Equals("host-agent", StringComparison.OrdinalIgnoreCase)
                && appType.Equals("HostAgent", StringComparison.OrdinalIgnoreCase))
            || (packageType.Equals("worker-host", StringComparison.OrdinalIgnoreCase)
                && appType.Equals("WorkerHost", StringComparison.OrdinalIgnoreCase));

    private static async Task<int> ApplyHostAgentArtifactToForwardRowsAsync(
        SqlConnection conn,
        int artifactId,
        string targetVersion,
        CancellationToken ct)
    {
        const string selectSql = @"
SELECT h.HostId,
       desired.ArtifactId,
       desired.IsEnabled,
       currentArtifact.Version
FROM omp.Hosts h
LEFT JOIN omp.HostAgentDesiredStates desired
    ON desired.HostId = h.HostId
LEFT JOIN omp.Artifacts currentArtifact
    ON currentArtifact.ArtifactId = desired.ArtifactId
WHERE h.IsEnabled = 1
  AND
  (
      desired.HostId IS NOT NULL
      OR EXISTS
      (
          SELECT 1
          FROM omp.HostAgentRuntimeStates runtimeState
          WHERE runtimeState.HostId = h.HostId
            AND runtimeState.IsActive = 1
      )
  );";

        var rows = new List<(Guid HostId, int? CurrentArtifactId, bool IsEnabled, string? CurrentVersion)>();
        await using (var select = new SqlCommand(selectSql, conn))
        {
            await using var rdr = await select.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                rows.Add((
                    rdr.GetGuid(0),
                    rdr.IsDBNull(1) ? null : rdr.GetInt32(1),
                    !rdr.IsDBNull(2) && rdr.GetBoolean(2),
                    rdr.IsDBNull(3) ? null : rdr.GetString(3)));
            }
        }

        const string updateSql = @"
UPDATE omp.HostAgentDesiredStates
SET ArtifactId = @ArtifactId,
    IsEnabled = 1,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostId = @HostId;";

        const string insertSql = @"
INSERT omp.HostAgentDesiredStates(HostId, ArtifactId, ServiceNamePrefix, InstallRoot, IsEnabled)
VALUES(@HostId, @ArtifactId, NULL, NULL, 1);";

        var updated = 0;
        foreach (var row in rows)
        {
            if (row.CurrentArtifactId.HasValue
                && row.CurrentArtifactId.Value != artifactId
                && !ShouldApplyImportedArtifact(targetVersion, row.CurrentVersion))
            {
                continue;
            }

            if (row.CurrentArtifactId.HasValue)
            {
                if (row.CurrentArtifactId.Value == artifactId && row.IsEnabled)
                {
                    continue;
                }

                await using var update = new SqlCommand(updateSql, conn);
                update.Parameters.AddWithValue("@ArtifactId", artifactId);
                update.Parameters.AddWithValue("@HostId", row.HostId);
                updated += await update.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var insert = new SqlCommand(insertSql, conn);
                insert.Parameters.AddWithValue("@ArtifactId", artifactId);
                insert.Parameters.AddWithValue("@HostId", row.HostId);
                updated += await insert.ExecuteNonQueryAsync(ct);
            }
        }

        return updated;
    }

    private static bool ShouldApplyImportedArtifact(string targetVersion, string? currentVersion)
        => string.IsNullOrWhiteSpace(currentVersion)
            || CompareArtifactVersions(targetVersion, currentVersion) > 0;

    public async Task<int> RequestHostAgentUpgradeAsync(
        Guid hostId,
        int artifactId,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @Changes table(ActionName nvarchar(10) NOT NULL);

MERGE omp.HostAgentDesiredStates AS target
USING
(
    SELECT h.HostId,
           ar.ArtifactId
    FROM omp.Hosts h
    CROSS JOIN omp.Artifacts ar
    INNER JOIN omp.Apps a ON a.AppId = ar.AppId
    INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
    WHERE h.HostId = @HostId
      AND h.IsEnabled = 1
      AND ar.ArtifactId = @ArtifactId
      AND ar.IsEnabled = 1
      AND m.ModuleKey = N'omp_core'
      AND a.AppKey = N'omp_hostagent'
      AND ar.PackageType = N'host-agent'
      AND ar.TargetName = N'omp-hostagent'
) AS source
ON target.HostId = source.HostId
WHEN MATCHED AND
(
       target.ArtifactId <> source.ArtifactId
    OR target.IsEnabled = 0
)
    THEN UPDATE SET
        ArtifactId = source.ArtifactId,
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(HostId, ArtifactId, ServiceNamePrefix, InstallRoot, IsEnabled)
    VALUES(source.HostId, source.ArtifactId, NULL, NULL, 1)
OUTPUT $action INTO @Changes;

SELECT COUNT(1)
FROM @Changes;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@HostId", hostId);
        Add(cmd, "@ArtifactId", artifactId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
    }

    // -------------------------------------------------------------------------
    // App-instance editing
    // -------------------------------------------------------------------------

    public async Task<AppInstanceEditData?> GetAppInstanceAsync(Guid appInstanceId, CancellationToken ct)
    {
        const string sql = @"
SELECT AppInstanceId,
       ModuleInstanceId,
       HostId,
       AppId,
       AppInstanceKey,
       DisplayName,
       Description,
       RoutePath,
       PublicUrl,
       InstallPath,
       InstallationName,
       ArtifactId,
       ConfigId,
       ExpectedLogin,
       ExpectedClientHostName,
       ExpectedClientIp,
       IsEnabled,
       IsAllowed,
       DesiredState,
       SortOrder
FROM omp.AppInstances
WHERE AppInstanceId = @AppInstanceId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AppInstanceId", appInstanceId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new AppInstanceEditData
        {
            AppInstanceId = rdr.GetGuid(0),
            ModuleInstanceId = rdr.GetGuid(1),
            HostId = rdr.IsDBNull(2) ? null : rdr.GetGuid(2),
            AppId = rdr.GetInt32(3),
            AppInstanceKey = rdr.GetString(4),
            DisplayName = rdr.GetString(5),
            Description = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            RoutePath = rdr.IsDBNull(7) ? null : rdr.GetString(7),
            PublicUrl = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            InstallPath = rdr.IsDBNull(9) ? null : rdr.GetString(9),
            InstallationName = rdr.IsDBNull(10) ? null : rdr.GetString(10),
            ArtifactId = rdr.IsDBNull(11) ? null : rdr.GetInt32(11),
            ConfigId = rdr.IsDBNull(12) ? null : rdr.GetInt32(12),
            ExpectedLogin = rdr.IsDBNull(13) ? null : rdr.GetString(13),
            ExpectedClientHostName = rdr.IsDBNull(14) ? null : rdr.GetString(14),
            ExpectedClientIp = rdr.IsDBNull(15) ? null : rdr.GetString(15),
            IsEnabled = rdr.GetBoolean(16),
            IsAllowed = rdr.GetBoolean(17),
            DesiredState = rdr.GetByte(18),
            SortOrder = rdr.GetInt32(19)
        };
    }

    public async Task<Guid> SaveAppInstanceAsync(AppInstanceEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.AppInstanceId == Guid.Empty)
        {
            input.AppInstanceId = Guid.NewGuid();

            const string insertSql = @"
INSERT INTO omp.AppInstances
(
    AppInstanceId,
    ModuleInstanceId,
    HostId,
    AppId,
    AppInstanceKey,
    DisplayName,
    Description,
    RoutePath,
    PublicUrl,
    InstallPath,
    InstallationName,
    ArtifactId,
    ConfigId,
    ExpectedLogin,
    ExpectedClientHostName,
    ExpectedClientIp,
    IsEnabled,
    IsAllowed,
    DesiredState,
    SortOrder
)
VALUES
(
    @AppInstanceId,
    @ModuleInstanceId,
    @HostId,
    @AppId,
    @AppInstanceKey,
    @DisplayName,
    @Description,
    @RoutePath,
    @PublicUrl,
    @InstallPath,
    @InstallationName,
    @ArtifactId,
    @ConfigId,
    @ExpectedLogin,
    @ExpectedClientHostName,
    @ExpectedClientIp,
    @IsEnabled,
    @IsAllowed,
    @DesiredState,
    @SortOrder
);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindAppInstance(insert, input);
            await insert.ExecuteNonQueryAsync(ct);
            return input.AppInstanceId;
        }

        const string updateSql = @"
UPDATE omp.AppInstances
SET ModuleInstanceId = @ModuleInstanceId,
    HostId = @HostId,
    AppId = @AppId,
    AppInstanceKey = @AppInstanceKey,
    DisplayName = @DisplayName,
    Description = @Description,
    RoutePath = @RoutePath,
    PublicUrl = @PublicUrl,
    InstallPath = @InstallPath,
    InstallationName = @InstallationName,
    ArtifactId = @ArtifactId,
    ConfigId = @ConfigId,
    ExpectedLogin = @ExpectedLogin,
    ExpectedClientHostName = @ExpectedClientHostName,
    ExpectedClientIp = @ExpectedClientIp,
    IsEnabled = @IsEnabled,
    IsAllowed = @IsAllowed,
    DesiredState = @DesiredState,
    SortOrder = @SortOrder,
    UpdatedUtc = SYSUTCDATETIME()
WHERE AppInstanceId = @AppInstanceId;";

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var templateAppInstanceId = await GetTemplateAppInstanceIdAsync(conn, tx, input.AppInstanceId, ct);
        if (templateAppInstanceId.HasValue)
        {
            throw new InvalidOperationException(
                "This app instance is managed by an instance template. Change the desired template app instead and let HostAgent update the runtime row.");
        }

        await using var update = new SqlCommand(updateSql, conn);
        update.Transaction = tx;
        BindAppInstance(update, input);
        var affected = await update.ExecuteNonQueryAsync(ct);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"App instance '{input.AppInstanceId}' no longer exists and could not be updated.");
        }

        await tx.CommitAsync(ct);
        return input.AppInstanceId;
    }

    public Task DeleteAppInstanceAsync(Guid appInstanceId, CancellationToken ct)
        => DeleteAppInstanceCoreAsync(appInstanceId, blockTemplateManagedRows: true, ct);

    public Task DeleteRuntimeAppInstanceRowAsync(Guid appInstanceId, CancellationToken ct)
        => DeleteAppInstanceCoreAsync(appInstanceId, blockTemplateManagedRows: false, ct);

    private async Task DeleteAppInstanceCoreAsync(
        Guid appInstanceId,
        bool blockTemplateManagedRows,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        var hasWorkerInstances = await TableExistsAsync(conn, "omp.WorkerInstances", ct);
        var hasWorkerInstanceRuntimeStates = await TableExistsAsync(conn, "omp.WorkerInstanceRuntimeStates", ct);
        var hasAppInstanceRuntimeStates = await TableExistsAsync(conn, "omp.AppInstanceRuntimeStates", ct);
        var hasHostAppDeploymentStates = await TableExistsAsync(conn, "omp.HostAppDeploymentStates", ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        var templateAppInstanceId = await GetTemplateAppInstanceIdAsync(conn, tx, appInstanceId, ct);
        if (blockTemplateManagedRows && templateAppInstanceId.HasValue)
        {
            throw new InvalidOperationException(
                "This app instance is managed by an instance template. Remove or disable the desired template app instead and let HostAgent materialize the change.");
        }

        if (hasWorkerInstanceRuntimeStates)
        {
            await ExecuteNonQueryAsync(
                conn,
                tx,
                "DELETE FROM omp.WorkerInstanceRuntimeStates WHERE AppInstanceId = @Id;",
                appInstanceId,
                ct);
        }

        if (hasWorkerInstances)
        {
            await ExecuteNonQueryAsync(
                conn,
                tx,
                "DELETE FROM omp.WorkerInstances WHERE AppInstanceId = @Id;",
                appInstanceId,
                ct);
        }

        if (hasAppInstanceRuntimeStates)
        {
            await ExecuteNonQueryAsync(
                conn,
                tx,
                "DELETE FROM omp.AppInstanceRuntimeStates WHERE AppInstanceId = @Id;",
                appInstanceId,
                ct);
        }

        if (hasHostAppDeploymentStates)
        {
            await ExecuteNonQueryAsync(
                conn,
                tx,
                "DELETE FROM omp.HostAppDeploymentStates WHERE AppInstanceId = @Id;",
                appInstanceId,
                ct);
        }

        await ExecuteNonQueryAsync(
            conn,
            tx,
            "DELETE FROM omp.AppInstances WHERE AppInstanceId = @Id;",
            appInstanceId,
            ct);

        await tx.CommitAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Context lookups used by validation on edit pages
    // -------------------------------------------------------------------------

    public async Task<ModuleInstanceContext?> GetModuleInstanceContextAsync(Guid moduleInstanceId, CancellationToken ct)
    {
        const string sql = @"
SELECT mi.ModuleInstanceId,
       mi.InstanceId,
       mi.ModuleId,
       i.InstanceKey,
       mi.ModuleInstanceKey
FROM omp.ModuleInstances mi
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
WHERE mi.ModuleInstanceId = @ModuleInstanceId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ModuleInstanceId", moduleInstanceId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ModuleInstanceContext
        {
            ModuleInstanceId = rdr.GetGuid(0),
            InstanceId = rdr.GetGuid(1),
            ModuleId = rdr.GetInt32(2),
            InstanceKey = rdr.GetString(3),
            ModuleInstanceKey = rdr.GetString(4)
        };
    }

    public async Task<HostContext?> GetHostContextAsync(Guid hostId, CancellationToken ct)
    {
        const string sql = @"
SELECT HostId,
       InstanceId,
       HostKey
FROM omp.Hosts
WHERE HostId = @HostId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@HostId", hostId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new HostContext
        {
            HostId = rdr.GetGuid(0),
            InstanceId = rdr.GetGuid(1),
            HostKey = rdr.GetString(2)
        };
    }

    public async Task<AppDefinitionContext?> GetAppContextAsync(int appId, CancellationToken ct)
    {
        const string sql = @"
SELECT AppId,
       ModuleId,
       AppKey,
       AppType,
       AllowMultipleActiveInstances
FROM omp.Apps
WHERE AppId = @AppId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AppId", appId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new AppDefinitionContext
        {
            AppId = rdr.GetInt32(0),
            ModuleId = rdr.GetInt32(1),
            AppKey = rdr.GetString(2),
            AppType = rdr.GetString(3),
            AllowMultipleActiveInstances = rdr.GetBoolean(4)
        };
    }

    public async Task<InstanceTemplateModuleContext?> GetInstanceTemplateModuleContextAsync(
        int instanceTemplateModuleInstanceId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT InstanceTemplateModuleInstanceId,
       InstanceTemplateId,
       ModuleId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateModuleInstanceId = @InstanceTemplateModuleInstanceId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@InstanceTemplateModuleInstanceId", instanceTemplateModuleInstanceId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new InstanceTemplateModuleContext
        {
            InstanceTemplateModuleInstanceId = rdr.GetInt32(0),
            InstanceTemplateId = rdr.GetInt32(1),
            ModuleId = rdr.GetInt32(2)
        };
    }

    public async Task<InstanceTemplateHostContext?> GetInstanceTemplateHostContextAsync(
        int instanceTemplateHostId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT InstanceTemplateHostId,
       InstanceTemplateId
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateHostId = @InstanceTemplateHostId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@InstanceTemplateHostId", instanceTemplateHostId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new InstanceTemplateHostContext
        {
            InstanceTemplateHostId = rdr.GetInt32(0),
            InstanceTemplateId = rdr.GetInt32(1)
        };
    }

    public async Task<bool> ArtifactBelongsToAppAsync(int artifactId, int appId, CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1)
FROM omp.Artifacts
WHERE ArtifactId = @ArtifactId
  AND AppId = @AppId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ArtifactId", artifactId);
        Add(cmd, "@AppId", appId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    public async Task<bool> ActiveAppInstancePlacementConflictExistsAsync(
        Guid appInstanceId,
        Guid moduleInstanceId,
        Guid? hostId,
        int appId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1)
FROM omp.AppInstances ai
INNER JOIN omp.Apps a
    ON a.AppId = ai.AppId
   AND a.AppType IN (N'Portal', N'WebApp')
   AND a.AllowMultipleActiveInstances = 0
WHERE ai.AppInstanceId <> @AppInstanceId
  AND ai.ModuleInstanceId = @ModuleInstanceId
  AND ai.AppId = @AppId
  AND ai.IsEnabled = 1
  AND ai.IsAllowed = 1
  AND ai.DesiredState = 1
  AND
  (
      (@HostId IS NULL)
      OR (ai.HostId IS NULL AND ai.TargetHostTemplateId IS NULL)
      OR ai.HostId = @HostId
      OR
      (
          ai.TargetHostTemplateId IS NOT NULL
          AND EXISTS
          (
              SELECT 1
              FROM omp.HostDeploymentAssignments hda
              WHERE hda.HostId = @HostId
                AND hda.HostTemplateId = ai.TargetHostTemplateId
                AND hda.IsActive = 1
          )
      )
  );";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@AppInstanceId", appInstanceId);
        Add(cmd, "@ModuleInstanceId", moduleInstanceId);
        cmd.Parameters.Add("@HostId", SqlDbType.UniqueIdentifier).Value =
            hostId.HasValue ? hostId.Value : DBNull.Value;
        Add(cmd, "@AppId", appId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    public async Task<bool> ActiveTemplateAppPlacementConflictExistsAsync(
        int instanceTemplateAppInstanceId,
        int instanceTemplateModuleInstanceId,
        int? instanceTemplateHostId,
        int? targetHostTemplateId,
        int appId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1)
FROM omp.InstanceTemplateAppInstances tai
INNER JOIN omp.Apps a
    ON a.AppId = tai.AppId
   AND a.AppType IN (N'Portal', N'WebApp')
   AND a.AllowMultipleActiveInstances = 0
WHERE tai.InstanceTemplateAppInstanceId <> @InstanceTemplateAppInstanceId
  AND tai.InstanceTemplateModuleInstanceId = @InstanceTemplateModuleInstanceId
  AND tai.AppId = @AppId
  AND tai.IsEnabled = 1
  AND tai.IsAllowed = 1
  AND tai.DesiredState = 1
  AND
  (
      (@InstanceTemplateHostId IS NULL AND @TargetHostTemplateId IS NULL)
      OR (tai.InstanceTemplateHostId IS NULL AND tai.TargetHostTemplateId IS NULL)
      OR (tai.InstanceTemplateHostId IS NOT NULL AND tai.InstanceTemplateHostId = @InstanceTemplateHostId)
      OR (tai.TargetHostTemplateId IS NOT NULL AND tai.TargetHostTemplateId = @TargetHostTemplateId)
      OR
      (
          @InstanceTemplateHostId IS NOT NULL
          AND tai.TargetHostTemplateId IS NOT NULL
          AND EXISTS
          (
              SELECT 1
              FROM omp.InstanceTemplateHosts ith
              WHERE ith.InstanceTemplateHostId = @InstanceTemplateHostId
                AND ith.HostTemplateId = tai.TargetHostTemplateId
          )
      )
      OR
      (
          @TargetHostTemplateId IS NOT NULL
          AND tai.InstanceTemplateHostId IS NOT NULL
          AND EXISTS
          (
              SELECT 1
              FROM omp.InstanceTemplateHosts ith
              WHERE ith.InstanceTemplateHostId = tai.InstanceTemplateHostId
                AND ith.HostTemplateId = @TargetHostTemplateId
          )
      )
  );";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@InstanceTemplateAppInstanceId", instanceTemplateAppInstanceId);
        Add(cmd, "@InstanceTemplateModuleInstanceId", instanceTemplateModuleInstanceId);
        cmd.Parameters.Add("@InstanceTemplateHostId", SqlDbType.Int).Value =
            instanceTemplateHostId.HasValue ? instanceTemplateHostId.Value : DBNull.Value;
        cmd.Parameters.Add("@TargetHostTemplateId", SqlDbType.Int).Value =
            targetHostTemplateId.HasValue ? targetHostTemplateId.Value : DBNull.Value;
        Add(cmd, "@AppId", appId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<int?> GetTemplateAppInstanceIdAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid appInstanceId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) tai.InstanceTemplateAppInstanceId
FROM omp.AppInstances ai
INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
INNER JOIN omp.InstanceTemplateModuleInstances tmi
    ON tmi.InstanceTemplateId = i.InstanceTemplateId
   AND tmi.ModuleId = mi.ModuleId
   AND tmi.ModuleInstanceKey = mi.ModuleInstanceKey
INNER JOIN omp.InstanceTemplateAppInstances tai
    ON tai.InstanceTemplateModuleInstanceId = tmi.InstanceTemplateModuleInstanceId
   AND tai.AppInstanceKey = ai.AppInstanceKey
WHERE ai.AppInstanceId = @AppInstanceId
ORDER BY tai.InstanceTemplateAppInstanceId;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@AppInstanceId", appInstanceId);

        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static async Task<byte?> GetWorkerRuntimeObservedStateAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid appInstanceId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       CAST(ISNULL(rs.ObservedState, CAST(0 AS tinyint)) AS tinyint)
FROM omp.AppInstances ai
INNER JOIN omp.AppWorkerDefinitions awd ON awd.AppId = ai.AppId
LEFT JOIN omp.AppInstanceRuntimeStates rs ON rs.AppInstanceId = ai.AppInstanceId
WHERE ai.AppInstanceId = @AppInstanceId;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@AppInstanceId", appInstanceId);

        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToByte(value);
    }

    // -------------------------------------------------------------------------
    // Parameter binders and low-level helpers
    // -------------------------------------------------------------------------

    private static void BindInstance(SqlCommand cmd, InstanceEditData input)
    {
        Add(cmd, "@InstanceId", input.InstanceId);
        Add(cmd, "@InstanceKey", input.InstanceKey);
        Add(cmd, "@DisplayName", input.DisplayName);
        Add(cmd, "@Description", input.Description);
        Add(cmd, "@InstanceTemplateId", input.InstanceTemplateId);
        Add(cmd, "@IsEnabled", input.IsEnabled);
    }

    private static void BindHost(SqlCommand cmd, HostEditData input, bool includeBaseUrl)
    {
        Add(cmd, "@HostId", input.HostId);
        Add(cmd, "@InstanceId", input.InstanceId);
        Add(cmd, "@HostKey", input.HostKey);
        Add(cmd, "@DisplayName", input.DisplayName);
        if (includeBaseUrl)
        {
            Add(cmd, "@BaseUrl", input.BaseUrl);
        }
        Add(cmd, "@Environment", input.Environment);
        Add(cmd, "@OsFamily", input.OsFamily);
        Add(cmd, "@OsVersion", input.OsVersion);
        Add(cmd, "@Architecture", input.Architecture);
        Add(cmd, "@IsEnabled", input.IsEnabled);
    }

    private static void BindModule(SqlCommand cmd, ModuleEditData input, bool includePrimaryKey)
    {
        if (includePrimaryKey)
        {
            Add(cmd, "@ModuleId", input.ModuleId);
        }

        Add(cmd, "@ModuleKey", input.ModuleKey);
        Add(cmd, "@DisplayName", input.DisplayName);
        Add(cmd, "@ModuleType", input.ModuleType);
        Add(cmd, "@SchemaName", input.SchemaName);
        Add(cmd, "@Description", input.Description);
        Add(cmd, "@IsEnabled", input.IsEnabled);
        Add(cmd, "@SortOrder", input.SortOrder);
    }

    private static void BindModuleInstance(SqlCommand cmd, ModuleInstanceEditData input)
    {
        Add(cmd, "@ModuleInstanceId", input.ModuleInstanceId);
        Add(cmd, "@InstanceId", input.InstanceId);
        Add(cmd, "@ModuleId", input.ModuleId);
        Add(cmd, "@ModuleInstanceKey", input.ModuleInstanceKey);
        Add(cmd, "@DisplayName", input.DisplayName);
        Add(cmd, "@Description", input.Description);
        Add(cmd, "@IsEnabled", input.IsEnabled);
        Add(cmd, "@SortOrder", input.SortOrder);
    }

    private static void BindApp(SqlCommand cmd, AppEditData input, bool includePrimaryKey)
    {
        if (includePrimaryKey)
        {
            Add(cmd, "@AppId", input.AppId);
        }

        Add(cmd, "@ModuleId", input.ModuleId);
        Add(cmd, "@AppKey", input.AppKey);
        Add(cmd, "@DisplayName", input.DisplayName);
        Add(cmd, "@AppType", input.AppType);
        Add(cmd, "@AllowMultipleActiveInstances", input.AllowMultipleActiveInstances);
        Add(cmd, "@Description", input.Description);
        Add(cmd, "@IsEnabled", input.IsEnabled);
        Add(cmd, "@SortOrder", input.SortOrder);
    }

    private static async Task<int> InsertModuleDefinitionDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        ModuleDefinitionDocumentEditData input,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.ModuleDefinitionDocuments
(
    ModuleKey,
    DefinitionVersion,
    FormatVersion,
    DefinitionJson,
    DefinitionSha256,
    SourceName,
    IsApplied
)
VALUES
(
    @ModuleKey,
    @DefinitionVersion,
    @FormatVersion,
    @DefinitionJson,
    @DefinitionSha256,
    @SourceName,
    0
);

SELECT CAST(SCOPE_IDENTITY() AS int);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        BindModuleDefinitionDocument(cmd, input);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task UpdateModuleDefinitionDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int documentId,
        ModuleDefinitionDocumentEditData input,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.ModuleDefinitionDocuments
SET FormatVersion = @FormatVersion,
    DefinitionJson = @DefinitionJson,
    DefinitionSha256 = @DefinitionSha256,
    SourceName = @SourceName,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ModuleDefinitionDocumentId = @ModuleDefinitionDocumentId;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@ModuleDefinitionDocumentId", documentId);
        BindModuleDefinitionDocument(cmd, input);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReplaceModuleDefinitionCompatibilityAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int documentId,
        IReadOnlyList<ModuleDefinitionCompatibilityEditData> entries,
        CancellationToken ct)
    {
        const string deleteSql = @"
DELETE FROM omp.ModuleDefinitionArtifactCompatibility
WHERE ModuleDefinitionDocumentId = @ModuleDefinitionDocumentId;";

        await using (var delete = new SqlCommand(deleteSql, conn, tx))
        {
            Add(delete, "@ModuleDefinitionDocumentId", documentId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        const string insertSql = @"
INSERT INTO omp.ModuleDefinitionArtifactCompatibility
(
    ModuleDefinitionDocumentId,
    AppKey,
    PackageType,
    TargetName,
    RelativePathTemplate,
    MinArtifactVersion,
    MaxArtifactVersion
)
VALUES
(
    @ModuleDefinitionDocumentId,
    @AppKey,
    @PackageType,
    @TargetName,
    @RelativePathTemplate,
    @MinArtifactVersion,
    @MaxArtifactVersion
);";

        foreach (var entry in entries)
        {
            await using var insert = new SqlCommand(insertSql, conn, tx);
            Add(insert, "@ModuleDefinitionDocumentId", documentId);
            Add(insert, "@AppKey", entry.AppKey);
            Add(insert, "@PackageType", entry.PackageType);
            Add(insert, "@TargetName", entry.TargetName);
            Add(insert, "@RelativePathTemplate", entry.RelativePathTemplate);
            Add(insert, "@MinArtifactVersion", entry.MinArtifactVersion);
            Add(insert, "@MaxArtifactVersion", entry.MaxArtifactVersion);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static void BindModuleDefinitionDocument(
        SqlCommand cmd,
        ModuleDefinitionDocumentEditData input)
    {
        Add(cmd, "@ModuleKey", input.ModuleKey);
        Add(cmd, "@DefinitionVersion", input.DefinitionVersion);
        Add(cmd, "@FormatVersion", input.FormatVersion);
        Add(cmd, "@DefinitionJson", input.DefinitionJson);
        Add(cmd, "@DefinitionSha256", input.DefinitionSha256);
        Add(cmd, "@SourceName", input.SourceName);
    }

    private static bool IsCompatibleArtifactReference(
        ModuleDefinitionArtifactReferenceRow reference,
        ModuleDefinitionCompatibilityRow slot)
        => string.Equals(reference.AppKey, slot.AppKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(reference.PackageType, slot.PackageType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(reference.TargetName ?? string.Empty, slot.TargetName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && IsVersionInRange(reference.Version, slot.MinArtifactVersion, slot.MaxArtifactVersion);

    private async Task<IReadOnlyList<string>> GetMissingModuleDefinitionMetadataAsync(
        ModuleDefinitionDocumentRow definition,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(definition.DefinitionJson))
        {
            return [];
        }

        var expected = ReadExpectedModuleMetadata(definition.DefinitionJson, definition.ModuleKey);
        if (!expected.ExpectsModule && expected.AppKeys.Count == 0)
        {
            return [];
        }

        var missing = new List<string>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        int? moduleId = null;
        await using (var moduleCmd = new SqlCommand("SELECT ModuleId FROM omp.Modules WHERE ModuleKey = @ModuleKey;", conn))
        {
            Add(moduleCmd, "@ModuleKey", expected.ModuleKey);
            var result = await moduleCmd.ExecuteScalarAsync(ct);
            if (result is not null && result is not DBNull)
            {
                moduleId = Convert.ToInt32(result);
            }
        }

        if (moduleId is null)
        {
            missing.Add($"module {expected.ModuleKey}");
            return missing;
        }

        foreach (var appKey in expected.AppKeys)
        {
            await using var appCmd = new SqlCommand(
                "SELECT 1 FROM omp.Apps WHERE ModuleId = @ModuleId AND AppKey = @AppKey;",
                conn);
            Add(appCmd, "@ModuleId", moduleId.Value);
            Add(appCmd, "@AppKey", appKey);
            if (await appCmd.ExecuteScalarAsync(ct) is null)
            {
                missing.Add($"app {expected.ModuleKey}.{appKey}");
            }
        }

        return missing;
    }

    private static ModuleDefinitionIntegritySummaryRow BuildModuleDefinitionIntegritySummary(
        ModuleDefinitionDocumentRow definition,
        IReadOnlyList<string> missingMetadata,
        IReadOnlyList<ModuleDefinitionSqlCheckRow> sqlChecks,
        IReadOnlyList<ModuleDefinitionDependencyCheckRow> dependencyChecks,
        IReadOnlyList<ModuleDefinitionArtifactReferenceRow> incompatibleReferences)
    {
        var missingRequiredObjects = sqlChecks.Sum(static row => row.MissingRequiredObjects.Count);
        var repairableSqlScripts = sqlChecks.Count(static row => row.CanExecute
            && !string.Equals(row.Status, "Not recorded", StringComparison.OrdinalIgnoreCase));
        var notRecordedSqlScripts = sqlChecks.Count(static row => string.Equals(row.Status, "Not recorded", StringComparison.OrdinalIgnoreCase));
        var sqlReviewCount = sqlChecks.Count(static row => ModuleDefinitionSqlNeedsReview(row.Status));
        var requiredDependencyIssues = dependencyChecks.Count(static row => row.IsRequired && !string.Equals(row.Status, "ok", StringComparison.OrdinalIgnoreCase));
        var optionalDependencyIssues = dependencyChecks.Count(static row => !row.IsRequired && !string.Equals(row.Status, "ok", StringComparison.OrdinalIgnoreCase));

        var hasError = missingMetadata.Count > 0
            || missingRequiredObjects > 0
            || sqlReviewCount > 0
            || requiredDependencyIssues > 0
            || incompatibleReferences.Count > 0;
        var hasWarning = repairableSqlScripts > 0
            || optionalDependencyIssues > 0;

        var messages = new List<string>();
        if (missingMetadata.Count > 0)
        {
            messages.Add("The module or app metadata declared by this definition is missing.");
        }

        if (missingRequiredObjects > 0)
        {
            messages.Add("Required database objects are missing.");
        }

        if (sqlReviewCount > 0)
        {
            messages.Add("Some SQL scripts need review before Portal can execute them.");
        }

        if (notRecordedSqlScripts > 0)
        {
            messages.Add("One or more SQL scripts have no successful execution record for their current content.");
        }

        if (requiredDependencyIssues > 0 || optionalDependencyIssues > 0)
        {
            messages.Add("Declared module dependencies are missing or incompatible.");
        }

        if (incompatibleReferences.Count > 0)
        {
            messages.Add("Current artifact selections are not compatible with this definition.");
        }

        if (messages.Count == 0)
        {
            messages.Add("Current database state satisfies the declared checks.");
        }

        return new ModuleDefinitionIntegritySummaryRow
        {
            ModuleDefinitionDocumentId = definition.ModuleDefinitionDocumentId,
            ModuleKey = definition.ModuleKey,
            DefinitionVersion = definition.DefinitionVersion,
            IsApplied = definition.IsApplied,
            OverallStatus = hasError ? "error" : hasWarning ? "warning" : "ok",
            OverallStatusLabel = hasError ? "Needs attention" : hasWarning ? "Review" : "OK",
            MetadataStatus = missingMetadata.Count > 0 ? "error" : "ok",
            MetadataStatusLabel = missingMetadata.Count > 0 ? "Missing" : "OK",
            DatabaseStatus = missingRequiredObjects > 0 ? "error" : "ok",
            DatabaseStatusLabel = missingRequiredObjects > 0 ? "Missing" : "OK",
            SqlStatus = GetSqlSummaryStatus(sqlChecks, sqlReviewCount, repairableSqlScripts, notRecordedSqlScripts),
            SqlStatusLabel = GetSqlSummaryStatusLabel(sqlChecks, sqlReviewCount, repairableSqlScripts, notRecordedSqlScripts),
            DependencyStatus = GetDependencySummaryStatus(dependencyChecks, requiredDependencyIssues, optionalDependencyIssues),
            DependencyStatusLabel = GetDependencySummaryStatusLabel(dependencyChecks, requiredDependencyIssues, optionalDependencyIssues),
            ArtifactStatus = incompatibleReferences.Count > 0 ? "error" : "ok",
            ArtifactStatusLabel = incompatibleReferences.Count > 0 ? "Incompatible" : "OK",
            SummaryLabel = hasError ? "Needs attention" : hasWarning ? "Review" : "Current",
            MissingMetadataCount = missingMetadata.Count,
            MissingRequiredObjectCount = missingRequiredObjects,
            RepairableSqlScriptCount = repairableSqlScripts,
            NotRecordedSqlScriptCount = notRecordedSqlScripts,
            SqlReviewCount = sqlReviewCount,
            RequiredDependencyIssueCount = requiredDependencyIssues,
            OptionalDependencyIssueCount = optionalDependencyIssues,
            IncompatibleArtifactReferenceCount = incompatibleReferences.Count,
            Messages = messages
        };
    }

    private static string GetSqlSummaryStatus(
        IReadOnlyList<ModuleDefinitionSqlCheckRow> sqlChecks,
        int sqlReviewCount,
        int repairableSqlScripts,
        int notRecordedSqlScripts)
    {
        if (sqlChecks.Count == 0)
        {
            return "ok";
        }

        if (sqlReviewCount > 0)
        {
            return "error";
        }

        if (repairableSqlScripts > 0)
        {
            return "warning";
        }

        return notRecordedSqlScripts > 0
            ? "neutral"
            : "ok";
    }

    private static string GetSqlSummaryStatusLabel(
        IReadOnlyList<ModuleDefinitionSqlCheckRow> sqlChecks,
        int sqlReviewCount,
        int repairableSqlScripts,
        int notRecordedSqlScripts)
    {
        if (sqlChecks.Count == 0)
        {
            return "No SQL";
        }

        if (sqlReviewCount > 0)
        {
            return "Needs attention";
        }

        if (repairableSqlScripts > 0)
        {
            return "Repair available";
        }

        return notRecordedSqlScripts > 0
            ? "Not recorded"
            : "OK";
    }

    private static string GetDependencySummaryStatus(
        IReadOnlyList<ModuleDefinitionDependencyCheckRow> dependencyChecks,
        int requiredDependencyIssues,
        int optionalDependencyIssues)
    {
        if (requiredDependencyIssues > 0)
        {
            return "error";
        }

        return optionalDependencyIssues > 0
            ? "warning"
            : "ok";
    }

    private static string GetDependencySummaryStatusLabel(
        IReadOnlyList<ModuleDefinitionDependencyCheckRow> dependencyChecks,
        int requiredDependencyIssues,
        int optionalDependencyIssues)
    {
        if (dependencyChecks.Count == 0)
        {
            return "No dependencies";
        }

        if (requiredDependencyIssues > 0)
        {
            return "Needs attention";
        }

        return optionalDependencyIssues > 0
            ? "Review"
            : "OK";
    }

    private static bool ModuleDefinitionSqlNeedsReview(string status)
        => string.Equals(status, "Invalid content hash", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Blocked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Not executable", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ModuleDefinitionDependencyCheckRow> GetModuleDefinitionDependencyChecks(
        ModuleDefinitionDocumentRow definition,
        IReadOnlyList<ModuleDefinitionDocumentRow> appliedDefinitions)
    {
        if (string.IsNullOrWhiteSpace(definition.DefinitionJson))
        {
            return [];
        }

        var dependencies = ReadModuleDefinitionDependencies(definition.DefinitionJson);
        if (dependencies.Count == 0)
        {
            return [];
        }

        var checks = new List<ModuleDefinitionDependencyCheckRow>(dependencies.Count);
        foreach (var dependency in dependencies)
        {
            var applied = appliedDefinitions
                .Where(row => string.Equals(row.ModuleKey, dependency.ModuleKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static row => row.AppliedUtc)
                .ThenByDescending(static row => row.UpdatedUtc)
                .FirstOrDefault();

            if (applied is null)
            {
                checks.Add(new ModuleDefinitionDependencyCheckRow
                {
                    ModuleKey = dependency.ModuleKey,
                    MinDefinitionVersion = dependency.MinDefinitionVersion,
                    MaxDefinitionVersion = dependency.MaxDefinitionVersion,
                    IsRequired = dependency.IsRequired,
                    Status = dependency.IsRequired ? "error" : "warning",
                    StatusLabel = dependency.IsRequired ? "Missing" : "Optional missing",
                    Reason = dependency.Reason
                });
                continue;
            }

            var isCompatible = IsVersionInRange(
                applied.DefinitionVersion,
                dependency.MinDefinitionVersion,
                dependency.MaxDefinitionVersion);

            checks.Add(new ModuleDefinitionDependencyCheckRow
            {
                ModuleKey = dependency.ModuleKey,
                MinDefinitionVersion = dependency.MinDefinitionVersion,
                MaxDefinitionVersion = dependency.MaxDefinitionVersion,
                IsRequired = dependency.IsRequired,
                AppliedDefinitionVersion = applied.DefinitionVersion,
                Status = isCompatible ? "ok" : dependency.IsRequired ? "error" : "warning",
                StatusLabel = isCompatible ? "OK" : "Incompatible",
                Reason = dependency.Reason
            });
        }

        return checks;
    }

    private static void BindArtifact(SqlCommand cmd, ArtifactEditData input, bool includePrimaryKey)
    {
        if (includePrimaryKey)
        {
            Add(cmd, "@ArtifactId", input.ArtifactId);
        }

        Add(cmd, "@AppId", input.AppId);
        Add(cmd, "@Version", input.Version);
        Add(cmd, "@PackageType", input.PackageType);
        Add(cmd, "@TargetName", input.TargetName);
        Add(cmd, "@RelativePath", input.RelativePath);
        Add(cmd, "@Sha256", input.Sha256);
        Add(cmd, "@IsEnabled", input.IsEnabled);
    }

    private static void BindArtifactConfigurationFile(
        SqlCommand cmd,
        ArtifactConfigurationFileEditData input,
        bool includePrimaryKey)
    {
        if (includePrimaryKey)
        {
            Add(cmd, "@ArtifactConfigurationFileId", input.ArtifactConfigurationFileId);
        }

        Add(cmd, "@ArtifactId", input.ArtifactId);
        Add(cmd, "@RelativePath", input.RelativePath);
        Add(cmd, "@FileContent", input.FileContent);
        Add(cmd, "@IsEnabled", input.IsEnabled);
    }

    private static void BindAppInstance(SqlCommand cmd, AppInstanceEditData input)
    {
        Add(cmd, "@AppInstanceId", input.AppInstanceId);
        Add(cmd, "@ModuleInstanceId", input.ModuleInstanceId);
        Add(cmd, "@HostId", input.HostId);
        Add(cmd, "@AppId", input.AppId);
        Add(cmd, "@AppInstanceKey", input.AppInstanceKey);
        Add(cmd, "@DisplayName", input.DisplayName);
        Add(cmd, "@Description", input.Description);
        Add(cmd, "@RoutePath", input.RoutePath);
        Add(cmd, "@PublicUrl", input.PublicUrl);
        Add(cmd, "@InstallPath", input.InstallPath);
        Add(cmd, "@InstallationName", input.InstallationName);
        Add(cmd, "@ArtifactId", input.ArtifactId);
        Add(cmd, "@ConfigId", input.ConfigId);
        Add(cmd, "@ExpectedLogin", input.ExpectedLogin);
        Add(cmd, "@ExpectedClientHostName", input.ExpectedClientHostName);
        Add(cmd, "@ExpectedClientIp", input.ExpectedClientIp);
        Add(cmd, "@IsEnabled", input.IsEnabled);
        Add(cmd, "@IsAllowed", input.IsAllowed);
        Add(cmd, "@DesiredState", input.DesiredState);
        Add(cmd, "@SortOrder", input.SortOrder);
    }

    private static void BindInstanceTemplateAppInstance(
        SqlCommand cmd,
        InstanceTemplateAppInstanceEditData input,
        bool includePrimaryKey)
    {
        if (includePrimaryKey)
        {
            Add(cmd, "@InstanceTemplateAppInstanceId", input.InstanceTemplateAppInstanceId);
        }

        Add(cmd, "@InstanceTemplateModuleInstanceId", input.InstanceTemplateModuleInstanceId);
        Add(cmd, "@InstanceTemplateHostId", input.InstanceTemplateHostId);
        Add(cmd, "@TargetHostTemplateId", input.TargetHostTemplateId);
        Add(cmd, "@AppId", input.AppId);
        Add(cmd, "@AppInstanceKey", input.AppInstanceKey);
        Add(cmd, "@DisplayName", input.DisplayName);
        Add(cmd, "@Description", input.Description);
        Add(cmd, "@RoutePath", input.RoutePath);
        Add(cmd, "@PublicUrl", input.PublicUrl);
        Add(cmd, "@InstallPath", input.InstallPath);
        Add(cmd, "@InstallationName", input.InstallationName);
        Add(cmd, "@DesiredArtifactId", input.DesiredArtifactId);
        Add(cmd, "@DesiredConfigId", input.DesiredConfigId);
        Add(cmd, "@ExpectedLogin", input.ExpectedLogin);
        Add(cmd, "@ExpectedClientHostName", input.ExpectedClientHostName);
        Add(cmd, "@ExpectedClientIp", input.ExpectedClientIp);
        Add(cmd, "@DesiredState", input.DesiredState);
        Add(cmd, "@SortOrder", input.SortOrder);
        Add(cmd, "@IsEnabled", input.IsEnabled);
        Add(cmd, "@IsAllowed", input.IsAllowed);
    }

    private static void BindInstanceTemplateHost(
        SqlCommand cmd,
        InstanceTemplateHostEditData input,
        bool includePrimaryKey)
    {
        if (includePrimaryKey)
        {
            Add(cmd, "@InstanceTemplateHostId", input.InstanceTemplateHostId);
        }

        Add(cmd, "@InstanceTemplateId", input.InstanceTemplateId);
        Add(cmd, "@HostTemplateId", input.HostTemplateId);
        Add(cmd, "@HostKey", input.HostKey);
        Add(cmd, "@DisplayName", input.DisplayName);
        Add(cmd, "@Environment", input.Environment);
        Add(cmd, "@SortOrder", input.SortOrder);
        Add(cmd, "@IsEnabled", input.IsEnabled);
    }

    private static void BindInstanceTemplateModule(
        SqlCommand cmd,
        InstanceTemplateModuleEditData input,
        bool includePrimaryKey)
    {
        if (includePrimaryKey)
        {
            Add(cmd, "@InstanceTemplateModuleInstanceId", input.InstanceTemplateModuleInstanceId);
        }

        Add(cmd, "@InstanceTemplateId", input.InstanceTemplateId);
        Add(cmd, "@ModuleId", input.ModuleId);
        Add(cmd, "@ModuleInstanceKey", input.ModuleInstanceKey);
        Add(cmd, "@DisplayName", input.DisplayName);
        Add(cmd, "@Description", input.Description);
        Add(cmd, "@SortOrder", input.SortOrder);
        Add(cmd, "@IsEnabled", input.IsEnabled);
    }

    private static void BindAppWorkerDefinition(SqlCommand cmd, AppWorkerDefinitionEditData input)
    {
        Add(cmd, "@AppId", input.AppId);
        Add(cmd, "@RuntimeKind", input.RuntimeKind);
        Add(cmd, "@WorkerTypeKey", input.WorkerTypeKey);
        Add(cmd, "@PluginRelativePath", input.PluginRelativePath);
        Add(cmd, "@IsEnabled", input.IsEnabled);
    }

    private async Task<IReadOnlyList<OptionItem>> GetOptionsAsync(string sql, CancellationToken ct)
        => await GetOptionsAsync(sql, ct, bind: null);

    private async Task<IReadOnlyList<OptionItem>> GetOptionsAsync(
        string sql,
        CancellationToken ct,
        Action<SqlCommand>? bind)
    {
        var rows = new List<OptionItem>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        bind?.Invoke(cmd);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            rows.Add(
                new OptionItem
                {
                    Value = rdr.GetString(0),
                    Label = rdr.GetString(1)
                });
        }

        return rows;
    }

    private async Task DeleteAsync(string sql, object id, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteNonQueryAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string sql,
        object id,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool IsVersionInRange(string version, string? minVersion, string? maxVersion)
    {
        if (!string.IsNullOrWhiteSpace(minVersion)
            && CompareArtifactVersions(version, minVersion) < 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(maxVersion)
            && CompareArtifactVersions(version, maxVersion) > 0)
        {
            return false;
        }

        return true;
    }

    private static int CompareArtifactVersions(string left, string right)
    {
        if (TryParseComparableVersion(left, out var leftVersion)
            && TryParseComparableVersion(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseComparableVersion(string? value, out Version version)
    {
        var text = value?.Trim() ?? string.Empty;
        var suffixIndex = text.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            text = text[..suffixIndex];
        }

        return Version.TryParse(text, out version!);
    }

    private static string FormatArtifactVersionRanges(IEnumerable<ArtifactCompatibilitySlot> slots)
        => string.Join(
            ", ",
            slots.Select(slot =>
            {
                var min = string.IsNullOrWhiteSpace(slot.MinArtifactVersion) ? "*" : slot.MinArtifactVersion;
                var max = string.IsNullOrWhiteSpace(slot.MaxArtifactVersion) ? "*" : slot.MaxArtifactVersion;
                return $"{min}..{max}";
            }));

    private static IReadOnlyList<PortableModuleDefinitionSqlScript> ReadPortableSqlScripts(string definitionJson)
    {
        var root = JsonNode.Parse(definitionJson);
        if (root?["sqlScripts"] is not JsonArray items)
        {
            return [];
        }

        var scripts = new List<PortableModuleDefinitionSqlScript>();
        foreach (var item in items)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var key = GetJsonString(obj, "key");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            scripts.Add(new PortableModuleDefinitionSqlScript(
                key,
                GetJsonString(obj, "phase", "setup"),
                GetJsonString(obj, "scope", "module"),
                GetJsonInt(obj, "order", 0),
                GetJsonString(obj, "execution", "idempotent"),
                NullIfWhiteSpace(GetJsonString(obj, "path")),
                NullIfWhiteSpace(GetJsonString(obj, "source")),
                NullIfWhiteSpace(GetJsonString(obj, "inlineSql")),
                NullIfWhiteSpace(GetJsonString(obj, "contentEncoding")),
                NullIfWhiteSpace(GetJsonString(obj, "content")),
                NullIfWhiteSpace(GetJsonString(obj, "sha256"))));
        }

        return scripts;
    }

    private static IReadOnlyList<RequiredDatabaseObject> ReadRequiredDatabaseObjects(string definitionJson)
    {
        var root = JsonNode.Parse(definitionJson);
        var integrity = root?["integrity"] as JsonObject;
        if (integrity is null)
        {
            return [];
        }

        var required = new List<RequiredDatabaseObject>();
        if (integrity["requiredSchemas"] is JsonArray schemas)
        {
            // Malformed/null JSON entries are ignored rather than failing the whole definition.
            required.AddRange(schemas
                .Select(item => item?.GetValue<string>())
                .Where(schema => !string.IsNullOrWhiteSpace(schema))
                .Select(schema => new RequiredDatabaseObject("schema", schema!.Trim(), null, null)));
        }

        if (integrity["requiredTables"] is JsonArray tables)
        {
            required.AddRange(tables
                .OfType<JsonObject>()
                .Select(item => new
                {
                    Schema = GetJsonString(item, "schema"),
                    Name = GetJsonString(item, "name"),
                    Source = NullIfWhiteSpace(GetJsonString(item, "source"))
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Schema) && !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => new RequiredDatabaseObject("table", item.Schema, item.Name, item.Source)));
        }

        return required;
    }

    private static bool IsValidationScript(PortableModuleDefinitionSqlScript script)
        => string.Equals(script.Phase, "validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Phase, "validation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Execution, "validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Execution, "validation", StringComparison.OrdinalIgnoreCase);

    private static async Task<ModuleDefinitionSqlCheckRow> BuildModuleDefinitionValidationSqlCheckAsync(
        SqlConnection conn,
        PortableModuleDefinitionSqlScript script,
        CancellationToken ct)
    {
        var sqlText = ResolvePortableSqlText(script);
        var scriptSha256 = ComputeTextSha256(sqlText ?? string.Empty);
        var suppliedSha256 = NullIfWhiteSpace(script.Sha256);
        var hasInlineSql = !string.IsNullOrWhiteSpace(sqlText);
        var hashMatches = suppliedSha256 is null
            || string.Equals(suppliedSha256, scriptSha256, StringComparison.OrdinalIgnoreCase);
        var safety = hasInlineSql
            ? ValidateReadOnlyModuleDefinitionSql(sqlText!)
            : "The validation script has no embedded SQL content.";
        var isSafe = safety is null;
        var status = "OK";
        var message = "The validation script reports that the module is healthy.";
        var needsRepair = false;

        if (!hasInlineSql)
        {
            status = "Not executable";
            message = "The validation script has no embedded SQL content.";
        }
        else if (!hashMatches)
        {
            status = "Invalid content hash";
            message = "The validation SQL content does not match the declared SHA-256 hash.";
        }
        else if (!isSafe)
        {
            status = "Blocked";
            message = safety!;
        }
        else
        {
            try
            {
                var result = await ExecuteModuleDefinitionValidationSqlAsync(conn, sqlText!, ct);
                if (!result.IsHealthy)
                {
                    status = "Needs repair";
                    message = string.IsNullOrWhiteSpace(result.Message)
                        ? "The validation script reports that the module needs repair."
                        : result.Message;
                    needsRepair = true;
                }
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                status = "Needs repair";
                message = $"The validation script failed, so the module should be repaired: {ex.Message}";
                needsRepair = true;
            }
        }

        return new ModuleDefinitionSqlCheckRow
        {
            Key = script.Key,
            Phase = script.Phase,
            Scope = script.Scope,
            Order = script.Order,
            Execution = script.Execution,
            Path = script.Path,
            Source = script.Source,
            ScriptSha256 = scriptSha256,
            IsValidation = true,
            HasInlineSql = hasInlineSql,
            IsSafe = isSafe && hashMatches,
            HasSuccessfulExecution = string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase),
            NeedsExecution = needsRepair,
            CanExecute = false,
            Status = status,
            StatusMessage = message,
            MissingRequiredObjects = []
        };
    }

    private static ExpectedModuleMetadata ReadExpectedModuleMetadata(string definitionJson, string fallbackModuleKey)
    {
        var root = JsonNode.Parse(definitionJson) as JsonObject;
        if (root is null)
        {
            return new ExpectedModuleMetadata(fallbackModuleKey, false, []);
        }

        var moduleKey = NullIfWhiteSpace(GetJsonString(root, "moduleKey")) ?? fallbackModuleKey;
        var appKeys = root["apps"] is JsonArray apps
            ? apps
                .OfType<JsonObject>()
                .Select(item => NullIfWhiteSpace(GetJsonString(item, "appKey")))
                .Where(appKey => appKey is not null)
                .Select(appKey => appKey!)
                .ToList()
            : [];

        var expectsModule = root["module"] is JsonObject || appKeys.Count > 0;
        return new ExpectedModuleMetadata(moduleKey, expectsModule, appKeys);
    }

    private static IReadOnlyList<ModuleDefinitionDependencySpec> ReadModuleDefinitionDependencies(string definitionJson)
    {
        var root = JsonNode.Parse(definitionJson) as JsonObject;
        if (root?["moduleDependencies"] is not JsonArray dependencies)
        {
            return [];
        }

        return dependencies
            .OfType<JsonObject>()
            .Select(item => new
            {
                ModuleKey = NullIfWhiteSpace(GetJsonString(item, "moduleKey")),
                MinDefinitionVersion = NullIfWhiteSpace(GetJsonString(item, "minDefinitionVersion")),
                MaxDefinitionVersion = NullIfWhiteSpace(GetJsonString(item, "maxDefinitionVersion")),
                Required = GetJsonBool(item, "required", true),
                Reason = NullIfWhiteSpace(GetJsonString(item, "reason"))
            })
            .Where(item => item.ModuleKey is not null)
            .Select(item => new ModuleDefinitionDependencySpec(
                item.ModuleKey!,
                item.MinDefinitionVersion,
                item.MaxDefinitionVersion,
                item.Required,
                item.Reason))
            .ToArray();
    }

    private static string? ResolvePortableSqlText(PortableModuleDefinitionSqlScript script)
    {
        if (!string.IsNullOrWhiteSpace(script.InlineSql))
        {
            return script.InlineSql;
        }

        if (string.IsNullOrWhiteSpace(script.Content))
        {
            return null;
        }

        if (string.Equals(script.ContentEncoding, "base64-utf8", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(script.Content));
        }

        return script.Content;
    }

    private static async Task<string> PatchBootstrapPortalAdminPrincipalAsync(
        SqlConnection conn,
        string sqlText,
        CancellationToken ct)
    {
        if (!sqlText.Contains(BootstrapPortalAdminPrincipalPlaceholder, StringComparison.Ordinal))
        {
            return sqlText;
        }

        var principal = await ResolveExistingBootstrapPortalAdminPrincipalAsync(conn, ct)
            ?? throw new InvalidOperationException(
                "The module definition SQL contains a bootstrap PortalAdmin placeholder, but no existing PortalAdmins principal was found to reuse.");

        var principalLiteral = ToSqlUnicodeLiteral(principal.Principal);
        var principalTypeLiteral = ToSqlUnicodeLiteral(principal.PrincipalType);
        var patched = Regex.Replace(
            sqlText,
            @"DECLARE\s+@BootstrapPortalAdminPrincipal\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';",
            $"DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = {principalLiteral};",
            RegexOptions.IgnoreCase);

        // The historical Portal initialization script hardcodes ADUser because the
        // installer normally patches the script before execution. Portal repair runs
        // after bootstrap and must preserve whichever principal type is already active.
        patched = Regex.Replace(
            patched,
            @"PrincipalType\s*=\s*N'ADUser'",
            $"PrincipalType = {principalTypeLiteral}",
            RegexOptions.IgnoreCase);
        patched = Regex.Replace(
            patched,
            @"VALUES\s*\(\s*@PortalAdminsRoleId\s*,\s*N'ADUser'\s*,\s*@BootstrapPortalAdminPrincipal\s*\)",
            $"VALUES(@PortalAdminsRoleId, {principalTypeLiteral}, @BootstrapPortalAdminPrincipal)",
            RegexOptions.IgnoreCase);

        return patched;
    }

    private static async Task<BootstrapPortalAdminPrincipal?> ResolveExistingBootstrapPortalAdminPrincipalAsync(
        SqlConnection conn,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       rp.PrincipalType,
       rp.Principal
FROM omp.RolePrincipals rp
INNER JOIN omp.Roles r ON r.RoleId = rp.RoleId
WHERE r.Name = N'PortalAdmins'
  AND NULLIF(LTRIM(RTRIM(rp.PrincipalType)), N'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(rp.Principal)), N'') IS NOT NULL
  AND rp.Principal NOT IN (N'__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__', N'REPLACE_ME\UserOrGroup')
ORDER BY CASE rp.PrincipalType WHEN N'ADUser' THEN 0 WHEN N'ADGroup' THEN 1 ELSE 2 END,
         rp.Principal;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new BootstrapPortalAdminPrincipal(rdr.GetString(0), rdr.GetString(1));
    }

    private static string ToSqlUnicodeLiteral(string value)
        => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task<Dictionary<string, IReadOnlyList<string>>> GetMissingRequiredObjectsByScriptKeyAsync(
        SqlConnection conn,
        IReadOnlyList<PortableModuleDefinitionSqlScript> scripts,
        IReadOnlyList<RequiredDatabaseObject> requiredObjects,
        CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var repairScripts = scripts
            .Where(static item => !IsValidationScript(item))
            .ToList();
        var fallbackScript = repairScripts
            .OrderBy(static item => string.Equals(item.Phase, "setup", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static item => item.Order)
            .FirstOrDefault();

        foreach (var required in requiredObjects)
        {
            var exists = required.Kind switch
            {
                "schema" => await SchemaExistsAsync(conn, required.Schema, ct),
                "table" when required.Name is not null => await TableExistsAsync(conn, required.Schema, required.Name, ct),
                _ => true
            };
            if (exists)
            {
                continue;
            }

            var owningScript = repairScripts.FirstOrDefault(script => RequiredObjectMatchesScriptSource(required, script))
                ?? fallbackScript;
            if (owningScript is null)
            {
                continue;
            }

            var list = result.GetValueOrDefault(owningScript.Key);
            if (list is null)
            {
                list = [];
                result[owningScript.Key] = list;
            }

            list.Add(required.Kind == "schema"
                ? $"schema {required.Schema}"
                : $"table {required.Schema}.{required.Name}");
        }

        return result.ToDictionary(
            static item => item.Key,
            static item => (IReadOnlyList<string>)item.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> SchemaExistsAsync(SqlConnection conn, string schema, CancellationToken ct)
    {
        const string sql = "SELECT 1 FROM sys.schemas WHERE name = @schema;";
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@schema", schema);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> TableExistsAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = @"
SELECT 1
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @schema
  AND t.name = @table;";

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@schema", schema);
        Add(cmd, "@table", table);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static bool RequiredObjectMatchesScriptSource(
        RequiredDatabaseObject required,
        PortableModuleDefinitionSqlScript script)
    {
        if (string.IsNullOrWhiteSpace(required.Source))
        {
            return false;
        }

        return string.Equals(required.Source, script.Path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(required.Source, script.Source, StringComparison.OrdinalIgnoreCase)
            || string.Equals(required.Source, script.Key, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<(string ScriptKey, string ScriptSha256), ModuleDefinitionSqlExecutionInfo>> GetLatestModuleDefinitionSqlExecutionsAsync(
        SqlConnection conn,
        int moduleDefinitionDocumentId,
        CancellationToken ct)
    {
        const string sql = @"
WITH Latest AS
(
    SELECT ScriptKey,
           ScriptSha256,
           ExecutionStatus,
           CompletedUtc,
           ErrorMessage,
           ROW_NUMBER() OVER
           (
               PARTITION BY ScriptKey, ScriptSha256
               ORDER BY StartedUtc DESC, ModuleDefinitionSqlExecutionId DESC
           ) AS RowNumber
    FROM omp.ModuleDefinitionSqlExecutions
    WHERE ModuleDefinitionDocumentId = @ModuleDefinitionDocumentId
)
SELECT ScriptKey,
       ScriptSha256,
       ExecutionStatus,
       CompletedUtc,
       ErrorMessage
FROM Latest
WHERE RowNumber = 1;";

        var executions = new Dictionary<(string ScriptKey, string ScriptSha256), ModuleDefinitionSqlExecutionInfo>();
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ModuleDefinitionDocumentId", moduleDefinitionDocumentId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            executions[(rdr.GetString(0), rdr.GetString(1))] = new ModuleDefinitionSqlExecutionInfo(
                rdr.GetString(2),
                rdr.IsDBNull(3) ? null : rdr.GetDateTime(3),
                rdr.IsDBNull(4) ? null : rdr.GetString(4));
        }

        return executions;
    }

    private static async Task AcquireModuleDefinitionSqlExecutionLockAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
DECLARE @Result int;
EXEC @Result = sys.sp_getapplock
    @Resource = N'omp.module-definition-sql-repair',
    @LockMode = N'Exclusive',
    @LockOwner = N'Session',
    @LockTimeout = 0;
SELECT @Result;";

        await using var cmd = new SqlCommand(sql, conn);
        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (result < 0)
        {
            throw new InvalidOperationException("Another module definition SQL repair is already running.");
        }
    }

    private static async Task<long> InsertModuleDefinitionSqlExecutionAsync(
        SqlConnection conn,
        int moduleDefinitionDocumentId,
        PortableModuleDefinitionSqlScript script,
        string scriptSha256,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.ModuleDefinitionSqlExecutions
(
    ModuleDefinitionDocumentId,
    ScriptKey,
    ScriptPhase,
    ScriptOrder,
    ScriptSha256,
    ExecutionStatus
)
VALUES
(
    @ModuleDefinitionDocumentId,
    @ScriptKey,
    @ScriptPhase,
    @ScriptOrder,
    @ScriptSha256,
    N'Running'
);

SELECT CAST(SCOPE_IDENTITY() AS bigint);";

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ModuleDefinitionDocumentId", moduleDefinitionDocumentId);
        Add(cmd, "@ScriptKey", script.Key);
        Add(cmd, "@ScriptPhase", script.Phase);
        Add(cmd, "@ScriptOrder", script.Order);
        Add(cmd, "@ScriptSha256", scriptSha256);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task CompleteModuleDefinitionSqlExecutionAsync(
        SqlConnection conn,
        long executionId,
        string status,
        string? errorMessage,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.ModuleDefinitionSqlExecutions
SET ExecutionStatus = @ExecutionStatus,
    CompletedUtc = SYSUTCDATETIME(),
    ErrorMessage = @ErrorMessage
WHERE ModuleDefinitionSqlExecutionId = @ModuleDefinitionSqlExecutionId;";

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ModuleDefinitionSqlExecutionId", executionId);
        Add(cmd, "@ExecutionStatus", status);
        Add(cmd, "@ErrorMessage", Truncate(errorMessage, 4000));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ModuleDefinitionValidationResult> ExecuteModuleDefinitionValidationSqlAsync(
        SqlConnection conn,
        string sqlText,
        CancellationToken ct)
    {
        ModuleDefinitionValidationResult? result = null;
        foreach (var batch in SplitSqlBatches(sqlText))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var cmd = new SqlCommand(batch, conn)
            {
                CommandTimeout = 3600
            };
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            do
            {
                if (await rdr.ReadAsync(ct))
                {
                    result = ReadModuleDefinitionValidationResult(rdr);
                }
            }
            while (await rdr.NextResultAsync(ct));
        }

        return result ?? new ModuleDefinitionValidationResult(
            false,
            "The validation script did not return a result row.");
    }

    private static ModuleDefinitionValidationResult ReadModuleDefinitionValidationResult(SqlDataReader rdr)
    {
        var healthyOrdinal = TryGetOrdinal(rdr, "IsHealthy") ?? 0;
        if (healthyOrdinal >= rdr.FieldCount)
        {
            throw new InvalidOperationException("The validation result must contain an IsHealthy column or at least one column.");
        }

        var messageOrdinal = TryGetOrdinal(rdr, "Message");
        var isHealthy = ConvertValidationBoolean(rdr.GetValue(healthyOrdinal))
            ?? throw new InvalidOperationException("The validation result IsHealthy value must be true/false or 1/0.");
        var message = messageOrdinal.HasValue && !rdr.IsDBNull(messageOrdinal.Value)
            ? Convert.ToString(rdr.GetValue(messageOrdinal.Value))
            : null;

        return new ModuleDefinitionValidationResult(isHealthy, message);
    }

    private static int? TryGetOrdinal(SqlDataReader rdr, string name)
    {
        for (var index = 0; index < rdr.FieldCount; index++)
        {
            if (string.Equals(rdr.GetName(index), name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }

    private static bool? ConvertValidationBoolean(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        if (value is bool boolean)
        {
            return boolean;
        }

        if (value is byte or short or int or long or decimal)
        {
            return Convert.ToDecimal(value) != 0m;
        }

        var text = Convert.ToString(value)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.ToLowerInvariant() switch
        {
            "1" or "true" or "ok" or "healthy" or "pass" or "passed" => true,
            "0" or "false" or "error" or "unhealthy" or "fail" or "failed" => false,
            _ => null
        };
    }

    private static async Task ExecuteSqlBatchesAsync(SqlConnection conn, string sqlText, CancellationToken ct)
    {
        foreach (var batch in SplitSqlBatches(sqlText))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var cmd = new SqlCommand(batch, conn)
            {
                CommandTimeout = 3600
            };
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static IEnumerable<string> SplitSqlBatches(string sqlText)
    {
        var batch = new StringBuilder();
        using var reader = new StringReader(sqlText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (Regex.IsMatch(line, @"^\s*GO\s*(?:--.*)?$", RegexOptions.IgnoreCase))
            {
                yield return batch.ToString();
                batch.Clear();
                continue;
            }

            batch.AppendLine(line);
        }

        yield return batch.ToString();
    }

    private static string? ValidateSafeModuleDefinitionSql(string sqlText)
    {
        if (Regex.IsMatch(sqlText, @"(?im)^\s*USE\s+(?:\[[^\]]+\]|[A-Za-z0-9_]+)\s*;?\s*$"))
        {
            return "Module definition SQL must not contain USE database directives. Portal runs repairs on the configured OMP database.";
        }

        if (Regex.IsMatch(sqlText, @"(?is)\bDROP\s+(?:DATABASE|SCHEMA|TABLE)\b"))
        {
            return "The script contains DROP DATABASE, DROP SCHEMA, or DROP TABLE, which is not allowed for Portal repair.";
        }

        if (Regex.IsMatch(sqlText, @"(?is)\bTRUNCATE\s+TABLE\b"))
        {
            return "The script contains TRUNCATE TABLE, which is not allowed for Portal repair.";
        }

        var unsafeDeleteStatement = Regex.Matches(sqlText, @"(?is)(?:\A|(?<=\n)|;)[^\S\r\n]*DELETE\b(?<statement>.*?)(?:;|\r?\n\s*GO\b|\z)")
            .Cast<Match>()
            .Select(static match => match.Groups["statement"].Value)
            .FirstOrDefault(static statement => !Regex.IsMatch(statement, @"(?is)\bWHERE\b"));
        if (unsafeDeleteStatement is not null)
        {
            return "The script contains DELETE without a WHERE clause, which is not allowed for Portal repair.";
        }

        return null;
    }

    private static string? ValidateReadOnlyModuleDefinitionSql(string sqlText)
    {
        var safety = ValidateSafeModuleDefinitionSql(sqlText);
        if (safety is not null)
        {
            return safety;
        }

        return Regex.IsMatch(
            sqlText,
            @"(?is)\b(?:INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC(?:UTE)?|GRANT|REVOKE|DENY)\b")
            ? "Validation SQL must be read-only and return an IsHealthy result."
            : null;
    }

    private static string ComputeTextSha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string? Truncate(string? value, int maxLength)
        => value is not null && value.Length > maxLength ? value[..maxLength] : value;

    private static string GetJsonString(JsonObject obj, string propertyName, string defaultValue = "")
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return defaultValue;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.Trim()
            : defaultValue;
    }

    private static int GetJsonInt(JsonObject obj, string propertyName, int defaultValue)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is not JsonValue value)
        {
            return defaultValue;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text)
            && int.TryParse(text, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static bool GetJsonBool(JsonObject obj, string propertyName, bool defaultValue)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is not JsonValue value)
        {
            return defaultValue;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return value.TryGetValue<string>(out var text)
            && bool.TryParse(text, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PortableModuleDefinitionSqlScript(
        string Key,
        string Phase,
        string Scope,
        int Order,
        string Execution,
        string? Path,
        string? Source,
        string? InlineSql,
        string? ContentEncoding,
        string? Content,
        string? Sha256);

    private sealed record RequiredDatabaseObject(
        string Kind,
        string Schema,
        string? Name,
        string? Source);

    private sealed record ModuleDefinitionValidationResult(
        bool IsHealthy,
        string? Message);

    private sealed record ExpectedModuleMetadata(
        string ModuleKey,
        bool ExpectsModule,
        IReadOnlyList<string> AppKeys);

    private sealed record ModuleDefinitionDependencySpec(
        string ModuleKey,
        string? MinDefinitionVersion,
        string? MaxDefinitionVersion,
        bool IsRequired,
        string? Reason);

    private sealed record ModuleDefinitionSqlExecutionInfo(
        string Status,
        DateTime? CompletedUtc,
        string? ErrorMessage);

    private sealed record BootstrapPortalAdminPrincipal(
        string PrincipalType,
        string Principal);

    private sealed record ArtifactAutoApplyTarget(
        int ArtifactId,
        int AppId,
        string Version,
        string PackageType,
        string AppType);

    private static void Add(SqlCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
