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

    public async Task<IReadOnlyList<ArtifactRetentionCandidateRow>> DeleteOldArtifactVersionsAsync(
        int maxVersionsToKeep,
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
         ArtifactId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            var deleted = await ReadArtifactRetentionCandidatesAsync(
                sql,
                maxVersionsToKeep,
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
            rows.Add(new ArtifactRetentionCandidateRow
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
            });
        }

        return rows;
    }
}
