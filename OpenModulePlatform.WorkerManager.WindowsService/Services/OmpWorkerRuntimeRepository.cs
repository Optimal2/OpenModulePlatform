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

        return Path.Join(localPath.Trim(), WorkerProcessHostExecutableName);
    }

    /// <summary>
    /// Recomputes omp.AppInstanceRuntimeStates for @appInstanceId as an aggregation of the
    /// per-instance rows in omp.WorkerInstanceRuntimeStates (R12-D1).
    /// </summary>
    /// <remarks>
    /// The app-instance row used to be written directly by whichever worker published last.
    /// With six worker instances under one app instance -- measured on LINUS-LAPTOP:
    /// 7 rows in the per-instance table against 2 in this one -- that made the summary a
    /// coin toss, and a worker stuck in Failed(5) stayed invisible for as long as any
    /// sibling reported Running. The summary is now the WORST sibling, not the newest one,
    /// and its LastSeenUtc is the OLDEST sibling's so the row can never look fresher than
    /// its stalest member. Both callers (publish and the staleness downgrade) share this
    /// one definition; two copies of an aggregation rule is how the two states drift apart.
    /// </remarks>
    private const string AppInstanceSummarySql = @"
-- Severity order, worst first: Failed(5), Unknown(0), Stopped(4), Stopping(3),
-- Starting(1), Running(2). See WorkerObservedStates.
DECLARE @siblingCount int = 0;
DECLARE @nullSeenCount int = 0;
DECLARE @aggRuntimeKind nvarchar(100);
DECLARE @aggWorkerTypeKey nvarchar(200);
DECLARE @aggObservedState tinyint;
DECLARE @aggProcessId int;
DECLARE @aggStartedUtc datetime2(3);
DECLARE @aggLastSeenUtc datetime2(3);
DECLARE @aggLastExitUtc datetime2(3);
DECLARE @aggLastExitCode int;
DECLARE @aggStatusMessage nvarchar(500);

SELECT @siblingCount = COUNT(1),
       @nullSeenCount = SUM(CASE WHEN s.LastSeenUtc IS NULL THEN 1 ELSE 0 END),
       @aggLastSeenUtc = MIN(s.LastSeenUtc)
FROM omp.WorkerInstanceRuntimeStates s
INNER JOIN omp.WorkerInstances wi
    ON wi.WorkerInstanceId = s.WorkerInstanceId
   AND wi.IsEnabled = 1 AND wi.IsAllowed = 1 AND wi.DesiredState = 1
WHERE s.AppInstanceId = @appInstanceId;

