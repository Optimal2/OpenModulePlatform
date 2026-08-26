// File: OpenModulePlatform.WorkerManager.WindowsService/Services/OmpWorkerRuntimeRepository.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.WorkerManager.WindowsService.Models;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

/// <summary>
/// Publishes observed worker runtime state back to OMP.
/// </summary>
public sealed class OmpWorkerRuntimeRepository
{
    private const string WorkerProcessHostExecutableName = "OpenModulePlatform.WorkerProcessHost.exe";

    /// <summary>
    /// R12-F2. The runtime version witness columns are newer than the service that writes
    /// them, and a universal package can land the WorkerManager binary before the omp_core
    /// schema has been applied.
    /// </summary>
    /// <remarks>
    /// Without this probe that window turns every publish into an "invalid column name"
    /// failure: the runtime rows stop being refreshed, the staleness downgrade turns them
    /// all Unknown, and the deployment gate reports a host-wide worker outage caused
    /// entirely by the diagnostics. Degrading to the old column set instead keeps the
    /// service publishing and costs only the version witness during that window.
    ///
    /// The probe is re-run while the answer is no, so the witness starts working by itself
    /// once the migration lands -- a cached "no" would have needed a service restart, and
    /// nobody restarts a service for a column they cannot see is missing. The missing
    /// columns are logged, once, because a silently degraded witness is the defect this
    /// whole finding is about.
    /// </remarks>
    private const string RuntimeArtifactColumnProbeSql = @"
SELECT CASE
           WHEN COL_LENGTH(N'omp.WorkerInstanceRuntimeStates', N'RuntimeArtifactId') IS NULL
             OR COL_LENGTH(N'omp.WorkerInstanceRuntimeStates', N'RuntimeArtifactVersion') IS NULL
             OR COL_LENGTH(N'omp.WorkerInstanceRuntimeStates', N'RuntimeHostArtifactId') IS NULL
             OR COL_LENGTH(N'omp.WorkerInstanceRuntimeStates', N'RuntimeHostArtifactVersion') IS NULL
             OR COL_LENGTH(N'omp.AppInstanceRuntimeStates', N'RuntimeArtifactId') IS NULL
             OR COL_LENGTH(N'omp.AppInstanceRuntimeStates', N'RuntimeArtifactVersion') IS NULL
             OR COL_LENGTH(N'omp.AppInstanceRuntimeStates', N'RuntimeHostArtifactId') IS NULL
             OR COL_LENGTH(N'omp.AppInstanceRuntimeStates', N'RuntimeHostArtifactVersion') IS NULL
           THEN 0 ELSE 1
       END;";

    private readonly SqlConnectionFactory _db;
    private readonly ILogger<OmpWorkerRuntimeRepository> _logger;

    private volatile bool _hasRuntimeArtifactColumns;
    private volatile bool _missingRuntimeArtifactColumnsLogged;

    public OmpWorkerRuntimeRepository(SqlConnectionFactory db, ILogger<OmpWorkerRuntimeRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    private async Task<bool> HasRuntimeArtifactColumnsAsync(SqlConnection conn, CancellationToken ct)
    {
        if (_hasRuntimeArtifactColumns)
        {
            return true;
        }

        await using var cmd = new SqlCommand(RuntimeArtifactColumnProbeSql, conn);
        var present = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0) == 1;
        if (present)
        {
            _hasRuntimeArtifactColumns = true;
            return true;
        }

        if (!_missingRuntimeArtifactColumnsLogged)
        {
            _missingRuntimeArtifactColumnsLogged = true;
            _logger.LogWarning(
                "omp.WorkerInstanceRuntimeStates/omp.AppInstanceRuntimeStates have no RuntimeArtifactId/RuntimeArtifactVersion columns, so which artifact version each worker runs cannot be recorded. The omp_core schema migration has not been applied to this database yet; deployment diagnostics will report the running worker version as unknown until it is.");
        }

        return false;
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

    /// <summary>
    /// Resolves the provisioned WorkerProcessHost executable for this host together with the
    /// artifact it belongs to (R12-F2).
    /// </summary>
    public async Task<ResolvedWorkerProcessHost?> ResolveWorkerProcessHostAsync(string hostKey, CancellationToken ct)
    {
        const string sql = @"
DECLARE @hostId uniqueidentifier;

SELECT @hostId = HostId
FROM omp.Hosts
WHERE HostKey = @hostKey
  AND IsEnabled = 1;

IF @hostId IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS nvarchar(500)) AS LocalPath,
        CAST(NULL AS int) AS ArtifactId,
        CAST(NULL AS nvarchar(50)) AS Version;
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
    has.LocalPath,
    ar.ArtifactId,
    ar.Version
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

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        var localPath = rdr.IsDBNull(0) ? null : rdr.GetString(0);
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return null;
        }

        var artifactId = rdr.IsDBNull(1) ? (int?)null : rdr.GetInt32(1);
        var version = rdr.IsDBNull(2) ? null : rdr.GetString(2).Trim();

