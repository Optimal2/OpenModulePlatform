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

            return new PortalReadinessResult(
                IsHealthy: databaseOk,
                MachineName: Environment.MachineName,
                DatabaseOk: databaseOk,
                CheckedUtc: checkedUtc,
                Error: databaseOk ? null : "Database probe returned an unexpected value.");
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
