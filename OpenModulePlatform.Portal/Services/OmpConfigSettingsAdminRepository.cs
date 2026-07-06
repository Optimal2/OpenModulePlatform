// File: OpenModulePlatform.Portal/Services/OmpConfigSettingsAdminRepository.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Portal administration for installation-scoped OMP configuration values.
/// The allowed setting definitions are seeded by OMP SQL upgrades; this repository
/// only manages the concrete values for the current installation.
/// </summary>
public sealed class OmpConfigSettingsAdminRepository
{
    private readonly SqlConnectionFactory _db;

    public OmpConfigSettingsAdminRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ConfigSettingDefinitionRow>> GetDefinitionsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT ConfigSettingId,
       ConfigCategory,
       ConfigSetting,
       Description,
       ValidationRegex,
       ExampleValues,
       IsEnabled,
       SortOrder
FROM omp.config_setting_definitions
ORDER BY ConfigCategory,
         SortOrder,
         ConfigSetting;";

        var rows = new List<ConfigSettingDefinitionRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            rows.Add(ReadDefinition(rdr));
        }

        return rows;
    }

    public async Task<ConfigSettingDefinitionRow?> GetDefinitionAsync(int configSettingId, CancellationToken ct)
    {
        const string sql = @"
SELECT ConfigSettingId,
       ConfigCategory,
       ConfigSetting,
       Description,
       ValidationRegex,
       ExampleValues,
       IsEnabled,
       SortOrder
FROM omp.config_setting_definitions
WHERE ConfigSettingId = @ConfigSettingId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ConfigSettingId", configSettingId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        return await rdr.ReadAsync(ct) ? ReadDefinition(rdr) : null;
    }

    public async Task<IReadOnlyList<ConfigSettingValueRow>> GetValuesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT cs.ConfigId,
       cs.ConfigSettingId,
       def.ConfigCategory,
       def.ConfigSetting,
       def.Description,
       def.ValidationRegex,
       def.ExampleValues,
       def.IsEnabled,
       cs.ConfigValue,
       cs.ConfigUsr,
       u.display_name AS UserDisplayName,
       cs.ConfigPermission,
       p.Name AS PermissionName,
       cs.ConfigRole,
       r.Name AS RoleName,
       cs.ConfigPriority
FROM omp.config_settings cs
INNER JOIN omp.config_setting_definitions def
    ON def.ConfigSettingId = cs.ConfigSettingId
LEFT JOIN omp.users u
    ON u.user_id = cs.ConfigUsr
LEFT JOIN omp.Permissions p
    ON p.PermissionId = cs.ConfigPermission
LEFT JOIN omp.Roles r
    ON r.RoleId = cs.ConfigRole
ORDER BY def.ConfigCategory,
         def.SortOrder,
         def.ConfigSetting,
         cs.ConfigScopeRank DESC,
         cs.ConfigPriority DESC,
         cs.ConfigId DESC;";

        var rows = new List<ConfigSettingValueRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            rows.Add(ReadValue(rdr));
        }

        return rows;
    }

    public async Task<ConfigSettingValueRow?> GetValueAsync(int configId, CancellationToken ct)
    {
        const string sql = @"
SELECT cs.ConfigId,
       cs.ConfigSettingId,
       def.ConfigCategory,
       def.ConfigSetting,
       def.Description,
       def.ValidationRegex,
       def.ExampleValues,
       def.IsEnabled,
       cs.ConfigValue,
       cs.ConfigUsr,
       u.display_name AS UserDisplayName,
       cs.ConfigPermission,
       p.Name AS PermissionName,
       cs.ConfigRole,
       r.Name AS RoleName,
       cs.ConfigPriority
FROM omp.config_settings cs
INNER JOIN omp.config_setting_definitions def
    ON def.ConfigSettingId = cs.ConfigSettingId
LEFT JOIN omp.users u
    ON u.user_id = cs.ConfigUsr
LEFT JOIN omp.Permissions p
    ON p.PermissionId = cs.ConfigPermission
LEFT JOIN omp.Roles r
    ON r.RoleId = cs.ConfigRole
WHERE cs.ConfigId = @ConfigId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ConfigId", configId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        return await rdr.ReadAsync(ct) ? ReadValue(rdr) : null;
    }

    public Task<IReadOnlyList<OptionItem>> GetUserOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(user_id AS nvarchar(50)),
       display_name + N' (id: ' + CONVERT(nvarchar(20), user_id) + N')'
FROM omp.users
WHERE account_status = 1
ORDER BY display_name,
         user_id;",
            ct);

    public Task<IReadOnlyList<OptionItem>> GetPermissionOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(PermissionId AS nvarchar(50)),
       Name