        return new ResolvedWorkerProcessHost(
            Path.Join(localPath.Trim(), WorkerProcessHostExecutableName),
            artifactId,
            version);
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
    ///
    /// R12-F2: the summary's artifact witness comes from the SAME sibling row every other
    /// summarised column comes from -- the worst one -- so the row tells one worker
    /// instance's whole story instead of a chimera assembled from several. Siblings that
    /// disagree about their version are visible in the per-instance table, which is what
    /// both diagnostics scripts read for workers.
    /// </remarks>
    private static string BuildAppInstanceSummarySql(bool hasRuntimeArtifactColumns)
    {
        var declare = hasRuntimeArtifactColumns
            ? @"
DECLARE @aggRuntimeArtifactId int;
DECLARE @aggRuntimeArtifactVersion nvarchar(50);
DECLARE @aggRuntimeHostArtifactId int;
DECLARE @aggRuntimeHostArtifactVersion nvarchar(50);"
            : string.Empty;
        var select = hasRuntimeArtifactColumns
            ? @",
        @aggRuntimeArtifactId = s.RuntimeArtifactId,
        @aggRuntimeArtifactVersion = s.RuntimeArtifactVersion,
        @aggRuntimeHostArtifactId = s.RuntimeHostArtifactId,
        @aggRuntimeHostArtifactVersion = s.RuntimeHostArtifactVersion"
            : string.Empty;
        var set = hasRuntimeArtifactColumns
            ? @",
            RuntimeArtifactId = @aggRuntimeArtifactId,
            RuntimeArtifactVersion = @aggRuntimeArtifactVersion,
            RuntimeHostArtifactId = @aggRuntimeHostArtifactId,
            RuntimeHostArtifactVersion = @aggRuntimeHostArtifactVersion"
            : string.Empty;
        var insertColumns = hasRuntimeArtifactColumns
            ? ", RuntimeArtifactId, RuntimeArtifactVersion, RuntimeHostArtifactId, RuntimeHostArtifactVersion"
            : string.Empty;
        var insertValues = hasRuntimeArtifactColumns
            ? ", @aggRuntimeArtifactId, @aggRuntimeArtifactVersion, @aggRuntimeHostArtifactId, @aggRuntimeHostArtifactVersion"
            : string.Empty;

        return AppInstanceSummarySqlTemplate
            .Replace("/*ARTIFACT_DECLARE*/", declare, StringComparison.Ordinal)
            .Replace("/*ARTIFACT_SELECT*/", select, StringComparison.Ordinal)
            .Replace("/*ARTIFACT_SET*/", set, StringComparison.Ordinal)
            .Replace("/*ARTIFACT_INSERT_COLUMNS*/", insertColumns, StringComparison.Ordinal)
            .Replace("/*ARTIFACT_INSERT_VALUES*/", insertValues, StringComparison.Ordinal);
    }

