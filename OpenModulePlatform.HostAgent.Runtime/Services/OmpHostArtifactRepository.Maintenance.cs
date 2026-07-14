using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed partial class OmpHostArtifactRepository
{
    public async Task UpsertMaintenanceFindingsAsync(
        IReadOnlyCollection<MaintenanceFindingUpsert> findings,
        long detectedByHostAgentJobId,
        CancellationToken ct)
    {
        if (findings.Count == 0)
        {
            return;
        }

        const string sql = @"
IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NULL
BEGIN
    THROW 51000, 'Maintenance findings are not available. Apply the core OMP schema before running maintenance scans.', 1;
END;

DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();

MERGE omp.MaintenanceFindings WITH (HOLDLOCK) AS target
USING
(
    SELECT @findingKey AS FindingKey
) AS source
ON target.FindingKey = source.FindingKey
WHEN MATCHED THEN
    UPDATE SET
        Scope = @scope,
        HostId = @hostId,
        Category = @category,
        TargetKind = @targetKind,
        TargetIdentifier = @targetIdentifier,
        Title = @title,
        Detail = @detail,
        RecommendedAction = @recommendedAction,
        SafetyNotes = @safetyNotes,
        ActionJson = @actionJson,
        Status = CASE
            WHEN target.Status IN (@cleanupQueuedStatus, @cleanedStatus, @skippedStatus) THEN target.Status
            ELSE @openStatus
        END,
        Severity = @severity,
        Confidence = @confidence,
        DetectedByHostAgentJobId = @detectedByHostAgentJobId,
        ResultMessage = CASE
            WHEN target.Status IN (@cleanupQueuedStatus, @cleanedStatus, @skippedStatus) THEN target.ResultMessage
            ELSE NULL
        END,
        LastSeenUtc = @nowUtc,
        UpdatedUtc = @nowUtc
WHEN NOT MATCHED THEN
    INSERT
    (
        FindingKey,
        Scope,
        HostId,
        Category,
        TargetKind,
        TargetIdentifier,
        Title,
        Detail,
        RecommendedAction,
        SafetyNotes,
        ActionJson,
        Status,
        Severity,
        Confidence,
        DetectedByHostAgentJobId,
        DetectedUtc,
        LastSeenUtc,
        UpdatedUtc
    )
    VALUES
    (
        @findingKey,
        @scope,
        @hostId,
        @category,
        @targetKind,
        @targetIdentifier,
        @title,
        @detail,
        @recommendedAction,
        @safetyNotes,
        @actionJson,
        @openStatus,
        @severity,
        @confidence,
        @detectedByHostAgentJobId,
        @nowUtc,
        @nowUtc,
        @nowUtc
    );";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            foreach (var finding in findings)
            {
                await using var cmd = new SqlCommand(sql, conn, tx);
                Add(cmd, "@findingKey", SqlDbType.NVarChar, 450, Truncate(finding.FindingKey, 450));
                Add(cmd, "@scope", SqlDbType.NVarChar, 20, Truncate(finding.Scope, 20));
                Add(cmd, "@hostId", SqlDbType.UniqueIdentifier, finding.HostId);
                Add(cmd, "@category", SqlDbType.NVarChar, 100, Truncate(finding.Category, 100));
                Add(cmd, "@targetKind", SqlDbType.NVarChar, 80, Truncate(finding.TargetKind, 80));
                Add(cmd, "@targetIdentifier", SqlDbType.NVarChar, 1000, Truncate(finding.TargetIdentifier, 1000));
                Add(cmd, "@title", SqlDbType.NVarChar, 300, Truncate(finding.Title, 300));
                Add(cmd, "@detail", SqlDbType.NVarChar, -1, NullIfWhiteSpace(finding.Detail));
                Add(cmd, "@recommendedAction", SqlDbType.NVarChar, 300, NullIfWhiteSpace(Truncate(finding.RecommendedAction ?? string.Empty, 300)));
                Add(cmd, "@safetyNotes", SqlDbType.NVarChar, -1, NullIfWhiteSpace(finding.SafetyNotes));
                Add(cmd, "@actionJson", SqlDbType.NVarChar, -1, NullIfWhiteSpace(finding.ActionJson));
                Add(cmd, "@severity", SqlDbType.TinyInt, Math.Clamp(finding.Severity, (byte)0, (byte)4));
                Add(cmd, "@confidence", SqlDbType.TinyInt, Math.Clamp(finding.Confidence, (byte)0, (byte)100));
                Add(cmd, "@detectedByHostAgentJobId", SqlDbType.BigInt, detectedByHostAgentJobId);
                Add(cmd, "@openStatus", SqlDbType.TinyInt, MaintenanceFindingStatuses.Open);
                Add(cmd, "@cleanupQueuedStatus", SqlDbType.TinyInt, MaintenanceFindingStatuses.CleanupQueued);
                Add(cmd, "@cleanedStatus", SqlDbType.TinyInt, MaintenanceFindingStatuses.Cleaned);
                Add(cmd, "@skippedStatus", SqlDbType.TinyInt, MaintenanceFindingStatuses.Skipped);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<bool> EnqueueMaintenanceScanJobAsync(
        string hostKey,
        string? requestedBy,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.HostAgentJobs', N'U') IS NULL
BEGIN
    SELECT CAST(0 AS bit) AS Enqueued;
    RETURN;
END;

DECLARE @hostId uniqueidentifier;

SELECT @hostId = HostId
FROM omp.Hosts
WHERE HostKey = @hostKey
  AND IsEnabled = 1;

IF @hostId IS NULL
BEGIN
    SELECT CAST(0 AS bit) AS Enqueued;
    RETURN;
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
VALUES
(
    @hostId,
    N'MaintenanceScan',
    @payloadJson,
    @pendingStatus,
    @requestedBy,
    3
);

SELECT CAST(1 AS bit) AS Enqueued;";

        var payloadJson = JsonSerializer.Serialize(
            new MaintenanceScanJobPayload { Scope = MaintenanceScanScopes.Host },
            JsonOptions);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@hostKey", SqlDbType.NVarChar, 128, hostKey);
        Add(cmd, "@payloadJson", SqlDbType.NVarChar, -1, payloadJson);
        Add(cmd, "@requestedBy", SqlDbType.NVarChar, 256, string.IsNullOrWhiteSpace(requestedBy) ? DBNull.Value : Truncate(requestedBy, 256));
        Add(cmd, "@pendingStatus", SqlDbType.TinyInt, HostAgentJobStatuses.Pending);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null && Convert.ToBoolean(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<string>> UpsertStaleHostAgentRuntimeStateFindingsAsync(
        long detectedByHostAgentJobId,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NULL
BEGIN
    THROW 51000, 'Maintenance findings are not available. Apply the core OMP schema before running maintenance scans.', 1;
END;

DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();
DECLARE @openStatus tinyint = 0;
DECLARE @cleanupQueuedStatus tinyint = 2;
DECLARE @cleanedStatus tinyint = 3;
DECLARE @skippedStatus tinyint = 5;
DECLARE @Upserted TABLE(FindingKey nvarchar(450) NOT NULL);

WITH StaleRuntime AS
(
    SELECT state.HostId,
           host.HostKey,
           state.ServiceName,
           state.Version,
           state.RuntimeMode,
           state.InstallPath,
           state.LastSeenUtc,
           N'hostagent-runtime:' + CONVERT(nvarchar(36), state.HostId) + N':' + state.ServiceName AS FindingKey
    FROM omp.HostAgentRuntimeStates state
    INNER JOIN omp.Hosts host ON host.HostId = state.HostId
    LEFT JOIN omp.HostAgentLeases lease
        ON lease.HostId = state.HostId
       AND lease.ServiceName = state.ServiceName
       AND lease.LeaseUntilUtc > @nowUtc
    WHERE state.IsActive = 0
      AND lease.HostId IS NULL
      AND
      (
          state.RuntimeMode IN (N'Quiesced', N'Failed')
          OR state.LastSeenUtc IS NULL
          OR state.LastSeenUtc < DATEADD(hour, -1, @nowUtc)
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM omp.HostAgentRuntimeStates activeState
          WHERE activeState.HostId = state.HostId
            AND activeState.ServiceName = state.ServiceName
            AND activeState.IsActive = 1
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM omp.HostAgentDesiredStates desired
          INNER JOIN omp.Artifacts artifact ON artifact.ArtifactId = desired.ArtifactId
          WHERE desired.HostId = state.HostId
            AND desired.IsEnabled = 1
            AND
            (
                COALESCE(NULLIF(LTRIM(RTRIM(desired.ServiceNamePrefix)), N''), N'OMP.HostAgent') + N'.' + artifact.Version = state.ServiceName
            )
      )
)
MERGE omp.MaintenanceFindings WITH (HOLDLOCK) AS target
USING
(
    SELECT runtime.HostId,
           runtime.HostKey,
           runtime.ServiceName,
           runtime.Version,
           runtime.RuntimeMode,
           runtime.InstallPath,
           runtime.LastSeenUtc,
           runtime.FindingKey,
           (
               SELECT 1 AS schemaVersion,
                      N'DatabaseRow' AS targetKind,
                      runtime.HostId AS hostId,
                      runtime.ServiceName AS serviceName
               FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
           ) AS ActionJson
    FROM StaleRuntime runtime
) AS source
ON target.FindingKey = source.FindingKey
WHEN MATCHED THEN
    UPDATE SET
        Scope = N'Global',
        HostId = source.HostId,
        Category = N'HostAgentLeftover',
        TargetKind = N'DatabaseRow',
        TargetIdentifier = source.ServiceName,
        Title = N'Stale HostAgent runtime row',
        Detail = CONCAT(N'Host ', source.HostKey, N' has an inactive HostAgent runtime row for service ', source.ServiceName, N'. Version=', COALESCE(source.Version, N''), N', mode=', COALESCE(source.RuntimeMode, N''), N', install path=', COALESCE(source.InstallPath, N''), N'.'),
        RecommendedAction = N'Delete the stale HostAgent runtime-state row.',
        SafetyNotes = N'The row is inactive, has no active lease, and does not match the currently desired HostAgent service name for the host.',
        ActionJson = source.ActionJson,
        Status = CASE
                     WHEN target.Status IN (@cleanupQueuedStatus, @cleanedStatus, @skippedStatus) THEN target.Status
                     ELSE @openStatus
                 END,
        Severity = 1,
        Confidence = 90,
        DetectedByHostAgentJobId = @detectedByHostAgentJobId,
        ResultMessage = CASE
                            WHEN target.Status IN (@cleanupQueuedStatus, @cleanedStatus, @skippedStatus) THEN target.ResultMessage
                            ELSE NULL
                        END,
        LastSeenUtc = @nowUtc,
        UpdatedUtc = @nowUtc
WHEN NOT MATCHED THEN
    INSERT
    (
        FindingKey,
        Scope,
        HostId,
        Category,
        TargetKind,
        TargetIdentifier,
        Title,
        Detail,
        RecommendedAction,
        SafetyNotes,
        ActionJson,
        Status,
        Severity,
        Confidence,
        DetectedByHostAgentJobId,
        DetectedUtc,
        LastSeenUtc,
        UpdatedUtc
    )
    VALUES
    (
        source.FindingKey,
        N'Global',
        source.HostId,
        N'HostAgentLeftover',
        N'DatabaseRow',
        source.ServiceName,
        N'Stale HostAgent runtime row',
        CONCAT(N'Host ', source.HostKey, N' has an inactive HostAgent runtime row for service ', source.ServiceName, N'. Version=', COALESCE(source.Version, N''), N', mode=', COALESCE(source.RuntimeMode, N''), N', install path=', COALESCE(source.InstallPath, N''), N'.'),
        N'Delete the stale HostAgent runtime-state row.',
        N'The row is inactive, has no active lease, and does not match the currently desired HostAgent service name for the host.',
        source.ActionJson,
        @openStatus,
        1,
        90,
        @detectedByHostAgentJobId,
        @nowUtc,
        @nowUtc,
        @nowUtc
    )
OUTPUT inserted.FindingKey INTO @Upserted(FindingKey);

SELECT FindingKey
FROM @Upserted
ORDER BY FindingKey;";

        var keys = new List<string>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@detectedByHostAgentJobId", SqlDbType.BigInt, detectedByHostAgentJobId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            keys.Add(rdr.GetString(0));
        }

        return keys;
    }

    public async Task<IReadOnlyList<MaintenanceFindingCleanupEntry>> GetMaintenanceCleanupEntriesAsync(
        Guid? jobHostId,
        IReadOnlyCollection<long> findingIds,
        CancellationToken ct)
    {
        if (findingIds.Count == 0)
        {
            return [];
        }

        const string sql = @"
IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS bigint) AS MaintenanceFindingId,
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS nvarchar(80)) AS TargetKind,
        CAST(NULL AS nvarchar(1000)) AS TargetIdentifier,
        CAST(NULL AS nvarchar(max)) AS ActionJson;
    RETURN;
END;

DECLARE @Ids TABLE(MaintenanceFindingId bigint NOT NULL PRIMARY KEY);

INSERT INTO @Ids(MaintenanceFindingId)
SELECT DISTINCT TRY_CONVERT(bigint, value)
FROM OPENJSON(@FindingIdsJson)
WHERE TRY_CONVERT(bigint, value) IS NOT NULL;

SELECT finding.MaintenanceFindingId,
       finding.HostId,
       finding.TargetKind,
       finding.TargetIdentifier,
       finding.ActionJson
FROM omp.MaintenanceFindings finding
INNER JOIN @Ids ids ON ids.MaintenanceFindingId = finding.MaintenanceFindingId
WHERE finding.Status = @cleanupQueuedStatus
  AND finding.ActionJson IS NOT NULL
  AND
  (
      (@jobHostId IS NULL AND finding.Scope = N'Global')
      OR
      (@jobHostId IS NOT NULL AND finding.Scope = N'Host' AND finding.HostId = @jobHostId)
  )
ORDER BY CASE finding.TargetKind
             WHEN N'WindowsService' THEN 0
             WHEN N'IisApplication' THEN 1
             WHEN N'IisAppPool' THEN 2
             WHEN N'File' THEN 3
             WHEN N'Directory' THEN 4
             WHEN N'DatabaseRow' THEN 5
             ELSE 9
         END,
         finding.MaintenanceFindingId;";

        var rows = new List<MaintenanceFindingCleanupEntry>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@FindingIdsJson", SqlDbType.NVarChar, -1, JsonSerializer.Serialize(findingIds.Distinct().Order()));
        Add(cmd, "@jobHostId", SqlDbType.UniqueIdentifier, jobHostId);
        Add(cmd, "@cleanupQueuedStatus", SqlDbType.TinyInt, MaintenanceFindingStatuses.CleanupQueued);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new MaintenanceFindingCleanupEntry
            {
                MaintenanceFindingId = rdr.GetInt64(0),
                HostId = rdr.IsDBNull(1) ? null : rdr.GetGuid(1),
                TargetKind = rdr.GetString(2),
                TargetIdentifier = rdr.GetString(3),
                ActionJson = rdr.IsDBNull(4) ? null : rdr.GetString(4)
            });
        }

        return rows;
    }

    public async Task<int> DeleteStaleHostAgentRuntimeStateAsync(
        Guid hostId,
        string serviceName,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();

DELETE state
FROM omp.HostAgentRuntimeStates state
WHERE state.HostId = @hostId
  AND state.ServiceName = @serviceName
  AND state.IsActive = 0
  AND NOT EXISTS
  (
      SELECT 1
      FROM omp.HostAgentLeases lease
      WHERE lease.HostId = state.HostId
        AND lease.ServiceName = state.ServiceName
        AND lease.LeaseUntilUtc > @nowUtc
  )
  AND NOT EXISTS
  (
      SELECT 1
      FROM omp.HostAgentDesiredStates desired
      INNER JOIN omp.Artifacts artifact ON artifact.ArtifactId = desired.ArtifactId
      WHERE desired.HostId = state.HostId
        AND desired.IsEnabled = 1
        AND COALESCE(NULLIF(LTRIM(RTRIM(desired.ServiceNamePrefix)), N''), N'OMP.HostAgent') + N'.' + artifact.Version = state.ServiceName
  );

SELECT @@ROWCOUNT;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@hostId", SqlDbType.UniqueIdentifier, hostId);
        Add(cmd, "@serviceName", SqlDbType.NVarChar, 200, serviceName);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task UpdateMaintenanceFindingResultAsync(
        long maintenanceFindingId,
        byte status,
        string? message,
        long cleanupHostAgentJobId,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.MaintenanceFindings
SET Status = @status,
    CleanupHostAgentJobId = @cleanupHostAgentJobId,
    ResultMessage = @message,
    UpdatedUtc = SYSUTCDATETIME()
WHERE MaintenanceFindingId = @maintenanceFindingId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@maintenanceFindingId", SqlDbType.BigInt, maintenanceFindingId);
        Add(cmd, "@status", SqlDbType.TinyInt, status);
        Add(cmd, "@cleanupHostAgentJobId", SqlDbType.BigInt, cleanupHostAgentJobId);
        Add(cmd, "@message", SqlDbType.NVarChar, -1, NullIfWhiteSpace(message));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<OrphanHostCandidate>> GetOrphanHostCandidatesAsync(
        Guid currentHostId,
        int maxCandidates,
        CancellationToken ct)
    {
        // Orphan-host criteria:
        //  - Environment is null/empty (unclassified / not assigned to a named environment).
        //  - Not the seed/sample host used during bootstrap.
        //  - InstanceId does not belong to an active omp.Instances row. A foreign InstanceId
        //    is definitive: the host belongs to no known installation, so it is an orphan.
        //  - No AppInstances: an AppInstance means someone intentionally configured an app for
        //    this host, so there is active deploy-intention and the host must not be treated as
        //    an orphan.
        //  - No HostAppDeploymentStates: a deployment state means a runtime deployment actually
        //    happened for this host, so it has been actively used and must not be treated as an
        //    orphan.
        // HostArtifactRequirements and HostArtifactStates are deliberately NOT exclusions.
        // Those rows are orphan-dependencies (leftover desired/provisioned artifacts for a host
        // that no longer belongs to an installation) and are exactly what cleanup should remove.
        const string sql = @"
SELECT TOP (@maxCandidates)
    h.HostId,
    h.HostKey,
    h.InstanceId,
    h.Environment,
    (
        SELECT COUNT(1)
        FROM omp.AppInstances ai
        WHERE ai.HostId = h.HostId
    ) AS AppInstanceCount,
    (
        SELECT COUNT(1)
        FROM omp.HostArtifactRequirements har
        WHERE har.HostId = h.HostId
    ) AS HostArtifactRequirementCount,
    (
        SELECT COUNT(1)
        FROM omp.HostArtifactStates has
        WHERE has.HostId = h.HostId
    ) AS HostArtifactStateCount,
    (
        SELECT COUNT(1)
        FROM omp.HostAppDeploymentStates hds
        WHERE hds.HostId = h.HostId
    ) AS HostAppDeploymentStateCount
FROM omp.Hosts h
WHERE h.HostId <> @currentHostId
  AND (h.Environment IS NULL OR LTRIM(RTRIM(h.Environment)) = N'')
  AND h.HostKey <> N'sample-host'
  AND NOT EXISTS
  (
      SELECT 1
      FROM omp.Instances i
      WHERE i.InstanceId = h.InstanceId
        AND i.IsEnabled = 1
  )
  AND NOT EXISTS
  (
      SELECT 1
      FROM omp.AppInstances ai
      WHERE ai.HostId = h.HostId
  )
  AND NOT EXISTS
  (
      SELECT 1
      FROM omp.HostAppDeploymentStates hds
      WHERE hds.HostId = h.HostId
  )
ORDER BY h.HostKey;";

        var candidates = new List<OrphanHostCandidate>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@currentHostId", SqlDbType.UniqueIdentifier, currentHostId);
        Add(cmd, "@maxCandidates", SqlDbType.Int, Math.Max(1, maxCandidates));

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            candidates.Add(new OrphanHostCandidate
            {
                HostId = rdr.GetGuid(0),
                HostKey = rdr.GetString(1),
                InstanceId = rdr.GetGuid(2),
                Environment = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                AppInstanceCount = rdr.GetInt32(4),
                HostArtifactRequirementCount = rdr.GetInt32(5),
                HostArtifactStateCount = rdr.GetInt32(6),
                HostAppDeploymentStateCount = rdr.GetInt32(7)
            });
        }

        return candidates;
    }

    /// <summary>
    /// Deletes an orphan host and all of its dependent rows in an FK-safe order.
    /// Maintenance findings for the host are unlinked (HostId set to NULL) so the
    /// cleanup job can still update the status of the finding that triggered the
    /// cleanup after the host row is gone.
    /// </summary>
    public async Task<int> DeleteOrphanHostAsync(
        Guid hostId,
        CancellationToken ct)
    {
        const string sql = @"
-- Unlink maintenance findings so the current finding row can be updated after the host is gone.
IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NOT NULL
    UPDATE omp.MaintenanceFindings
    SET HostId = NULL
    WHERE HostId = @hostId;

-- Delete direct FK children of omp.Hosts, ordered so that rows referencing AppInstances
-- are removed before AppInstances itself (defensive, since the orphan finder excludes
-- hosts with AppInstances/HostAppDeploymentStates). Each statement is guarded with
-- OBJECT_ID so the cleanup is robust on partial/schemas (e.g., isolated test databases).
IF OBJECT_ID(N'omp.HostArtifactRequirements', N'U') IS NOT NULL DELETE FROM omp.HostArtifactRequirements WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.HostArtifactStates', N'U') IS NOT NULL DELETE FROM omp.HostArtifactStates WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.HostAgentLeases', N'U') IS NOT NULL DELETE FROM omp.HostAgentLeases WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.HostAgentRuntimeStates', N'U') IS NOT NULL DELETE FROM omp.HostAgentRuntimeStates WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.HostAgentDesiredStates', N'U') IS NOT NULL DELETE FROM omp.HostAgentDesiredStates WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.WebAppHealthStates', N'U') IS NOT NULL DELETE FROM omp.WebAppHealthStates WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.HostResourceSamples', N'U') IS NOT NULL DELETE FROM omp.HostResourceSamples WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.HostResourceLatest', N'U') IS NOT NULL DELETE FROM omp.HostResourceLatest WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.HostDeploymentAssignments', N'U') IS NOT NULL DELETE FROM omp.HostDeploymentAssignments WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.HostDeployments', N'U') IS NOT NULL DELETE FROM omp.HostDeployments WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.WorkerInstances', N'U') IS NOT NULL DELETE FROM omp.WorkerInstances WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.HostAppDeploymentStates', N'U') IS NOT NULL DELETE FROM omp.HostAppDeploymentStates WHERE HostId = @hostId;
IF OBJECT_ID(N'omp.AppInstances', N'U') IS NOT NULL DELETE FROM omp.AppInstances WHERE HostId = @hostId;

-- Delete the orphan host itself.
DELETE FROM omp.Hosts WHERE HostId = @hostId;

SELECT @@ROWCOUNT;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@hostId", SqlDbType.UniqueIdentifier, hostId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

}
