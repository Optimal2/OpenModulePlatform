// File: OpenModulePlatform.Service.ExampleServiceAppModule/Services/AppInstanceRepository.cs
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Service.ExampleServiceAppModule.Services;

public sealed class AppInstanceRepository
{
    private readonly SqlConnectionFactory _db;

    public AppInstanceRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public sealed record AppInstanceRuntime(
        Guid AppInstanceId,
        Guid? HostId,
        int AppId,
        string AppKey,
        bool IsAllowed,
        byte DesiredState,
        int? ConfigId,
        string? ExpectedLogin,
        string? ExpectedClientHostName,
        string? ExpectedClientIp);

    public sealed record ObservedIdentity(string Login, string HostName, string? ClientIp);

    public async Task<AppInstanceRuntime?> GetRuntimeAsync(Guid appInstanceId, CancellationToken ct)
    {
        const string sql = @"
SELECT ai.AppInstanceId,
       ai.HostId,
       ai.AppId,
       a.AppKey,
       ai.IsAllowed,
       ai.DesiredState,
       ai.ConfigId,
       ai.ExpectedLogin,
       ai.ExpectedClientHostName,
       ai.ExpectedClientIp
FROM omp.AppInstances ai
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
WHERE ai.AppInstanceId = @appInstanceId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
            return null;

        return new AppInstanceRuntime(
            rdr.GetGuid(0),
            rdr.IsDBNull(1) ? null : rdr.GetGuid(1),
            rdr.GetInt32(2),
            rdr.GetString(3),
            rdr.GetBoolean(4),
            rdr.GetByte(5),
            rdr.IsDBNull(6) ? null : rdr.GetInt32(6),
            rdr.IsDBNull(7) ? null : rdr.GetString(7),
            rdr.IsDBNull(8) ? null : rdr.GetString(8),
            rdr.IsDBNull(9) ? null : rdr.GetString(9));
    }

    public async Task<ObservedIdentity?> HeartbeatAsync(Guid appInstanceId, CancellationToken ct)
    {
        const string sql = @"
DECLARE @login nvarchar(256) = ORIGINAL_LOGIN();
DECLARE @hostName nvarchar(128) = HOST_NAME();
DECLARE @clientIp nvarchar(64) = CONVERT(nvarchar(64), CONNECTIONPROPERTY('client_net_address'));
DECLARE @hostId uniqueidentifier;

SELECT @hostId = HostId
FROM omp.AppInstances
WHERE AppInstanceId = @appInstanceId;

UPDATE omp.AppInstances
SET LastSeenUtc = SYSUTCDATETIME(),
    LastLogin = @login,
    LastClientHostName = @hostName,
    LastClientIp = @clientIp,
    UpdatedUtc = SYSUTCDATETIME()
WHERE AppInstanceId = @appInstanceId;

IF @hostId IS NOT NULL
BEGIN
    UPDATE omp.Hosts
    SET LastSeenUtc = SYSUTCDATETIME(),
        UpdatedUtc = SYSUTCDATETIME()
    WHERE HostId = @hostId;
END

SELECT @login AS LoginName, @hostName AS ClientHostName, @clientIp AS ClientIp;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
            return null;

        return new ObservedIdentity(
            rdr.IsDBNull(0) ? string.Empty : rdr.GetString(0),
            rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1),
            rdr.IsDBNull(2) ? null : rdr.GetString(2));
    }
}