IF @siblingCount > 0
BEGIN
    SELECT TOP (1)
        @aggRuntimeKind = s.RuntimeKind,
        @aggWorkerTypeKey = s.WorkerTypeKey,
        @aggObservedState = s.ObservedState,
        @aggProcessId = s.ProcessId,
        @aggStartedUtc = s.StartedUtc,
        @aggLastExitUtc = s.LastExitUtc,
        @aggLastExitCode = s.LastExitCode,
        @aggStatusMessage = s.StatusMessage
    FROM omp.WorkerInstanceRuntimeStates s
    INNER JOIN omp.WorkerInstances wi
        ON wi.WorkerInstanceId = s.WorkerInstanceId
       AND wi.IsEnabled = 1 AND wi.IsAllowed = 1 AND wi.DesiredState = 1
    WHERE s.AppInstanceId = @appInstanceId
    ORDER BY CASE s.ObservedState
                 WHEN 5 THEN 0
                 WHEN 0 THEN 1
                 WHEN 4 THEN 2
                 WHEN 3 THEN 3
                 WHEN 1 THEN 4
                 WHEN 2 THEN 5
                 ELSE 6
             END,
             -- Tie-break on the stalest report, then on the id, so the same set of
             -- siblings always produces the same summary regardless of write order.
             COALESCE(s.LastSeenUtc, CAST(N'1900-01-01' AS datetime2(3))),
             s.WorkerInstanceId;

    -- A sibling that has never reported at all must not be averaged away by one that
    -- has: any NULL makes the summary NULL rather than the oldest non-NULL value.
    IF @nullSeenCount > 0
    BEGIN
        SET @aggLastSeenUtc = NULL;
    END

    IF @siblingCount > 1
    BEGIN
        SET @aggStatusMessage = LEFT(
            ISNULL(@aggStatusMessage, N'') + N' (worst of ' + CAST(@siblingCount AS nvarchar(10)) + N' worker instances)',
            500);
    END

    IF EXISTS (SELECT 1 FROM omp.AppInstanceRuntimeStates WHERE AppInstanceId = @appInstanceId)
    BEGIN
        UPDATE omp.AppInstanceRuntimeStates
        SET RuntimeKind = @aggRuntimeKind,
            WorkerTypeKey = @aggWorkerTypeKey,
            ObservedState = @aggObservedState,
            ProcessId = @aggProcessId,
            StartedUtc = @aggStartedUtc,
            LastSeenUtc = @aggLastSeenUtc,
            LastExitUtc = @aggLastExitUtc,
            LastExitCode = @aggLastExitCode,
            StatusMessage = @aggStatusMessage,
            UpdatedUtc = @nowUtc
        WHERE AppInstanceId = @appInstanceId;
    END
    ELSE
    BEGIN
        INSERT INTO omp.AppInstanceRuntimeStates
        (
            AppInstanceId, RuntimeKind, WorkerTypeKey, ObservedState, ProcessId,
            StartedUtc, LastSeenUtc, LastExitUtc, LastExitCode, StatusMessage,
            CreatedUtc, UpdatedUtc
        )
        VALUES
        (
            @appInstanceId, @aggRuntimeKind, @aggWorkerTypeKey, @aggObservedState, @aggProcessId,
            @aggStartedUtc, @aggLastSeenUtc, @aggLastExitUtc, @aggLastExitCode, @aggStatusMessage,
            @nowUtc, @nowUtc
        );
    END
END
";

    public async Task PublishObservationAsync(
        WorkerRuntimeObservation observation,
        bool touchAppInstanceHeartbeat,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observation);

        const string sql = @"
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;

DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();
DECLARE @login nvarchar(256) = ORIGINAL_LOGIN();
DECLARE @clientHostName nvarchar(128) = HOST_NAME();
DECLARE @clientIp nvarchar(64) = CONVERT(nvarchar(64), CONNECTIONPROPERTY('client_net_address'));
DECLARE @hostId uniqueidentifier;

-- R12-D1. The per-instance write and the summary recomputation have to be one step:
-- without it two workers under the same app instance can interleave read and write and
-- leave a summary that matches neither. Scoped to the app instance so unrelated apps
-- never wait on each other, and taken before any write so the lock order is the same on
-- every path through this file.
DECLARE @lockResource nvarchar(255) = N'omp.AppInstanceRuntimeStates:' + CONVERT(nvarchar(50), @appInstanceId);
DECLARE @lockResult int;
EXEC @lockResult = sp_getapplock
    @Resource = @lockResource,
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 5000;
IF @lockResult < 0
BEGIN
    ROLLBACK TRANSACTION;
    THROW 51100, N'Could not acquire the app-instance runtime state lock.', 1;
END

SELECT @hostId = HostId
FROM omp.AppInstances
WHERE AppInstanceId = @appInstanceId;

IF EXISTS (SELECT 1 FROM omp.WorkerInstances WHERE WorkerInstanceId = @workerInstanceId)
BEGIN
    MERGE omp.WorkerInstanceRuntimeStates WITH (HOLDLOCK) AS target
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
"
            + AppInstanceSummarySql
            + @"
