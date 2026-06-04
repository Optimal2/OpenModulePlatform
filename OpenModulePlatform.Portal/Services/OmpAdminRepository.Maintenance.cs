using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Services;

public sealed partial class OmpAdminRepository
{
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

    public async Task<ArtifactRetentionDeletionResult> DeleteOldArtifactVersionsAsync(
        int maxVersionsToKeep,
        string? requestedBy,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @DeleteArtifacts TABLE
(
    ArtifactId int NOT NULL PRIMARY KEY,
    ModuleKey nvarchar(100) NOT NULL,
    AppKey nvarchar(100) NOT NULL,
    Version nvarchar(50) NOT NULL,
    PackageType nvarchar(50) NOT NULL,
    TargetName nvarchar(100) NULL,
    RelativePath nvarchar(400) NULL,
    CreatedUtc datetime2(3) NOT NULL,
    RetentionRank int NOT NULL,
    TotalVersions int NOT NULL,
    ProtectedReferenceCount int NOT NULL
);

DECLARE @CacheEntries TABLE
(
    HostId uniqueidentifier NOT NULL,
    ArtifactId int NOT NULL,
    Version nvarchar(50) NOT NULL,
    PackageType nvarchar(50) NOT NULL,
    TargetName nvarchar(100) NULL,
    LocalPath nvarchar(500) NULL,
    ContentSha256 nvarchar(128) NULL
);

DECLARE @CreatedJobs TABLE
(
    HostAgentJobId bigint NOT NULL
);

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
INSERT INTO @DeleteArtifacts
(
    ArtifactId,
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
  AND ProtectedReferenceCount = 0;

INSERT INTO @CacheEntries
(
    HostId,
    ArtifactId,
    Version,
    PackageType,
    TargetName,
    LocalPath,
    ContentSha256
)
SELECT has.HostId,
       d.ArtifactId,
       d.Version,
       d.PackageType,
       d.TargetName,
       has.LocalPath,
       has.ContentSha256
FROM omp.HostArtifactStates has
INNER JOIN @DeleteArtifacts d ON d.ArtifactId = has.ArtifactId
WHERE has.LocalPath IS NOT NULL
  AND LTRIM(RTRIM(has.LocalPath)) <> N'';

DELETE s
FROM omp.HostAppDeploymentStates s
INNER JOIN @DeleteArtifacts d ON d.ArtifactId = s.ArtifactId;

DELETE s
FROM omp.HostAgentRuntimeStates s
INNER JOIN @DeleteArtifacts d ON d.ArtifactId = s.ArtifactId;

DELETE s
FROM omp.HostArtifactStates s
INNER JOIN @DeleteArtifacts d ON d.ArtifactId = s.ArtifactId;

DELETE c
FROM omp.ArtifactConfigurationFiles c
INNER JOIN @DeleteArtifacts d ON d.ArtifactId = c.ArtifactId;

DELETE ar
FROM omp.Artifacts ar
INNER JOIN @DeleteArtifacts d ON d.ArtifactId = ar.ArtifactId;

INSERT INTO omp.HostAgentJobs
(
    HostId,
    JobType,
    PayloadJson,
    Status,
    RequestedBy,
    MaxAttempts
)
OUTPUT inserted.HostAgentJobId INTO @CreatedJobs(HostAgentJobId)
SELECT hostEntries.HostId,
       N'ArtifactCacheCleanup',
       (
           SELECT 1 AS schemaVersion,
                  JSON_QUERY
                  (
                      (
                          SELECT entry.ArtifactId AS artifactId,
                                 entry.Version AS version,
                                 entry.PackageType AS packageType,
                                 entry.TargetName AS targetName,
                                 entry.LocalPath AS localPath,
                                 CAST(NULL AS nvarchar(500)) AS cacheRelativePath,
                                 entry.ContentSha256 AS contentSha256
                          FROM @CacheEntries entry
                          WHERE entry.HostId = hostEntries.HostId
                          ORDER BY entry.PackageType,
                                   entry.TargetName,
                                   entry.Version,
                                   entry.ArtifactId
                          FOR JSON PATH
                      )
                  ) AS artifactCacheEntries
           FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
       ),
       CAST(0 AS tinyint),
       @RequestedBy,
       3
FROM
(
    SELECT DISTINCT HostId
    FROM @CacheEntries
) hostEntries;

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
FROM @DeleteArtifacts
ORDER BY ModuleKey,
         AppKey,
         PackageType,
         TargetName,
         RetentionRank DESC,
         CreatedUtc,
         ArtifactId;

SELECT COUNT(1) AS HostCacheEntryCount
FROM @CacheEntries;

SELECT COUNT(1) AS CreatedHostAgentJobCount
FROM @CreatedJobs;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            var deleted = await ReadArtifactRetentionDeletionResultAsync(
                sql,
                maxVersionsToKeep,
                requestedBy,
                ct,
                conn,
                tx);

            await tx.CommitAsync(ct);
            return deleted;
        }
        catch (Exception)
        {
            // The broad catch is intentional here: any cleanup failure must roll back the transaction before the original error is rethrown.
            await tx.RollbackAsync(ct);
            throw;
        }
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
INNER JOIN omp.Hosts host ON host.HostId = job.HostId
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
                HostId = rdr.GetGuid(1),
                HostKey = rdr.GetString(2),
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

    private async Task<ArtifactRetentionDeletionResult> ReadArtifactRetentionDeletionResultAsync(
        string sql,
        int maxVersionsToKeep,
        string? requestedBy,
        CancellationToken ct,
        SqlConnection conn,
        SqlTransaction tx)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.CommandTimeout = 3600;
        Add(cmd, "@MaxVersionsToKeep", maxVersionsToKeep);
        Add(cmd, "@RequestedBy", string.IsNullOrWhiteSpace(requestedBy) ? DBNull.Value : requestedBy.Trim());

        var deletedArtifacts = new List<ArtifactRetentionCandidateRow>();
        var hostCacheEntryCount = 0;
        var createdHostAgentJobCount = 0;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            deletedArtifacts.Add(ReadArtifactRetentionCandidate(rdr));
        }

        if (await rdr.NextResultAsync(ct) && await rdr.ReadAsync(ct))
        {
            hostCacheEntryCount = rdr.GetInt32(0);
        }

        if (await rdr.NextResultAsync(ct) && await rdr.ReadAsync(ct))
        {
            createdHostAgentJobCount = rdr.GetInt32(0);
        }

        return new ArtifactRetentionDeletionResult
        {
            DeletedArtifacts = deletedArtifacts,
            HostCacheEntryCount = hostCacheEntryCount,
            CreatedHostAgentJobCount = createdHostAgentJobCount
        };
    }

    private async Task<IReadOnlyList<ArtifactRetentionCandidateRow>> ReadArtifactRetentionCandidatesAsync(
        string sql,
        int maxVersionsToKeep,
        CancellationToken ct,
        SqlConnection? existingConnection = null,
        SqlTransaction? transaction = null)
    {
        if (existingConnection is not null)
        {
            await using var cmd = new SqlCommand(sql, existingConnection, transaction);
            cmd.CommandTimeout = 3600;
            Add(cmd, "@MaxVersionsToKeep", maxVersionsToKeep);
            return await ReadArtifactRetentionCandidatesAsync(cmd, ct);
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var newCmd = new SqlCommand(sql, conn);
        newCmd.CommandTimeout = 3600;
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
}
