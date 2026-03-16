// File: OpenModulePlatform.Web.ExampleWebAppModule/Services/ExampleWebAppModuleAdminRepository.cs
using OpenModulePlatform.Web.ExampleWebAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Web.ExampleWebAppModule.Services;

/// <summary>
/// Portal-facing repository for the simple web-only example module.
/// </summary>
public sealed class ExampleWebAppModuleAdminRepository
{
    private readonly SqlConnectionFactory _db;

    public const string ModuleKey = "example_webapp_module";
    public const string ModuleSchema = "omp_example_webapp_module";

    public ExampleWebAppModuleAdminRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<OverviewRow> GetOverviewAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT @moduleKey,
       @moduleSchema,
       COUNT(1)
FROM omp_example_webapp_module.Configurations
WHERE VersionNo = 0;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@moduleKey", ModuleKey);
        cmd.Parameters.AddWithValue("@moduleSchema", ModuleSchema);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        await rdr.ReadAsync(ct);

        return new OverviewRow
        {
            ModuleKey = rdr.GetString(0),
            SchemaName = rdr.GetString(1),
            ActiveConfigurationCount = rdr.GetInt32(2)
        };
    }

    public async Task<IReadOnlyList<ConfigurationRow>> GetConfigurationsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT ConfigId,
       VersionNo,
       ConfigJson,
       Comment,
       CreatedUtc,
       CreatedBy
FROM omp_example_webapp_module.Configurations
WHERE VersionNo = 0
ORDER BY ConfigId DESC;";

        var rows = new List<ConfigurationRow>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ConfigurationRow
            {
                ConfigId = rdr.GetInt32(0),
                VersionNo = rdr.GetInt32(1),
                ConfigJson = rdr.GetString(2),
                Comment = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                CreatedUtc = rdr.GetDateTime(4),
                CreatedBy = rdr.IsDBNull(5) ? null : rdr.GetString(5)
            });
        }

        return rows;
    }

    public async Task<ConfigurationRow?> GetConfigurationAsync(int configId, CancellationToken ct)
    {
        const string sql = @"
SELECT ConfigId,
       VersionNo,
       ConfigJson,
       Comment,
       CreatedUtc,
       CreatedBy
FROM omp_example_webapp_module.Configurations
WHERE ConfigId = @configId
  AND VersionNo = 0;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@configId", configId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ConfigurationRow
        {
            ConfigId = rdr.GetInt32(0),
            VersionNo = rdr.GetInt32(1),
            ConfigJson = rdr.GetString(2),
            Comment = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            CreatedUtc = rdr.GetDateTime(4),
            CreatedBy = rdr.IsDBNull(5) ? null : rdr.GetString(5)
        };
    }

    public async Task UpdateConfigurationAsync(
        int configId,
        string configJson,
        string? comment,
        string actor,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp_example_webapp_module.Configurations
SET ConfigJson = @configJson,
    Comment = @comment,
    CreatedBy = @actor,
    CreatedUtc = SYSUTCDATETIME()
WHERE ConfigId = @configId
  AND VersionNo = 0;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@configId", configId);
        cmd.Parameters.AddWithValue("@configJson", configJson);
        cmd.Parameters.AddWithValue("@comment", (object?)comment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@actor", actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