-- Not every runtime observation belongs to a catalogued worker instance: a manually
-- created runtime app instance, or an observation whose WorkerInstanceId falls back to
-- the AppInstanceId, leaves the per-instance table empty and the aggregation above with
-- nothing to aggregate. Writing the observation straight to the summary keeps those
-- callers working exactly as before -- an aggregation that silently drops the only
-- report it had would be a worse bug than the one being fixed.
IF NOT EXISTS
(
    SELECT 1
    FROM omp.WorkerInstanceRuntimeStates s
    INNER JOIN omp.WorkerInstances wi
        ON wi.WorkerInstanceId = s.WorkerInstanceId
       AND wi.IsEnabled = 1 AND wi.IsAllowed = 1 AND wi.DesiredState = 1
    WHERE s.AppInstanceId = @appInstanceId
)
BEGIN
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
            AppInstanceId, RuntimeKind, WorkerTypeKey, ObservedState, ProcessId,
            StartedUtc, LastSeenUtc, LastExitUtc, LastExitCode, StatusMessage,
            CreatedUtc, UpdatedUtc
        )
        VALUES
        (
            @appInstanceId, @runtimeKind, @workerTypeKey, @observedState, @processId,
            @startedUtc, @lastSeenUtc, @lastExitUtc, @lastExitCode, @statusMessage,
            @nowUtc, @nowUtc
        );
    END
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
END