FROM omp.Permissions
ORDER BY Name;",
            ct);

    public Task<IReadOnlyList<OptionItem>> GetRoleOptionsAsync(CancellationToken ct)
        => GetOptionsAsync(
            @"
SELECT CAST(RoleId AS nvarchar(50)),
       Name
FROM omp.Roles
ORDER BY Name;",
            ct);

    public async Task<int> SaveValueAsync(ConfigSettingValueEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.ConfigId == 0)
        {
            const string insertSql = @"
INSERT INTO omp.config_settings
(
    ConfigSettingId,
    ConfigValue,
    ConfigUsr,
    ConfigPermission,
    ConfigRole,
    ConfigPriority
)
VALUES
(
    @ConfigSettingId,
    @ConfigValue,
    @ConfigUsr,
    @ConfigPermission,
    @ConfigRole,
    @ConfigPriority
);

SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var insert = new SqlCommand(insertSql, conn);
            Bind(insert, input, includePrimaryKey: false);
            return Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
        }

        const string updateSql = @"
UPDATE omp.config_settings
SET ConfigSettingId = @ConfigSettingId,
    ConfigValue = @ConfigValue,
    ConfigUsr = @ConfigUsr,
    ConfigPermission = @ConfigPermission,
    ConfigRole = @ConfigRole,
    ConfigPriority = @ConfigPriority
WHERE ConfigId = @ConfigId;";

        await using var update = new SqlCommand(updateSql, conn);
        Bind(update, input, includePrimaryKey: true);
        await update.ExecuteNonQueryAsync(ct);
        return input.ConfigId;
    }

    public async Task DeleteValueAsync(int configId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("DELETE FROM omp.config_settings WHERE ConfigId = @ConfigId;", conn);
        Add(cmd, "@ConfigId", configId);
        await cmd.ExecuteNonQueryAsync(ct);
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

    private static ConfigSettingDefinitionRow ReadDefinition(SqlDataReader rdr)
        => new()
        {
            ConfigSettingId = rdr.GetInt32(0),
            ConfigCategory = rdr.GetString(1),
            ConfigSetting = rdr.GetString(2),
            Description = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            ValidationRegex = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            ExampleValues = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            IsEnabled = rdr.GetBoolean(6),
            SortOrder = rdr.GetInt32(7)
        };

    private static ConfigSettingValueRow ReadValue(SqlDataReader rdr)
        => new()
        {
            ConfigId = rdr.GetInt32(0),
            ConfigSettingId = rdr.GetInt32(1),
            ConfigCategory = rdr.GetString(2),
            ConfigSetting = rdr.GetString(3),
            Description = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            ValidationRegex = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            ExampleValues = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            IsDefinitionEnabled = rdr.GetBoolean(7),
            ConfigValue = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            ConfigUsr = rdr.IsDBNull(9) ? null : rdr.GetInt32(9),
            UserDisplayName = rdr.IsDBNull(10) ? null : rdr.GetString(10),
            ConfigPermission = rdr.IsDBNull(11) ? null : rdr.GetInt32(11),
            PermissionName = rdr.IsDBNull(12) ? null : rdr.GetString(12),
            ConfigRole = rdr.IsDBNull(13) ? null : rdr.GetInt32(13),
            RoleName = rdr.IsDBNull(14) ? null : rdr.GetString(14),
            ConfigPriority = rdr.GetInt32(15)
        };

    private static void Bind(SqlCommand cmd, ConfigSettingValueEditData input, bool includePrimaryKey)
    {
        if (includePrimaryKey)
        {
            Add(cmd, "@ConfigId", input.ConfigId);
        }

        Add(cmd, "@ConfigSettingId", input.ConfigSettingId);
        Add(cmd, "@ConfigValue", input.ConfigValue);
        Add(cmd, "@ConfigUsr", input.ConfigUsr);
        Add(cmd, "@ConfigPermission", input.ConfigPermission);
        Add(cmd, "@ConfigRole", input.ConfigRole);
        Add(cmd, "@ConfigPriority", input.ConfigPriority);
    }

    private static void Add(SqlCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}

public sealed class ConfigSettingDefinitionRow
{
    public int ConfigSettingId { get; set; }

    public string ConfigCategory { get; set; } = string.Empty;

    public string ConfigSetting { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? ValidationRegex { get; set; }

    public string? ExampleValues { get; set; }

    public bool IsEnabled { get; set; }

    public int SortOrder { get; set; }

    public string Key => $"{ConfigCategory}/{ConfigSetting}";
}

public sealed class ConfigSettingValueRow
{
    public int ConfigId { get; set; }

    public int ConfigSettingId { get; set; }

    public string ConfigCategory { get; set; } = string.Empty;

    public string ConfigSetting { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? ValidationRegex { get; set; }

    public string? ExampleValues { get; set; }

    public bool IsDefinitionEnabled { get; set; }

    public string? ConfigValue { get; set; }

    public int? ConfigUsr { get; set; }

    public string? UserDisplayName { get; set; }

    public int? ConfigPermission { get; set; }

    public string? PermissionName { get; set; }

    public int? ConfigRole { get; set; }

    public string? RoleName { get; set; }

    public int ConfigPriority { get; set; }

    public string Key => $"{ConfigCategory}/{ConfigSetting}";
}

public sealed class ConfigSettingValueEditData
{
    public int ConfigId { get; set; }

    public int ConfigSettingId { get; set; }

    public string? ConfigValue { get; set; }

    public int? ConfigUsr { get; set; }

    public int? ConfigPermission { get; set; }

    public int? ConfigRole { get; set; }

    public int ConfigPriority { get; set; }
}
