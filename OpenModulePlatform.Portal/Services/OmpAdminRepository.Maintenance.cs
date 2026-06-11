using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;
using System.Text.Json;

namespace OpenModulePlatform.Portal.Services;

public sealed partial class OmpAdminRepository
{
    // Retention preview and cleanup jobs scan all historical artifact versions and every reference table.
    // Large installations can legitimately need a long-running query while the result is still bounded in C#.
    private const int ArtifactRetentionCommandTimeoutSeconds = 3600;

    private const string ArtifactRetentionCandidateSql = @"
WITH RankedArtifacts AS
(
    SELECT ar.ArtifactId,
           m.ModuleKey,
           a.AppKey,
           ar.Version,
           ar.PackageType,
           ar.TargetName,
           ar.RelativePath,
           ar.CreatedUtc,
           CAST(ROW_NUMBER() OVER
           (
               PARTITION BY ar.AppId, ar.PackageType, ISNULL(ar.TargetName, N'')
               ORDER BY ar.CreatedUtc DESC, ar.ArtifactId DESC
           ) AS int) AS RetentionRank,
           CAST(COUNT(1) OVER
           (
               PARTITION BY ar.AppId, ar.PackageType, ISNULL(ar.TargetName, N'')
           ) AS int) AS TotalVersions,
           pr.ProtectedReferenceCount
    FROM omp.Artifacts ar
    INNER JOIN omp.Apps a ON a.AppId = ar.AppId
    INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
    OUTER APPLY
    (
        SELECT COUNT(1) AS ProtectedReferenceCount
        FROM
        (
            SELECT 1 AS ReferenceRow
            FROM omp.AppInstances ai
            WHERE ai.ArtifactId = ar.ArtifactId

            UNION ALL

            SELECT 1
            FROM omp.WorkerInstances wi
            WHERE wi.ArtifactId = ar.ArtifactId

            UNION ALL

            SELECT 1
            FROM omp.InstanceTemplateAppInstances tai
            WHERE tai.DesiredArtifactId = ar.ArtifactId

            UNION ALL

            SELECT 1
            FROM omp.HostArtifactRequirements har
            WHERE har.ArtifactId = ar.ArtifactId
              AND har.IsEnabled = 1

            UNION ALL

            SELECT 1
            FROM omp.HostAgentDesiredStates hads
            WHERE hads.ArtifactId = ar.ArtifactId

            UNION ALL

            SELECT 1
            FROM omp.HostAppDeploymentStates hads
            WHERE hads.ArtifactId = ar.ArtifactId

            UNION ALL

            SELECT 1
            FROM omp.HostAgentRuntimeStates hars
            WHERE hars.ArtifactId = ar.ArtifactId
              AND hars.IsActive = 1
        ) protectedRefs
    ) pr
)
SELECT ArtifactId,
       ModuleKey,
       AppKey,
       Version,
       PackageType,
       TargetName,
       RelativePath,
       CreatedUtc,
       RetentionRank,
       TotalVersions,
       ProtectedReferenceCount
FROM RankedArtifacts
WHERE TotalVersions > @MaxVersionsToKeep
  AND RetentionRank > @MaxVersionsToKeep
ORDER BY ModuleKey,
         AppKey,
         PackageType,
         TargetName,
         RetentionRank DESC,
         CreatedUtc,
         ArtifactId;";

    public async Task<ArtifactRetentionPreview> GetArtifactRetentionPreviewAsync(
        int maxVersionsToKeep,
        CancellationToken ct)
    {
        var candidates = await ReadArtifactRetentionCandidatesAsync(
            ArtifactRetentionCandidateSql,
            maxVersionsToKeep,
            ct);

        return new ArtifactRetentionPreview
        {
            MaxVersionsToKeep = maxVersionsToKeep,
            Candidates = candidates
        };
    }