COMMIT TRANSACTION;";

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
        // R8-P3-12: bound explicitly rather than through AddWithValue. AddWithValue infers
        // datetime2 at the default scale of 7 for a DateTime, and the three target columns are
        // datetime2(3), so SQL Server rounds on the way in. Every value written here is later read
        // back and compared, and the same latent mismatch is what surfaced in R7-F24 as soon as a
        // locking token started depending on an exact round trip.
        AddDateTime2(cmd, "@startedUtc", observation.StartedUtc);
        AddDateTime2(cmd, "@lastSeenUtc", observation.LastSeenUtc);
        AddDateTime2(cmd, "@lastExitUtc", observation.LastExitUtc);
        cmd.Parameters.AddWithValue("@lastExitCode", (object?)observation.LastExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@statusMessage", ToStatusMessageValue(observation.StatusMessage));
        cmd.Parameters.AddWithValue("@touchAppInstanceHeartbeat", touchAppInstanceHeartbeat);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Downgrades worker runtime rows on this host that still claim Starting or Running but
    /// have not been written for <paramref name="staleAfterSeconds"/>, and recomputes the
    /// app-instance summaries they feed. Returns the number of rows downgraded.
    /// </summary>
    /// <remarks>
    /// R12-D8/F10. Nothing downgraded a worker state: a WorkerManager killed hard (or a host
    /// that lost power) left every row reading Running forever, and both the Portal and the
    /// deployment gate reported a healthy worker that had not existed for days. The state a
    /// dead writer left behind is not evidence, and the only honest replacement is Unknown --
    /// not Failed, which would claim knowledge of an exit nobody observed.
    ///
    /// Staleness is measured on COALESCE(LastSeenUtc, UpdatedUtc), not LastSeenUtc alone: a
    /// Starting observation carries no LastSeenUtc, so keying on it would have downgraded
    /// every worker one cycle after it started -- the guard blocking the very path it exists
    /// to protect. UpdatedUtc moves on every write, which is exactly the "is the writer
    /// alive" question being asked.
    ///
    /// Staleness is also what makes this safe to run without owning the rows: a worker some
    /// other live manager is refreshing is never stale, so it is never touched.
    /// </remarks>
    public async Task<int> DowngradeStaleWorkerStatesAsync(
        string hostKey,
        int staleAfterSeconds,
        CancellationToken ct)
    {
        const string sql = @"
SET NOCOUNT ON;

DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();
DECLARE @hostId uniqueidentifier;

SELECT @hostId = HostId
FROM omp.Hosts
WHERE HostKey = @hostKey
  AND IsEnabled = 1;

DECLARE @stale TABLE (WorkerInstanceId uniqueidentifier, AppInstanceId uniqueidentifier);

IF @hostId IS NOT NULL
BEGIN
    INSERT INTO @stale (WorkerInstanceId, AppInstanceId)
    SELECT s.WorkerInstanceId, s.AppInstanceId
    FROM omp.WorkerInstanceRuntimeStates s
    INNER JOIN omp.WorkerInstances wi ON wi.WorkerInstanceId = s.WorkerInstanceId
    INNER JOIN omp.AppInstances ai ON ai.AppInstanceId = wi.AppInstanceId
    -- Starting(1) and Running(2) are the only claims a dead writer can leave behind that
    -- read as healthy. Stopped, Failed and Unknown already describe themselves correctly.
    WHERE s.ObservedState IN (1, 2)
      AND DATEDIFF(second, COALESCE(s.LastSeenUtc, s.UpdatedUtc), @nowUtc) > @staleAfterSeconds
      AND
      (
          wi.HostId = @hostId
          OR (wi.HostId IS NULL AND ai.HostId = @hostId)
          OR (wi.HostId IS NULL AND ai.HostId IS NULL AND ai.TargetHostTemplateId IS NULL)
          OR
          (
              wi.HostId IS NULL AND ai.HostId IS NULL AND ai.TargetHostTemplateId IS NOT NULL
              AND EXISTS
              (
                  SELECT 1
                  FROM omp.HostDeploymentAssignments hda
                  WHERE hda.HostId = @hostId
                    AND hda.IsActive = 1
                    AND hda.HostTemplateId = ai.TargetHostTemplateId
              )
          )
      );

    UPDATE s
    SET ObservedState = 0,
        ProcessId = NULL,
        StatusMessage = LEFT(
            N'No WorkerManager report for more than ' + CAST(@staleAfterSeconds AS nvarchar(10))
            + N' s; state downgraded to Unknown.', 500),
        UpdatedUtc = @nowUtc
    FROM omp.WorkerInstanceRuntimeStates s
    INNER JOIN @stale st ON st.WorkerInstanceId = s.WorkerInstanceId;
END

SELECT DISTINCT AppInstanceId FROM @stale;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var affectedAppInstances = new List<Guid>();
        await using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@hostKey", hostKey);
            cmd.Parameters.AddWithValue("@staleAfterSeconds", staleAfterSeconds);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                affectedAppInstances.Add(reader.GetGuid(0));
            }
        }

        foreach (var appInstanceId in affectedAppInstances)
        {
            await RecomputeAppInstanceSummaryAsync(conn, appInstanceId, ct);
        }

        return affectedAppInstances.Count;
    }

    /// <summary>
    /// Runs the shared app-instance aggregation for one app instance under the same
    /// app-scoped lock the publish path uses.
    /// </summary>
    private static async Task RecomputeAppInstanceSummaryAsync(
        SqlConnection conn,
        Guid appInstanceId,
        CancellationToken ct)
    {
        const string sql = @"
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;

DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();
DECLARE @lockResource nvarchar(255) = N'omp.AppInstanceRuntimeStates:' + CONVERT(nvarchar(50), @appInstanceId);
DECLARE @lockResult int;
EXEC @lockResult = sp_getapplock
    @Resource = @lockResource,
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 5000;
IF @lockResult < 0
BEGIN
    ROLLBACK TRANSACTION;
    THROW 51100, N'Could not acquire the app-instance runtime state lock.', 1;
END
"
            + AppInstanceSummarySql
            + @"
COMMIT TRANSACTION;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object ToDbValue(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.UtcDateTime : DBNull.Value;
    }

    /// <summary>
    /// Binds a UTC timestamp at the scale the target columns actually use (R8-P3-12).
    /// </summary>
    private static void AddDateTime2(SqlCommand cmd, string name, DateTimeOffset? value)
    {
        var parameter = cmd.Parameters.Add(name, System.Data.SqlDbType.DateTime2);
        parameter.Scale = 3;
        parameter.Value = ToDbValue(value);
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
