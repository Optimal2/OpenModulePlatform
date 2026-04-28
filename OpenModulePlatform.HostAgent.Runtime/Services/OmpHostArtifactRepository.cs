using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class OmpHostArtifactRepository
{
    private readonly SqlConnectionFactory _db;

    public OmpHostArtifactRepository(SqlConnectionFactory db)
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

    public async Task<ArtifactDescriptor?> GetArtifactByIdAsync(
        string hostKey,
        int artifactId,
        string? desiredLocalPath,
        CancellationToken ct)
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
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS int) AS ArtifactId,
        CAST(NULL AS nvarchar(50)) AS Version,
        CAST(NULL AS nvarchar(50)) AS PackageType,
        CAST(NULL AS nvarchar(100)) AS TargetName,
        CAST(NULL AS nvarchar(400)) AS RelativePath,
        CAST(NULL AS nvarchar(128)) AS Sha256;
    RETURN;
END;

SELECT TOP (1)
    @hostId AS HostId,
    ar.ArtifactId,
    ar.Version,
    ar.PackageType,
    ar.TargetName,
    ar.RelativePath,
    ar.Sha256
FROM omp.Artifacts ar
WHERE ar.ArtifactId = @artifactId
  AND ar.IsEnabled = 1
  AND ar.RelativePath IS NOT NULL
  AND LTRIM(RTRIM(ar.RelativePath)) <> N'';";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ArtifactDescriptor
        {
            HostId = rdr.GetGuid(0),
            ArtifactId = rdr.GetInt32(1),
            Version = rdr.GetString(2),
            PackageType = rdr.GetString(3),
            TargetName = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            RelativePath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            Sha256 = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            RequirementKey = $"rpc:{artifactId}",
            DesiredLocalPath = string.IsNullOrWhiteSpace(desiredLocalPath) ? null : desiredLocalPath.Trim()
        };
    }

    public async Task<IReadOnlyList<ArtifactDescriptor>> GetDesiredArtifactsAsync(
        string hostKey,
        bool includeAppInstanceArtifacts,
        bool includeExplicitRequirements,
        int maxArtifacts,
        CancellationToken ct)
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
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS int) AS ArtifactId,
        CAST(NULL AS nvarchar(50)) AS Version,
        CAST(NULL AS nvarchar(50)) AS PackageType,
        CAST(NULL AS nvarchar(100)) AS TargetName,
        CAST(NULL AS nvarchar(400)) AS RelativePath,
        CAST(NULL AS nvarchar(128)) AS Sha256,
        CAST(NULL AS nvarchar(200)) AS RequirementKey,
        CAST(NULL AS nvarchar(500)) AS DesiredLocalPath;
    RETURN;
END;

WITH Desired AS
(
    SELECT
        @hostId AS HostId,
        ar.ArtifactId,
        ar.Version,
        ar.PackageType,
        ar.TargetName,
        ar.RelativePath,
        ar.Sha256,
        CONCAT(N'appinstance:', CONVERT(nvarchar(36), ai.AppInstanceId)) AS RequirementKey,
        CAST(NULL AS nvarchar(500)) AS DesiredLocalPath
    FROM omp.AppInstances ai
    INNER JOIN omp.Artifacts ar ON ar.ArtifactId = ai.ArtifactId
    WHERE @includeAppInstanceArtifacts = 1
      AND ai.HostId = @hostId
      AND ai.IsEnabled = 1
      AND ai.IsAllowed = 1
      AND ar.IsEnabled = 1
      AND ar.RelativePath IS NOT NULL
      AND LTRIM(RTRIM(ar.RelativePath)) <> N''

    UNION ALL

    SELECT
        COALESCE(wi.HostId, ai.HostId) AS HostId,
        ar.ArtifactId,
        ar.Version,
        ar.PackageType,
        ar.TargetName,
        ar.RelativePath,
        ar.Sha256,
        CONCAT(N'workerinstance:', CONVERT(nvarchar(36), wi.WorkerInstanceId)) AS RequirementKey,
        CAST(NULL AS nvarchar(500)) AS DesiredLocalPath
    FROM omp.WorkerInstances wi
    INNER JOIN omp.AppInstances ai ON ai.AppInstanceId = wi.AppInstanceId
    INNER JOIN omp.Artifacts ar ON ar.ArtifactId = COALESCE(wi.ArtifactId, ai.ArtifactId)
    WHERE @includeAppInstanceArtifacts = 1
      AND COALESCE(wi.HostId, ai.HostId) = @hostId
      AND ai.IsEnabled = 1
      AND ai.IsAllowed = 1
      AND wi.IsEnabled = 1
      AND wi.IsAllowed = 1
      AND ar.IsEnabled = 1
      AND ar.RelativePath IS NOT NULL
      AND LTRIM(RTRIM(ar.RelativePath)) <> N''

    UNION ALL

    SELECT
        har.HostId,
        ar.ArtifactId,
        ar.Version,
        ar.PackageType,
        ar.TargetName,
        ar.RelativePath,
        ar.Sha256,
        har.RequirementKey,
        har.DesiredLocalPath
    FROM omp.HostArtifactRequirements har
    INNER JOIN omp.Artifacts ar ON ar.ArtifactId = har.ArtifactId
    WHERE @includeExplicitRequirements = 1
      AND har.HostId = @hostId
      AND har.IsEnabled = 1
      AND ar.IsEnabled = 1
      AND ar.RelativePath IS NOT NULL
      AND LTRIM(RTRIM(ar.RelativePath)) <> N''
)
SELECT TOP (@maxArtifacts)
    HostId,
    ArtifactId,
    Version,
    PackageType,
    TargetName,
    RelativePath,
    Sha256,
    MIN(RequirementKey) AS RequirementKey,
    MIN(DesiredLocalPath) AS DesiredLocalPath