    public async Task<long> QueueArtifactRetentionCleanupAsync(
        int maxVersionsToKeep,
        string? requestedBy,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.HostAgentJobs', N'U') IS NULL
BEGIN
    THROW 51000, 'HostAgent job queue is not available. Apply the core OMP schema before queueing maintenance jobs.', 1;
END;

DECLARE @PayloadJson nvarchar(max) =
(
    SELECT 1 AS schemaVersion,
           @MaxVersionsToKeep AS maxVersionsToKeep
    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
);

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
    NULL,
    N'ArtifactRetentionCleanup',
    @PayloadJson,
    CAST(0 AS tinyint),
    @RequestedBy,
    3
);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@MaxVersionsToKeep", Math.Clamp(maxVersionsToKeep, 1, 100));
        Add(cmd, "@RequestedBy", string.IsNullOrWhiteSpace(requestedBy) ? DBNull.Value : Truncate(requestedBy.Trim(), 256));

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<MaintenanceScanQueueResult> QueueMaintenanceScanAsync(
        string? requestedBy,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.HostAgentJobs', N'U') IS NULL
BEGIN
    THROW 51000, 'HostAgent job queue is not available. Apply the core OMP schema before queueing maintenance jobs.', 1;
END;

IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NULL
BEGIN
    THROW 51000, 'Maintenance findings are not available. Apply the core OMP schema before queueing a maintenance scan.', 1;
END;

DECLARE @CreatedJobs TABLE
(
    HostAgentJobId bigint NOT NULL,
    HostId uniqueidentifier NULL
);

INSERT INTO omp.HostAgentJobs
(
    HostId,
    JobType,
    PayloadJson,
    Status,
    RequestedBy,
    MaxAttempts
)
OUTPUT inserted.HostAgentJobId, inserted.HostId INTO @CreatedJobs(HostAgentJobId, HostId)
VALUES
(
    NULL,
    N'MaintenanceScan',
    N'{""schemaVersion"":1,""scope"":""Global""}',
    CAST(0 AS tinyint),
    @RequestedBy,
    3
);

INSERT INTO omp.HostAgentJobs
(
    HostId,
    JobType,
    PayloadJson,
    Status,
    RequestedBy,
    MaxAttempts
)
OUTPUT inserted.HostAgentJobId, inserted.HostId INTO @CreatedJobs(HostAgentJobId, HostId)
SELECT host.HostId,
       N'MaintenanceScan',
       N'{""schemaVersion"":1,""scope"":""Host""}',
       CAST(0 AS tinyint),
       @RequestedBy,
       3
FROM omp.Hosts host
WHERE host.IsEnabled = 1
  AND
  (
      EXISTS
      (
          SELECT 1
          FROM omp.HostAgentDesiredStates desired
          WHERE desired.HostId = host.HostId
            AND desired.IsEnabled = 1
      )
      OR EXISTS
      (
          SELECT 1
          FROM omp.HostAgentRuntimeStates runtime
          WHERE runtime.HostId = host.HostId
            AND runtime.IsActive = 1
      )
  );

SELECT
    MAX(CASE WHEN HostId IS NULL THEN HostAgentJobId END) AS GlobalHostAgentJobId,
    SUM(CASE WHEN HostId IS NOT NULL THEN 1 ELSE 0 END) AS HostJobCount
FROM @CreatedJobs;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@RequestedBy", string.IsNullOrWhiteSpace(requestedBy) ? DBNull.Value : Truncate(requestedBy.Trim(), 256));

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return new MaintenanceScanQueueResult();
        }

        return new MaintenanceScanQueueResult
        {
            GlobalHostAgentJobId = rdr.IsDBNull(0) ? null : rdr.GetInt64(0),
            HostJobCount = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1)
        };
    }

    public async Task<IReadOnlyList<MaintenanceFindingRow>> GetMaintenanceFindingsAsync(
        int limit,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS bigint) AS MaintenanceFindingId,
        CAST(NULL AS nvarchar(450)) AS FindingKey,
        CAST(NULL AS nvarchar(20)) AS Scope,
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS nvarchar(128)) AS HostKey,
        CAST(NULL AS nvarchar(200)) AS HostDisplayName,
        CAST(NULL AS nvarchar(100)) AS Category,
        CAST(NULL AS nvarchar(80)) AS TargetKind,
        CAST(NULL AS nvarchar(1000)) AS TargetIdentifier,
        CAST(NULL AS nvarchar(300)) AS Title,
        CAST(NULL AS nvarchar(max)) AS Detail,
        CAST(NULL AS nvarchar(300)) AS RecommendedAction,
        CAST(NULL AS nvarchar(max)) AS SafetyNotes,
        CAST(NULL AS tinyint) AS Status,
        CAST(NULL AS tinyint) AS Severity,
        CAST(NULL AS tinyint) AS Confidence,
        CAST(NULL AS datetime2(3)) AS DetectedUtc,
        CAST(NULL AS datetime2(3)) AS LastSeenUtc,
        CAST(NULL AS nvarchar(max)) AS ResultMessage;
    RETURN;
