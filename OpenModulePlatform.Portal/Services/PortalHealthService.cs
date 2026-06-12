using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Services;

public sealed class PortalHealthService
{
    private readonly SqlConnectionFactory _db;
    private readonly ILogger<PortalHealthService> _logger;

    public PortalHealthService(SqlConnectionFactory db, ILogger<PortalHealthService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public PortalLivenessResult CheckLive()
        => new(
            IsHealthy: true,
            MachineName: Environment.MachineName,
            CheckedUtc: DateTime.UtcNow);

    public async Task<PortalReadinessResult> CheckReadyAsync(CancellationToken ct)
    {
        var checkedUtc = DateTime.UtcNow;
        try
        {
            await using var conn = _db.Create();
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT 1;", conn);
            cmd.CommandTimeout = 5;
            var value = await cmd.ExecuteScalarAsync(ct);
            var databaseOk = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 1;
            var profileImageSchemaOk = false;
            if (databaseOk)
            {
                const string requiredSchemaSql = @"
SELECT CASE
    WHEN OBJECT_ID(N'omp.users', N'U') IS NOT NULL
     AND COL_LENGTH(N'omp.users', N'profile_image_file_name') IS NOT NULL
     AND COL_LENGTH(N'omp.users', N'profile_image_storage_key') IS NOT NULL
    THEN 1 ELSE 0 END;";

                await using var schemaCmd = new SqlCommand(requiredSchemaSql, conn);
                schemaCmd.CommandTimeout = 5;
                var schemaValue = await schemaCmd.ExecuteScalarAsync(ct);
                profileImageSchemaOk = Convert.ToInt32(schemaValue, System.Globalization.CultureInfo.InvariantCulture) == 1;
            }

            var isHealthy = databaseOk && profileImageSchemaOk;

            return new PortalReadinessResult(
                IsHealthy: isHealthy,
                MachineName: Environment.MachineName,
                DatabaseOk: databaseOk,
                CheckedUtc: checkedUtc,
                Error: isHealthy
                    ? null
                    : databaseOk
                        ? "Required core profile image schema is missing. Run SQL repair for the omp_core module."
                        : "Database probe returned an unexpected value.");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            _logger.LogWarning(ex, "Portal readiness probe failed.");
            return new PortalReadinessResult(
                IsHealthy: false,
                MachineName: Environment.MachineName,
                DatabaseOk: false,
                CheckedUtc: checkedUtc,
                Error: ex.Message);
        }
    }
}

public sealed record PortalLivenessResult(
    bool IsHealthy,
    string MachineName,
    DateTime CheckedUtc);

public sealed record PortalReadinessResult(
    bool IsHealthy,
    string MachineName,
    bool DatabaseOk,
    DateTime CheckedUtc,
    string? Error);
