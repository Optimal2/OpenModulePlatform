// File: OpenModulePlatform.Service.ExampleServiceAppModule/Services/HostInstallationRepository.cs
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Service.ExampleServiceAppModule.Services;

public sealed class HostInstallationRepository
{
    private readonly SqlConnectionFactory _db;

    public HostInstallationRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public sealed record HostInstallationRuntime(
        Guid HostInstallationId,
        Guid HostId,
        int AppId,
        string AppKey,
        bool IsAllowed,
        byte DesiredState,
        int? ConfigId,
        string ExpectedLogin,
        string? ExpectedHostName,
        string? ExpectedClientIp);

    public sealed record ObservedIdentity(string Login, string HostName, string? ClientIp);

    public async Task<HostInstallationRuntime?> GetRuntimeAsync(Guid hostInstallationId, CancellationToken ct)
    {
        const string sql = @"
SELECT hi.HostInstallationId,
       hi.HostId,
       hi.AppId,
       a.AppKey,
       hi.IsAllowed,
       hi.DesiredState,
       hi.ConfigId,
       h.ExpectedLogin,
       h.ExpectedHostName,
       h.ExpectedClientIp
FROM omp.HostInstallations hi
INNER JOIN omp.Hosts h ON h.HostId = hi.HostId
INNER JOIN omp.Apps a ON a.AppId = hi.AppId
WHERE hi.HostInstallationId = @hostInstallationId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostInstallationId", hostInstallationId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
            return null;

        return new HostInstallationRuntime(
            rdr.GetGuid(0),
            rdr.GetGuid(1),
            rdr.GetInt32(2),
            rdr.GetString(3),
            rdr.GetBoolean(4),
            rdr.GetByte(5),
            rdr.IsDBNull(6) ? null : rdr.GetInt32(6),
            rdr.IsDBNull(7) ? string.Empty : rdr.GetString(7),
            rdr.IsDBNull(8) ? null : rdr.GetString(8),
            rdr.IsDBNull(9) ? null : rdr.GetString(9));
    }

    public async Task<ObservedIdentity?> HeartbeatAsync(Guid hostInstallationId, CancellationToken ct)
    {
        const string sql = @"
DECLARE @login nvarchar(256) = ORIGINAL_LOGIN();
DECLARE @hostName nvarchar(128) = HOST_NAME();
DECLARE @clientIp nvarchar(64) = CONVERT(nvarchar(64), CONNECTIONPROPERTY('client_net_address'));

UPDATE omp.HostInstallations
SET LastSeenUtc = SYSUTCDATETIME(),
    LastLogin = @login,
    LastClientHostName = @hostName,
    LastClientIp = @clientIp,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostInstallationId = @hostInstallationId;

UPDATE h
SET LastSeenUtc = SYSUTCDATETIME(),
    UpdatedUtc = SYSUTCDATETIME()
FROM omp.Hosts h
INNER JOIN omp.HostInstallations hi ON hi.HostId = h.HostId
WHERE hi.HostInstallationId = @hostInstallationId;

SELECT @login AS LoginName, @hostName AS ClientHostName, @clientIp AS ClientIp;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostInstallationId", hostInstallationId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
            return null;

        return new ObservedIdentity(
            rdr.IsDBNull(0) ? string.Empty : rdr.GetString(0),
            rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1),
            rdr.IsDBNull(2) ? null : rdr.GetString(2));
    }
}