END;

SELECT TOP (@Limit)
       finding.MaintenanceFindingId,
       finding.FindingKey,
       finding.Scope,
       finding.HostId,
       host.HostKey,
       host.DisplayName AS HostDisplayName,
       finding.Category,
       finding.TargetKind,
       finding.TargetIdentifier,
       finding.Title,
       finding.Detail,
       finding.RecommendedAction,
       finding.SafetyNotes,
       finding.Status,
       finding.Severity,
       finding.Confidence,
       finding.DetectedUtc,
       finding.LastSeenUtc,
       finding.ResultMessage
FROM omp.MaintenanceFindings finding
LEFT JOIN omp.Hosts host ON host.HostId = finding.HostId
WHERE finding.Status IN (0, 2, 4)
ORDER BY CASE finding.Status WHEN 4 THEN 0 WHEN 0 THEN 1 ELSE 2 END,
         finding.Severity DESC,
         finding.LastSeenUtc DESC,
         finding.MaintenanceFindingId DESC;";

        var rows = new List<MaintenanceFindingRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@Limit", Math.Clamp(limit, 1, 500));

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new MaintenanceFindingRow
            {
                MaintenanceFindingId = rdr.GetInt64(0),
                FindingKey = rdr.GetString(1),
                Scope = rdr.GetString(2),
                HostId = rdr.IsDBNull(3) ? null : rdr.GetGuid(3),
                HostKey = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                HostDisplayName = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                Category = rdr.GetString(6),
                TargetKind = rdr.GetString(7),
                TargetIdentifier = rdr.GetString(8),
                Title = rdr.GetString(9),
                Detail = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                RecommendedAction = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                SafetyNotes = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                Status = rdr.GetByte(13),
                Severity = rdr.GetByte(14),
                Confidence = rdr.GetByte(15),
                DetectedUtc = rdr.GetDateTime(16),
                LastSeenUtc = rdr.GetDateTime(17),
                ResultMessage = rdr.IsDBNull(18) ? null : rdr.GetString(18)
            });
        }

        return rows;
    }

    public async Task<int> IgnoreMaintenanceFindingsAsync(
        IReadOnlyCollection<long> findingIds,
        string? requestedBy,
        CancellationToken ct)
    {
        if (findingIds.Count == 0)
        {
            return 0;
        }

        const string sql = @"
IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NULL
BEGIN
    SELECT 0;
    RETURN;
END;

DECLARE @Ids TABLE(MaintenanceFindingId bigint NOT NULL PRIMARY KEY);

INSERT INTO @Ids(MaintenanceFindingId)
SELECT DISTINCT TRY_CONVERT(bigint, value)
FROM OPENJSON(@FindingIdsJson)
WHERE TRY_CONVERT(bigint, value) IS NOT NULL;

UPDATE finding
SET Status = 1,
    IgnoredBy = @RequestedBy,
    ResultMessage = N'Ignored by portal administrator.',
    UpdatedUtc = SYSUTCDATETIME()
FROM omp.MaintenanceFindings finding
INNER JOIN @Ids ids ON ids.MaintenanceFindingId = finding.MaintenanceFindingId
WHERE finding.Status IN (0, 4);

SELECT @@ROWCOUNT;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@FindingIdsJson", JsonSerializer.Serialize(findingIds.Distinct().Order()));
        Add(cmd, "@RequestedBy", string.IsNullOrWhiteSpace(requestedBy) ? DBNull.Value : Truncate(requestedBy.Trim(), 256));

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<MaintenanceCleanupQueueResult> QueueMaintenanceCleanupAsync(
        IReadOnlyCollection<long> findingIds,
        string? requestedBy,
        CancellationToken ct)
    {
        if (findingIds.Count == 0)
        {
            return new MaintenanceCleanupQueueResult();
        }

        var selectedIds = findingIds.Distinct().Order().ToArray();
        var candidates = await GetMaintenanceCleanupCandidatesAsync(selectedIds, ct);
        var queuedJobCount = 0;
        var queuedFindingCount = 0;

        foreach (var group in candidates.GroupBy(static row => row.JobHostId))
        {
            var ids = group.Select(static row => row.MaintenanceFindingId).Order().ToArray();
            if (ids.Length == 0)
            {
                continue;
            }

            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                findingIds = ids
            });

            var jobId = await QueueMaintenanceCleanupJobAsync(group.Key, payload, requestedBy, ct);
            var updated = await MarkMaintenanceFindingsCleanupQueuedAsync(ids, jobId, requestedBy, ct);
            queuedFindingCount += updated;
            queuedJobCount++;
        }

        return new MaintenanceCleanupQueueResult
        {
            SelectedFindingCount = selectedIds.Length,
            QueuedFindingCount = queuedFindingCount,
            QueuedJobCount = queuedJobCount
        };
    }

    public async Task<IReadOnlyList<HostAgentJobRow>> GetRecentHostAgentJobsAsync(
        int limit,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.HostAgentJobs', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS bigint) AS HostAgentJobId,
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS nvarchar(128)) AS HostKey,
        CAST(NULL AS nvarchar(200)) AS DisplayName,
        CAST(NULL AS nvarchar(100)) AS JobType,
        CAST(NULL AS tinyint) AS Status,
        CAST(NULL AS nvarchar(256)) AS RequestedBy,
        CAST(NULL AS datetime2(3)) AS RequestedUtc,
        CAST(NULL AS nvarchar(200)) AS ClaimedByServiceName,
        CAST(NULL AS datetime2(3)) AS ClaimedUtc,
        CAST(NULL AS datetime2(3)) AS LeaseUntilUtc,
        CAST(NULL AS datetime2(3)) AS StartedUtc,
        CAST(NULL AS datetime2(3)) AS CompletedUtc,
        CAST(NULL AS int) AS AttemptCount,
        CAST(NULL AS int) AS MaxAttempts,
        CAST(NULL AS nvarchar(max)) AS ResultJson,
        CAST(NULL AS nvarchar(max)) AS LastError;
    RETURN;
