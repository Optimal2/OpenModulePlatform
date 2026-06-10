using System.Data;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed partial class OmpHostArtifactRepository
{
    public async Task<WebAppHealthProbeResult> UpsertWebAppHealthStateAsync(
        Guid hostId,
        WebAppHealthProbeResult probe,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.WebAppHealthStates', N'U') IS NULL
BEGIN
    SELECT @Status AS Status,
           CASE WHEN @Status = @HealthyStatus THEN 0 ELSE 1 END AS ConsecutiveFailures,
           CAST(NULL AS datetime2(3)) AS LastActionUtc;
    RETURN;
END;

DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();

MERGE omp.WebAppHealthStates WITH (HOLDLOCK) AS target
USING
(
    SELECT @HostId AS HostId,
           @HealthKey AS HealthKey
) AS source
ON target.HostId = source.HostId
AND target.HealthKey = source.HealthKey
WHEN MATCHED THEN
    UPDATE SET
        AppInstanceId = NULL,
        AppKey = @AppKey,
        DisplayName = @DisplayName,
        ProbeUrl = @ProbeUrl,
        AppPoolName = @AppPoolName,
        Status = @Status,
        HttpStatusCode = @HttpStatusCode,
        ConsecutiveFailures = CASE WHEN @Status = @HealthyStatus THEN 0 ELSE target.ConsecutiveFailures + 1 END,
        LastProbeUtc = @nowUtc,
        LastSuccessUtc = CASE WHEN @Status = @HealthyStatus THEN @nowUtc ELSE target.LastSuccessUtc END,
        LastFailureUtc = CASE WHEN @Status = @HealthyStatus THEN target.LastFailureUtc ELSE @nowUtc END,
        ResponseSummary = @ResponseSummary,
        LastError = @LastError,
        UpdatedUtc = @nowUtc
WHEN NOT MATCHED THEN
    INSERT
    (
        HostId,
        HealthKey,
        AppInstanceId,
        AppKey,
        DisplayName,
        ProbeUrl,
        AppPoolName,
        Status,
        HttpStatusCode,
        ConsecutiveFailures,
        LastProbeUtc,
        LastSuccessUtc,
        LastFailureUtc,
        ResponseSummary,
        LastError,
        CreatedUtc,
        UpdatedUtc
    )
    VALUES
    (
        @HostId,
        @HealthKey,
        NULL,
        @AppKey,
        @DisplayName,
        @ProbeUrl,
        @AppPoolName,
        @Status,
        @HttpStatusCode,
        CASE WHEN @Status = @HealthyStatus THEN 0 ELSE 1 END,
        @nowUtc,
        CASE WHEN @Status = @HealthyStatus THEN @nowUtc ELSE NULL END,
        CASE WHEN @Status = @HealthyStatus THEN NULL ELSE @nowUtc END,
        @ResponseSummary,
        @LastError,
        @nowUtc,
        @nowUtc
    );

SELECT Status,
       ConsecutiveFailures,
       LastActionUtc
FROM omp.WebAppHealthStates
WHERE HostId = @HostId
  AND HealthKey = @HealthKey;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@HostId", SqlDbType.UniqueIdentifier, hostId);
        Add(cmd, "@HealthKey", SqlDbType.NVarChar, 200, Truncate(probe.HealthKey, 200));
        Add(cmd, "@AppKey", SqlDbType.NVarChar, 200, "omp_portal");
        Add(cmd, "@DisplayName", SqlDbType.NVarChar, 200, Truncate(probe.DisplayName, 200));
        Add(cmd, "@ProbeUrl", SqlDbType.NVarChar, 1000, Truncate(probe.ProbeUrl, 1000));
        Add(cmd, "@AppPoolName", SqlDbType.NVarChar, 200, Truncate(probe.AppPoolName, 200));
        Add(cmd, "@Status", SqlDbType.TinyInt, probe.Status);
        Add(cmd, "@HealthyStatus", SqlDbType.TinyInt, WebAppHealthStatuses.Healthy);
        Add(cmd, "@HttpStatusCode", SqlDbType.Int, probe.HttpStatusCode);
        Add(cmd, "@ResponseSummary", SqlDbType.NVarChar, 1000, Truncate(probe.ResponseSummary ?? string.Empty, 1000));
        Add(cmd, "@LastError", SqlDbType.NVarChar, 4000, Truncate(probe.Error ?? string.Empty, StoredDiagnosticMessageMaxLength));

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (await rdr.ReadAsync(ct))
        {
            probe.Status = rdr.GetByte(0);
            probe.ConsecutiveFailures = rdr.GetInt32(1);
            probe.LastActionUtc = rdr.IsDBNull(2) ? null : rdr.GetDateTime(2);
        }

        return probe;
    }

    public async Task RecordWebAppHealthActionAsync(
        Guid hostId,
        string healthKey,
        string message,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.WebAppHealthStates', N'U') IS NULL
BEGIN
    RETURN;
END;

UPDATE omp.WebAppHealthStates
SET LastActionUtc = SYSUTCDATETIME(),
    LastActionMessage = @Message,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostId = @HostId
  AND HealthKey = @HealthKey;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@HostId", SqlDbType.UniqueIdentifier, hostId);
        Add(cmd, "@HealthKey", SqlDbType.NVarChar, 200, Truncate(healthKey, 200));
        Add(cmd, "@Message", SqlDbType.NVarChar, 1000, Truncate(message, 1000));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