FROM Desired
GROUP BY HostId, ArtifactId, Version, PackageType, TargetName, RelativePath, Sha256
ORDER BY PackageType, TargetName, Version, ArtifactId;";

        var result = new List<ArtifactDescriptor>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@includeAppInstanceArtifacts", includeAppInstanceArtifacts);
        cmd.Parameters.AddWithValue("@includeExplicitRequirements", includeExplicitRequirements);
        cmd.Parameters.AddWithValue("@maxArtifacts", maxArtifacts);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            result.Add(new ArtifactDescriptor
            {
                HostId = rdr.GetGuid(0),
                ArtifactId = rdr.GetInt32(1),
                Version = rdr.GetString(2),
                PackageType = rdr.GetString(3),
                TargetName = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                RelativePath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                Sha256 = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                RequirementKey = rdr.IsDBNull(7) ? string.Empty : rdr.GetString(7),
                DesiredLocalPath = rdr.IsDBNull(8) ? null : rdr.GetString(8)
            });
        }

        return result;
    }

    public async Task PublishResultAsync(
        ArtifactDescriptor artifact,
        ArtifactProvisioningResult result,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();

MERGE omp.HostArtifactStates AS target
USING (SELECT @hostId AS HostId, @artifactId AS ArtifactId) AS source
ON target.HostId = source.HostId
AND target.ArtifactId = source.ArtifactId
WHEN MATCHED THEN
    UPDATE SET
        ProvisioningState = @state,
        LocalPath = @localPath,
        ContentSha256 = @contentSha256,
        LastCheckedUtc = @nowUtc,
        LastProvisionedUtc = CASE WHEN @state = 2 THEN @nowUtc ELSE target.LastProvisionedUtc END,
        LastError = @lastError,
        UpdatedUtc = @nowUtc
WHEN NOT MATCHED THEN
    INSERT
    (
        HostId,
        ArtifactId,
        ProvisioningState,
        LocalPath,
        ContentSha256,
        LastCheckedUtc,
        LastProvisionedUtc,
        LastError,
        CreatedUtc,
        UpdatedUtc
    )
    VALUES
    (
        @hostId,
        @artifactId,
        @state,
        @localPath,
        @contentSha256,
        @nowUtc,
        CASE WHEN @state = 2 THEN @nowUtc ELSE NULL END,
        @lastError,
        @nowUtc,
        @nowUtc
    );";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostId", artifact.HostId);
        cmd.Parameters.AddWithValue("@artifactId", artifact.ArtifactId);
        cmd.Parameters.AddWithValue("@state", result.State);
        cmd.Parameters.AddWithValue("@localPath", string.IsNullOrWhiteSpace(result.LocalPath) ? (object)DBNull.Value : result.LocalPath);
        cmd.Parameters.AddWithValue("@contentSha256", string.IsNullOrWhiteSpace(result.ContentHash) ? (object)DBNull.Value : result.ContentHash);
        cmd.Parameters.AddWithValue("@lastError", string.IsNullOrWhiteSpace(result.ErrorMessage) ? (object)DBNull.Value : result.ErrorMessage);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