END;

SELECT TOP (@Limit)
       job.HostAgentJobId,
       job.HostId,
       host.HostKey,
       host.DisplayName,
       job.JobType,
       job.Status,
       job.RequestedBy,
       job.RequestedUtc,
       job.ClaimedByServiceName,
       job.ClaimedUtc,
       job.LeaseUntilUtc,
       job.StartedUtc,
       job.CompletedUtc,
       job.AttemptCount,
       job.MaxAttempts,
       job.ResultJson,
       job.LastError
FROM omp.HostAgentJobs job
LEFT JOIN omp.Hosts host ON host.HostId = job.HostId
ORDER BY job.RequestedUtc DESC,
         job.HostAgentJobId DESC;";

        var rows = new List<HostAgentJobRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@Limit", Math.Clamp(limit, 1, 200));

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostAgentJobRow
            {
                HostAgentJobId = rdr.GetInt64(0),
                HostId = rdr.IsDBNull(1) ? null : rdr.GetGuid(1),
                HostKey = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                HostDisplayName = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                JobType = rdr.GetString(4),
                Status = rdr.GetByte(5),
                RequestedBy = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                RequestedUtc = rdr.GetDateTime(7),
                ClaimedByServiceName = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                ClaimedUtc = rdr.IsDBNull(9) ? null : rdr.GetDateTime(9),
                LeaseUntilUtc = rdr.IsDBNull(10) ? null : rdr.GetDateTime(10),
                StartedUtc = rdr.IsDBNull(11) ? null : rdr.GetDateTime(11),
                CompletedUtc = rdr.IsDBNull(12) ? null : rdr.GetDateTime(12),
                AttemptCount = rdr.GetInt32(13),
                MaxAttempts = rdr.GetInt32(14),
                ResultJson = rdr.IsDBNull(15) ? null : rdr.GetString(15),
                LastError = rdr.IsDBNull(16) ? null : rdr.GetString(16)
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<MaintenanceCleanupCandidate>> GetMaintenanceCleanupCandidatesAsync(
        IReadOnlyCollection<long> findingIds,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS bigint) AS MaintenanceFindingId,
        CAST(NULL AS uniqueidentifier) AS JobHostId;
    RETURN;
END;

DECLARE @Ids TABLE(MaintenanceFindingId bigint NOT NULL PRIMARY KEY);

INSERT INTO @Ids(MaintenanceFindingId)
SELECT DISTINCT TRY_CONVERT(bigint, value)
FROM OPENJSON(@FindingIdsJson)
WHERE TRY_CONVERT(bigint, value) IS NOT NULL;

SELECT finding.MaintenanceFindingId,
       CASE WHEN finding.Scope = N'Host' THEN finding.HostId ELSE CAST(NULL AS uniqueidentifier) END AS JobHostId
FROM omp.MaintenanceFindings finding
INNER JOIN @Ids ids ON ids.MaintenanceFindingId = finding.MaintenanceFindingId
WHERE finding.Status IN (0, 4)
  AND finding.ActionJson IS NOT NULL
  AND
  (
      finding.Scope = N'Global'
      OR finding.HostId IS NOT NULL
  )
ORDER BY JobHostId,
         finding.MaintenanceFindingId;";

        var rows = new List<MaintenanceCleanupCandidate>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@FindingIdsJson", JsonSerializer.Serialize(findingIds.Distinct().Order()));

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new MaintenanceCleanupCandidate(
                rdr.GetInt64(0),
                rdr.IsDBNull(1) ? null : rdr.GetGuid(1)));
        }

        return rows;
    }

    private async Task<long> QueueMaintenanceCleanupJobAsync(
        Guid? hostId,
        string payloadJson,
        string? requestedBy,
        CancellationToken ct)
    {
        const string sql = @"
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
    N'MaintenanceCleanup',
    @PayloadJson,
    CAST(0 AS tinyint),
    @RequestedBy,
    3
);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@HostId", hostId.HasValue ? hostId.Value : DBNull.Value);
        Add(cmd, "@PayloadJson", payloadJson);
        Add(cmd, "@RequestedBy", string.IsNullOrWhiteSpace(requestedBy) ? DBNull.Value : Truncate(requestedBy.Trim(), 256));

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> MarkMaintenanceFindingsCleanupQueuedAsync(
        IReadOnlyCollection<long> findingIds,
        long hostAgentJobId,
        string? requestedBy,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @Ids TABLE(MaintenanceFindingId bigint NOT NULL PRIMARY KEY);

INSERT INTO @Ids(MaintenanceFindingId)
SELECT DISTINCT TRY_CONVERT(bigint, value)
FROM OPENJSON(@FindingIdsJson)
WHERE TRY_CONVERT(bigint, value) IS NOT NULL;

UPDATE finding
SET Status = 2,
    CleanupHostAgentJobId = @HostAgentJobId,
    RequestedBy = @RequestedBy,
    ResultMessage = N'Cleanup queued.',
    UpdatedUtc = SYSUTCDATETIME()
FROM omp.MaintenanceFindings finding
INNER JOIN @Ids ids ON ids.MaintenanceFindingId = finding.MaintenanceFindingId
WHERE finding.Status IN (0, 4);

SELECT @@ROWCOUNT;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@FindingIdsJson", JsonSerializer.Serialize(findingIds.Distinct().Order()));
        Add(cmd, "@HostAgentJobId", hostAgentJobId);
        Add(cmd, "@RequestedBy", string.IsNullOrWhiteSpace(requestedBy) ? DBNull.Value : Truncate(requestedBy.Trim(), 256));

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<ArtifactRetentionCandidateRow>> ReadArtifactRetentionCandidatesAsync(
        string sql,
        int maxVersionsToKeep,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var newCmd = new SqlCommand(sql, conn);
        newCmd.CommandTimeout = ArtifactRetentionCommandTimeoutSeconds;
        Add(newCmd, "@MaxVersionsToKeep", maxVersionsToKeep);
        return await ReadArtifactRetentionCandidatesAsync(newCmd, ct);
    }

    private static async Task<IReadOnlyList<ArtifactRetentionCandidateRow>> ReadArtifactRetentionCandidatesAsync(
        SqlCommand cmd,
        CancellationToken ct)
    {
        var rows = new List<ArtifactRetentionCandidateRow>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(ReadArtifactRetentionCandidate(rdr));
        }

        return rows;
    }

    private static ArtifactRetentionCandidateRow ReadArtifactRetentionCandidate(SqlDataReader rdr)
        => new()
        {
            ArtifactId = rdr.GetInt32(0),
            ModuleKey = rdr.GetString(1),
            AppKey = rdr.GetString(2),
            Version = rdr.GetString(3),
            PackageType = rdr.GetString(4),
            TargetName = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            RelativePath = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            CreatedUtc = rdr.GetDateTime(7),
            RetentionRank = rdr.GetInt32(8),
            TotalVersions = rdr.GetInt32(9),
            ProtectedReferenceCount = rdr.GetInt32(10)
        };

    private sealed record MaintenanceCleanupCandidate(
        long MaintenanceFindingId,
        Guid? JobHostId);
}
