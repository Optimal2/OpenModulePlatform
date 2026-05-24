// File: OpenModulePlatform.WorkerManager.WindowsService/Services/OmpWorkerRuntimeRepository.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.WorkerManager.WindowsService.Models;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

/// <summary>
/// Publishes observed worker runtime state back to OMP.
/// </summary>
public sealed class OmpWorkerRuntimeRepository
{
    private const string WorkerProcessHostExecutableName = "OpenModulePlatform.WorkerProcessHost.exe";

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

    public async Task<string?> ResolveWorkerProcessHostPathAsync(string hostKey, CancellationToken ct)
    {
        const string sql = @"
DECLARE @hostId uniqueidentifier;

SELECT @hostId = HostId
FROM omp.Hosts
WHERE HostKey = @hostKey
  AND IsEnabled = 1;

IF @hostId IS NULL
BEGIN
    SELECT TOP (0) CAST(NULL AS nvarchar(500)) AS LocalPath;
    RETURN;
END;

WITH HostRoles AS
(
    SELECT HostTemplateId
    FROM omp.HostDeploymentAssignments
    WHERE HostId = @hostId
      AND IsActive = 1
)
SELECT TOP (1)
    has.LocalPath
FROM omp.AppInstances ai
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
INNER JOIN omp.Artifacts ar ON ar.ArtifactId = ai.ArtifactId
INNER JOIN omp.HostArtifactStates has
    ON has.HostId = @hostId
   AND has.ArtifactId = ar.ArtifactId
   AND has.ProvisioningState = 2
WHERE
  (
      ai.HostId = @hostId
      OR (ai.HostId IS NULL AND ai.TargetHostTemplateId IS NULL)
      OR
      (
          ai.HostId IS NULL
          AND ai.TargetHostTemplateId IS NOT NULL
          AND EXISTS (SELECT 1 FROM HostRoles hr WHERE hr.HostTemplateId = ai.TargetHostTemplateId)
      )
  )
  AND a.AppKey = N'omp_workerprocesshost'
  AND a.IsEnabled = 1
  AND ai.IsEnabled = 1
  AND ai.IsAllowed = 1
  AND ai.DesiredState = 1
  AND ar.IsEnabled = 1
  AND ar.PackageType = N'worker-host'
  AND has.LocalPath IS NOT NULL
  AND LTRIM(RTRIM(has.LocalPath)) <> N''
ORDER BY
    CASE
        WHEN ai.HostId = @hostId THEN 0
        WHEN ai.TargetHostTemplateId IS NOT NULL THEN 1
        ELSE 2
    END,
    ai.SortOrder,
    ai.AppInstanceKey;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);

        var localPath = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return null;
        }

        return Path.Combine(localPath.Trim(), WorkerProcessHostExecutableName);
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

IF EXISTS (SELECT 1 FROM omp.WorkerInstances WHERE WorkerInstanceId = @workerInstanceId)
BEGIN
    MERGE omp.WorkerInstanceRuntimeStates AS target
    USING (SELECT @workerInstanceId AS WorkerInstanceId) AS source
    ON target.WorkerInstanceId = source.WorkerInstanceId
    WHEN MATCHED THEN
        UPDATE SET
            AppInstanceId = @appInstanceId,
            WorkerInstanceKey = @workerInstanceKey,
            RuntimeKind = @runtimeKind,
            WorkerTypeKey = @workerTypeKey,
            ObservedState = @observedState,
            ProcessId = @processId,
            StartedUtc = @startedUtc,
            LastSeenUtc = @lastSeenUtc,
            LastExitUtc = @lastExitUtc,
            LastExitCode = @lastExitCode,
            StatusMessage = @statusMessage,
            UpdatedUtc = @nowUtc
    WHEN NOT MATCHED THEN
        INSERT
        (
            WorkerInstanceId,
            AppInstanceId,
            WorkerInstanceKey,
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
            @workerInstanceId,
            @appInstanceId,
            @workerInstanceKey,
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
        cmd.Parameters.AddWithValue("@workerInstanceId", observation.WorkerInstanceId == Guid.Empty ? observation.AppInstanceId : observation.WorkerInstanceId);
        cmd.Parameters.AddWithValue("@workerInstanceKey", ToNullableStringValue(observation.WorkerInstanceKey, 150));
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

    private static object ToNullableStringValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DBNull.Value;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static object ToStatusMessageValue(string? statusMessage)
    {
        return ToNullableStringValue(statusMessage, 500);
    }
}
