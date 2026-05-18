// File: OpenModulePlatform.Portal/Services/OmpAdminRepository.Editor.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// CRUD and lookup methods that back the editable Portal admin pages.
/// This partial keeps write-oriented admin logic separate from the read-only list pages in <c>OmpAdminRepository.cs</c>.
/// </summary>
public sealed partial class OmpAdminRepository
{
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
            AppId = rdr.GetInt32(4),
            AppInstanceKey = rdr.GetString(5),
            DisplayName = rdr.GetString(6),
            Description = rdr.IsDBNull(7) ? null : rdr.GetString(7),
            RoutePath = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            PublicUrl = rdr.IsDBNull(9) ? null : rdr.GetString(9),
            InstallPath = rdr.IsDBNull(10) ? null : rdr.GetString(10),
            InstallationName = rdr.IsDBNull(11) ? null : rdr.GetString(11),
            DesiredArtifactId = rdr.IsDBNull(12) ? null : rdr.GetInt32(12),
            DesiredConfigId = rdr.IsDBNull(13) ? null : rdr.GetInt32(13),
            ExpectedLogin = rdr.IsDBNull(14) ? null : rdr.GetString(14),
            ExpectedClientHostName = rdr.IsDBNull(15) ? null : rdr.GetString(15),
            ExpectedClientIp = rdr.IsDBNull(16) ? null : rdr.GetString(16),
            IsEnabled = rdr.GetBoolean(17),
            IsAllowed = rdr.GetBoolean(18),
            DesiredState = rdr.GetByte(19),
            SortOrder = rdr.GetInt32(20)
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

    public Task DeleteInstanceTemplateHostAsync(int instanceTemplateHostId, CancellationToken ct)
        => DeleteAsync(
            "DELETE FROM omp.InstanceTemplateHosts WHERE InstanceTemplateHostId = @Id;",
            instanceTemplateHostId,
            ct);

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
            Description = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            IsEnabled = rdr.GetBoolean(6),
            SortOrder = rdr.GetInt32(7)
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
SELECT ArtifactId,
       AppId,
       Version,
       PackageType,
       TargetName,
       RelativePath,
       Sha256,
       IsEnabled
FROM omp.Artifacts
WHERE ArtifactId = @ArtifactId;";

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
            Version = rdr.GetString(2),
            PackageType = rdr.GetString(3),
            TargetName = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            RelativePath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            Sha256 = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            IsEnabled = rdr.GetBoolean(7)
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

    public Task DeleteArtifactAsync(int artifactId, CancellationToken ct)
        => DeleteAsync("DELETE FROM omp.Artifacts WHERE ArtifactId = @Id;", artifactId, ct);

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

    public async Task<ArtifactDuplicateInfo?> FindArtifactBySha256Async(string sha256, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       ar.ArtifactId,
       a.AppKey,
       ar.Version,
       ar.PackageType,
       ar.TargetName
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
            AppKey = rdr.GetString(1),
            Version = rdr.GetString(2),
            PackageType = rdr.GetString(3),
            TargetName = rdr.IsDBNull(4) ? null : rdr.GetString(4)
        };
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
       AppType
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
            AppType = rdr.GetString(3)
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
        Add(cmd, "@Description", input.Description);
        Add(cmd, "@IsEnabled", input.IsEnabled);
        Add(cmd, "@SortOrder", input.SortOrder);
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

    private static void Add(SqlCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
