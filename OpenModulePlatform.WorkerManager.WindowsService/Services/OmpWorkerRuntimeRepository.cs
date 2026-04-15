// File: OpenModulePlatform.WorkerManager.WindowsService/Services/OmpWorkerRuntimeRepository.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.WorkerManager.WindowsService.Models;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

/// <summary>
/// Publishes observed worker runtime state back to OMP.
/// </summary>
public sealed class OmpWorkerRuntimeRepository
{
    private readonly SqlConnectionFactory _db;

    public OmpWorkerRuntimeRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task TouchHostHeartbeatAsync(string hostKey, CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.Hosts
SET LastSeenUtc = SYSUTCDATETIME(),
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostKey = @hostKey;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PublishObservationAsync(
        WorkerRuntimeObservation observation,
        bool touchAppInstanceHeartbeat,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observation);

        const string sql = @"
DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();
DECLARE @login nvarchar(256) = ORIGINAL_LOGIN();
DECLARE @clientHostName nvarchar(128) = HOST_NAME();
DECLARE @clientIp nvarchar(64) = CONVERT(nvarchar(64), CONNECTIONPROPERTY('client_net_address'));
DECLARE @hostId uniqueidentifier;

SELECT @hostId = HostId
FROM omp.AppInstances
WHERE AppInstanceId = @appInstanceId;

IF EXISTS (SELECT 1 FROM omp.AppInstanceRuntimeStates WHERE AppInstanceId = @appInstanceId)
BEGIN
    UPDATE omp.AppInstanceRuntimeStates
    SET RuntimeKind = @runtimeKind,
        WorkerTypeKey = @workerTypeKey,
        ObservedState = @observedState,
        ProcessId = @processId,
        StartedUtc = @startedUtc,
        LastSeenUtc = @lastSeenUtc,
        LastExitUtc = @lastExitUtc,
        LastExitCode = @lastExitCode,
        StatusMessage = @statusMessage,
        UpdatedUtc = @nowUtc
    WHERE AppInstanceId = @appInstanceId;
END
ELSE
BEGIN
    INSERT INTO omp.AppInstanceRuntimeStates
    (
        AppInstanceId,
        RuntimeKind,
        WorkerTypeKey,
        ObservedState,
        ProcessId,
        StartedUtc,
        LastSeenUtc,
        LastExitUtc,
        LastExitCode,
        StatusMessage,
        CreatedUtc,
        UpdatedUtc
    )
    VALUES
    (
        @appInstanceId,
        @runtimeKind,
        @workerTypeKey,
        @observedState,
        @processId,
        @startedUtc,
        @lastSeenUtc,
        @lastExitUtc,
        @lastExitCode,
        @statusMessage,
        @nowUtc,
        @nowUtc
    );
END

IF @touchAppInstanceHeartbeat = 1
BEGIN
    UPDATE omp.AppInstances
    SET LastSeenUtc = COALESCE(@lastSeenUtc, @nowUtc),
        LastLogin = @login,
        LastClientHostName = @clientHostName,
        LastClientIp = @clientIp,
        UpdatedUtc = @nowUtc
    WHERE AppInstanceId = @appInstanceId;

    IF @hostId IS NOT NULL
    BEGIN
        UPDATE omp.Hosts
        SET LastSeenUtc = COALESCE(@lastSeenUtc, @nowUtc),
            UpdatedUtc = @nowUtc
        WHERE HostId = @hostId;
    END
END";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@appInstanceId", observation.AppInstanceId);
        cmd.Parameters.AddWithValue("@runtimeKind", observation.RuntimeKind.Trim());
        cmd.Parameters.AddWithValue("@workerTypeKey", observation.WorkerTypeKey.Trim());
        cmd.Parameters.AddWithValue("@observedState", observation.ObservedState);
        cmd.Parameters.AddWithValue("@processId", (object?)observation.ProcessId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@startedUtc", ToDbValue(observation.StartedUtc));
        cmd.Parameters.AddWithValue("@lastSeenUtc", ToDbValue(observation.LastSeenUtc));
        cmd.Parameters.AddWithValue("@lastExitUtc", ToDbValue(observation.LastExitUtc));
        cmd.Parameters.AddWithValue("@lastExitCode", (object?)observation.LastExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@statusMessage", ToStatusMessageValue(observation.StatusMessage));
        cmd.Parameters.AddWithValue("@touchAppInstanceHeartbeat", touchAppInstanceHeartbeat);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object ToDbValue(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.UtcDateTime : DBNull.Value;
    }

    private static object ToStatusMessageValue(string? statusMessage)
    {
        if (string.IsNullOrWhiteSpace(statusMessage))
        {
            return DBNull.Value;
        }

        var trimmed = statusMessage.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }
}
