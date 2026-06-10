using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;
using System.Text.Json;

namespace OpenModulePlatform.Portal.Services;

public sealed partial class OmpAdminRepository
{
    public async Task<IReadOnlyList<WebAppHealthStateRow>> GetWebAppHealthStatesAsync(CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.WebAppHealthStates', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS nvarchar(128)) AS HostKey,
        CAST(NULL AS nvarchar(200)) AS HostDisplayName,
        CAST(NULL AS nvarchar(200)) AS HealthKey,
        CAST(NULL AS nvarchar(200)) AS AppKey,
        CAST(NULL AS nvarchar(200)) AS DisplayName,
        CAST(NULL AS nvarchar(1000)) AS ProbeUrl,
        CAST(NULL AS nvarchar(200)) AS AppPoolName,
        CAST(NULL AS tinyint) AS Status,
        CAST(NULL AS int) AS HttpStatusCode,
        CAST(NULL AS int) AS ConsecutiveFailures,
        CAST(NULL AS datetime2(3)) AS LastProbeUtc,
        CAST(NULL AS datetime2(3)) AS LastSuccessUtc,
        CAST(NULL AS datetime2(3)) AS LastFailureUtc,
        CAST(NULL AS datetime2(3)) AS LastActionUtc,
        CAST(NULL AS nvarchar(1000)) AS LastActionMessage,
        CAST(NULL AS nvarchar(1000)) AS ResponseSummary,
        CAST(NULL AS nvarchar(4000)) AS LastError;
    RETURN;
END;

SELECT host.HostId,
       host.HostKey,
       host.DisplayName AS HostDisplayName,
       COALESCE(state.HealthKey, N'portal') AS HealthKey,
       state.AppKey,
       COALESCE(state.DisplayName, N'OMP Portal') AS DisplayName,
       state.ProbeUrl,
       state.AppPoolName,
       COALESCE(state.Status, CAST(0 AS tinyint)) AS Status,
       state.HttpStatusCode,
       COALESCE(state.ConsecutiveFailures, 0) AS ConsecutiveFailures,
       state.LastProbeUtc,
       state.LastSuccessUtc,
       state.LastFailureUtc,
       state.LastActionUtc,
       state.LastActionMessage,
       state.ResponseSummary,
       state.LastError
FROM omp.Hosts host
LEFT JOIN omp.WebAppHealthStates state
    ON state.HostId = host.HostId
   AND state.HealthKey = N'portal'
WHERE host.IsEnabled = 1
ORDER BY CASE COALESCE(state.Status, CAST(0 AS tinyint)) WHEN 3 THEN 0 WHEN 2 THEN 1 WHEN 0 THEN 2 ELSE 3 END,
         host.HostKey,
         COALESCE(state.HealthKey, N'portal');";

        var rows = new List<WebAppHealthStateRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new WebAppHealthStateRow
            {
                HostId = rdr.GetGuid(0),
                HostKey = rdr.GetString(1),
                HostDisplayName = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                HealthKey = rdr.GetString(3),
                AppKey = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                DisplayName = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                ProbeUrl = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                AppPoolName = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                Status = rdr.GetByte(8),
                HttpStatusCode = rdr.IsDBNull(9) ? null : rdr.GetInt32(9),
                ConsecutiveFailures = rdr.GetInt32(10),
                LastProbeUtc = rdr.IsDBNull(11) ? null : rdr.GetDateTime(11),
                LastSuccessUtc = rdr.IsDBNull(12) ? null : rdr.GetDateTime(12),
                LastFailureUtc = rdr.IsDBNull(13) ? null : rdr.GetDateTime(13),
                LastActionUtc = rdr.IsDBNull(14) ? null : rdr.GetDateTime(14),
                LastActionMessage = rdr.IsDBNull(15) ? null : rdr.GetString(15),
                ResponseSummary = rdr.IsDBNull(16) ? null : rdr.GetString(16),
                LastError = rdr.IsDBNull(17) ? null : rdr.GetString(17)
            });
        }

        return rows;
    }

    public Task<long> QueueWebAppHealthProbeAsync(
        Guid hostId,
        string healthKey,
        bool recycleIfUnhealthy,
        string? requestedBy,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            healthKey = string.IsNullOrWhiteSpace(healthKey) ? "portal" : healthKey.Trim(),
            recycleIfUnhealthy
        });

        return QueueHostAgentJobAsync(
            hostId,
            "WebAppHealthProbe",
            payload,
            requestedBy,
            ct);
    }

    public Task<long> QueueWebAppAppPoolRecycleAsync(
        Guid hostId,
        string healthKey,
        string? appPoolName,
        string? requestedBy,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            healthKey = string.IsNullOrWhiteSpace(healthKey) ? "portal" : healthKey.Trim(),
            appPoolName = appPoolName?.Trim() ?? string.Empty
        });

        return QueueHostAgentJobAsync(
            hostId,
            "RecycleWebAppAppPool",
            payload,
            requestedBy,
            ct);
    }

    public Task<long> QueueWebAppLogCollectionAsync(
        Guid hostId,
        string healthKey,
        string? requestedBy,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            healthKey = string.IsNullOrWhiteSpace(healthKey) ? "portal" : healthKey.Trim(),
            maxLines = 120
        });

        return QueueHostAgentJobAsync(
            hostId,
            "CollectWebAppLogs",
            payload,
            requestedBy,
            ct);
    }

    private async Task<long> QueueHostAgentJobAsync(
        Guid hostId,
        string jobType,
        string payloadJson,
        string? requestedBy,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.HostAgentJobs', N'U') IS NULL
BEGIN
    THROW 51000, 'HostAgent job queue is not available. Apply the core OMP schema before queueing HostAgent jobs.', 1;
END;

INSERT INTO omp.HostAgentJobs
(
    HostId,
    JobType,
    PayloadJson,
    Status,
    RequestedBy,
    MaxAttempts
)
OUTPUT inserted.HostAgentJobId
VALUES
(
    @HostId,
    @JobType,
    @PayloadJson,
    CAST(0 AS tinyint),
    @RequestedBy,
    3
);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@HostId", hostId);
        Add(cmd, "@JobType", jobType);
        Add(cmd, "@PayloadJson", payloadJson);
        Add(cmd, "@RequestedBy", string.IsNullOrWhiteSpace(requestedBy) ? DBNull.Value : requestedBy.Trim());

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
