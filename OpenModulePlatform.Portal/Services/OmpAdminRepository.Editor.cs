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

    public Task<IReadOnlyList<OptionItem>> GetAppOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(a.AppId AS nvarchar(50)),
       m.ModuleKey + N' / ' + a.AppKey + N' - ' + a.DisplayName
FROM omp.Apps a
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
ORDER BY m.ModuleKey, a.SortOrder, a.AppKey;",
            ct);

    public Task<IReadOnlyList<OptionItem>> GetArtifactOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(ar.ArtifactId AS nvarchar(50)),
       a.AppKey + N' / ' + ar.Version + N' / ' + ar.PackageType
       + COALESCE(N' / ' + ar.TargetName, N'')
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
ORDER BY a.AppKey, ar.CreatedUtc DESC, ar.ArtifactId DESC;",
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
        const string sql = @"
SELECT HostId,
       InstanceId,
       HostKey,
       DisplayName,
       Environment,
       OsFamily,
       OsVersion,
       Architecture,
       IsEnabled
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

        return new HostEditData
        {
            HostId = rdr.GetGuid(0),
            InstanceId = rdr.GetGuid(1),
            HostKey = rdr.GetString(2),
            DisplayName = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            Environment = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            OsFamily = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            OsVersion = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            Architecture = rdr.IsDBNull(7) ? null : rdr.GetString(7),
            IsEnabled = rdr.GetBoolean(8)
        };
    }

    public async Task<Guid> SaveHostAsync(HostEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.HostId == Guid.Empty)
        {
            input.HostId = Guid.NewGuid();

            const string insertSql = @"
INSERT INTO omp.Hosts
(
    HostId,
    InstanceId,
    HostKey,
    DisplayName,
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
    @DisplayName,
    @Environment,
    @OsFamily,
    @OsVersion,
    @Architecture,
    @IsEnabled
);";

            await using var insert = new SqlCommand(insertSql, conn);
            BindHost(insert, input);
            await insert.ExecuteNonQueryAsync(ct);
            return input.HostId;
        }

        const string updateSql = @"
UPDATE omp.Hosts
SET InstanceId = @InstanceId,
    HostKey = @HostKey,
    DisplayName = @DisplayName,
    Environment = @Environment,
    OsFamily = @OsFamily,
    OsVersion = @OsVersion,
    Architecture = @Architecture,
    IsEnabled = @IsEnabled,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostId = @HostId;";

        await using var update = new SqlCommand(updateSql, conn);
        BindHost(update, input);
        await update.ExecuteNonQueryAsync(ct);
        return input.HostId;
    }

    public Task DeleteHostAsync(Guid hostId, CancellationToken ct)
        => DeleteAsync("DELETE FROM omp.Hosts WHERE HostId = @Id;", hostId, ct);

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

        await using var update = new SqlCommand(updateSql, conn);
        BindAppInstance(update, input);
        await update.ExecuteNonQueryAsync(ct);
        return input.AppInstanceId;
    }

    public Task DeleteAppInstanceAsync(Guid appInstanceId, CancellationToken ct)
        => DeleteAsync(
            "DELETE FROM omp.AppInstances WHERE AppInstanceId = @Id;",
            appInstanceId,
            ct);

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

    private static void BindHost(SqlCommand cmd, HostEditData input)
    {
        Add(cmd, "@HostId", input.HostId);
        Add(cmd, "@InstanceId", input.InstanceId);
        Add(cmd, "@HostKey", input.HostKey);
        Add(cmd, "@DisplayName", input.DisplayName);
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

    private async Task<IReadOnlyList<OptionItem>> GetOptionsAsync(string sql, CancellationToken ct)
    {
        var rows = new List<OptionItem>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
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

    private static void Add(SqlCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
