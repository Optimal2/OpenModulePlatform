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