    private const string AppInstanceSummarySqlTemplate = @"
-- Severity order, worst first: Failed(5), Unknown(0), Stopped(4), Stopping(3),
-- Draining(6), Starting(1), Running(2). See WorkerObservedStates.
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
DECLARE @aggStatusMessage nvarchar(500);/*ARTIFACT_DECLARE*/

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
        @aggStatusMessage = s.StatusMessage/*ARTIFACT_SELECT*/
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
                 WHEN 6 THEN 4
                 WHEN 1 THEN 5
                 WHEN 2 THEN 6
                 ELSE 7
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
            StatusMessage = @aggStatusMessage/*ARTIFACT_SET*/,
            UpdatedUtc = @nowUtc
        WHERE AppInstanceId = @appInstanceId;
    END
    ELSE
    BEGIN
        INSERT INTO omp.AppInstanceRuntimeStates
        (
            AppInstanceId, RuntimeKind, WorkerTypeKey, ObservedState, ProcessId,
            StartedUtc, LastSeenUtc, LastExitUtc, LastExitCode, StatusMessage/*ARTIFACT_INSERT_COLUMNS*/,
            CreatedUtc, UpdatedUtc
        )
        VALUES
        (
            @appInstanceId, @aggRuntimeKind, @aggWorkerTypeKey, @aggObservedState, @aggProcessId,
            @aggStartedUtc, @aggLastSeenUtc, @aggLastExitUtc, @aggLastExitCode, @aggStatusMessage/*ARTIFACT_INSERT_VALUES*/,
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

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        // R12-F2. Probed before the statement is composed, not assumed: a WorkerManager
        // that lands ahead of the omp_core migration must keep publishing state.
        var hasArtifactColumns = await HasRuntimeArtifactColumnsAsync(conn, ct);
        var mergeSetFragment = hasArtifactColumns
            ? @",
            RuntimeArtifactId = @runtimeArtifactId,
            RuntimeArtifactVersion = @runtimeArtifactVersion,
            RuntimeHostArtifactId = @runtimeHostArtifactId,
            RuntimeHostArtifactVersion = @runtimeHostArtifactVersion"
            : string.Empty;
        var mergeColumnsFragment = hasArtifactColumns
            ? @",
            RuntimeArtifactId,
            RuntimeArtifactVersion,
            RuntimeHostArtifactId,
            RuntimeHostArtifactVersion"
            : string.Empty;
        var mergeValuesFragment = hasArtifactColumns
            ? @",
            @runtimeArtifactId,
            @runtimeArtifactVersion,
            @runtimeHostArtifactId,
            @runtimeHostArtifactVersion"
            : string.Empty;

        var sql = @"
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

-- R7-F4. BOTH foreign keys of omp.WorkerInstanceRuntimeStates are guarded, not just
-- the one the MERGE matches on: the row also writes AppInstanceId, and an observation
-- arriving after its app instance was deleted used to die on FK_..._AppInstance and
-- take the whole publish (and the caller's reconcile step) with it. A row whose
-- parent is gone is dropped, not written.
IF EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @appInstanceId)
   AND EXISTS (SELECT 1 FROM omp.WorkerInstances WHERE WorkerInstanceId = @workerInstanceId)
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
            StatusMessage = @statusMessage" + mergeSetFragment + @",
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
            StatusMessage" + mergeColumnsFragment + @",
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
            @statusMessage" + mergeValuesFragment + @",
            @nowUtc,
            @nowUtc
        );
END
"
            + BuildAppInstanceSummarySql(hasArtifactColumns)
            + @"
-- Not every runtime observation belongs to a catalogued worker instance: a manually
-- created runtime app instance, or an observation whose WorkerInstanceId falls back to
-- the AppInstanceId, leaves the per-instance table empty and the aggregation above with
-- nothing to aggregate. Writing the observation straight to the summary keeps those
-- callers working exactly as before -- an aggregation that silently drops the only
-- report it had would be a worse bug than the one being fixed.
-- R7-F4 applies here too: omp.AppInstanceRuntimeStates.AppInstanceId is itself an FK,
-- so this fallback write is guarded on the parent row the same way.
IF EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @appInstanceId)
   AND NOT EXISTS
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
            StatusMessage = @statusMessage" + mergeSetFragment + @",
            UpdatedUtc = @nowUtc
        WHERE AppInstanceId = @appInstanceId;
    END
    ELSE
    BEGIN
        INSERT INTO omp.AppInstanceRuntimeStates
        (
            AppInstanceId, RuntimeKind, WorkerTypeKey, ObservedState, ProcessId,
            StartedUtc, LastSeenUtc, LastExitUtc, LastExitCode, StatusMessage" + mergeColumnsFragment + @",
            CreatedUtc, UpdatedUtc
        )
        VALUES
        (
            @appInstanceId, @runtimeKind, @workerTypeKey, @observedState, @processId,
            @startedUtc, @lastSeenUtc, @lastExitUtc, @lastExitCode, @statusMessage" + mergeValuesFragment + @",
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
        if (hasArtifactColumns)
        {
            cmd.Parameters.AddWithValue("@runtimeArtifactId", (object?)observation.RuntimeArtifactId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@runtimeArtifactVersion", ToNullableStringValue(observation.RuntimeArtifactVersion, 50));
            cmd.Parameters.AddWithValue("@runtimeHostArtifactId", (object?)observation.RuntimeHostArtifactId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@runtimeHostArtifactVersion", ToNullableStringValue(observation.RuntimeHostArtifactVersion, 50));
        }

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
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var hasArtifactColumns = await HasRuntimeArtifactColumnsAsync(conn, ct);
        // R12-F2. The version claim has to die with the process it described. A row
        // downgraded to Unknown says "nobody is reporting on this worker any more"; leaving
        // RuntimeArtifactVersion behind would let a worker that has not existed for days go
        // on naming the version it runs, and a reader comparing that against the desired
        // version would find agreement -- exactly the false all-clear this finding is about.
        var clearArtifactFragment = hasArtifactColumns
            ? @",
        RuntimeArtifactId = NULL,
        RuntimeArtifactVersion = NULL,
        RuntimeHostArtifactId = NULL,
        RuntimeHostArtifactVersion = NULL"
            : string.Empty;

        var sql = @"
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
    -- Starting(1), Running(2) and Draining(6) are the only claims a dead writer can
    -- leave behind that read as alive. Stopped, Failed and Unknown already describe
    -- themselves correctly. Draining heartbeats exactly like Running, so it must die
    -- like Running when the writer dies mid-drain (R7-F7).
    WHERE s.ObservedState IN (1, 2, 6)
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
        ProcessId = NULL" + clearArtifactFragment + @",
        StatusMessage = LEFT(
            N'No WorkerManager report for more than ' + CAST(@staleAfterSeconds AS nvarchar(10))
            + N' s; state downgraded to Unknown.', 500),
        UpdatedUtc = @nowUtc
    FROM omp.WorkerInstanceRuntimeStates s
    INNER JOIN @stale st ON st.WorkerInstanceId = s.WorkerInstanceId;
END

SELECT DISTINCT AppInstanceId FROM @stale;";

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
            await RecomputeAppInstanceSummaryAsync(conn, appInstanceId, hasArtifactColumns, ct);
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
        bool hasRuntimeArtifactColumns,
        CancellationToken ct)
    {
        var sql = @"
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
            + BuildAppInstanceSummarySql(hasRuntimeArtifactColumns)
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
