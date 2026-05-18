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

    public async Task<TemplateMaterializationResult> MaterializeTemplatesForHostAsync(
        string hostKey,
        int? hostTemplateId,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.MaterializeInstanceTemplate', N'P') IS NULL
BEGIN
    SELECT CAST(0 AS int) AS ModuleInstanceChanges,
           CAST(0 AS int) AS AppInstanceChanges;
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM omp.Hosts
    WHERE HostKey = @hostKey
      AND IsEnabled = 1
)
BEGIN
    SELECT CAST(0 AS int) AS ModuleInstanceChanges,
           CAST(0 AS int) AS AppInstanceChanges;
    RETURN;
END;

EXEC omp.MaterializeInstanceTemplate
    @HostKey = @hostKey,
    @HostTemplateId = @hostTemplateId,
    @RequestedBy = N'HostAgent';";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@hostTemplateId", hostTemplateId.HasValue ? hostTemplateId.Value : DBNull.Value);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return new TemplateMaterializationResult(0, 0);
        }

        return new TemplateMaterializationResult(
            rdr.GetInt32(0),
            rdr.GetInt32(1));
    }

    public async Task<HostDeploymentWorkItem?> TryClaimNextHostDeploymentAsync(
        string hostKey,
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
        CAST(NULL AS bigint) AS HostDeploymentId,
        CAST(NULL AS int) AS HostTemplateId,
        CAST(NULL AS nvarchar(100)) AS HostTemplateKey;
    RETURN;
END;

DECLARE @claimed TABLE
(
    HostDeploymentId bigint NOT NULL,
    HostTemplateId int NULL
);

;WITH NextDeployment AS
(
    SELECT TOP (1) HostDeploymentId
    FROM omp.HostDeployments WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE HostId = @hostId
      AND Status = @pendingStatus
    ORDER BY RequestedUtc, HostDeploymentId
)
UPDATE d
SET Status = @runningStatus,
    StartedUtc = COALESCE(d.StartedUtc, SYSUTCDATETIME()),
    CompletedUtc = NULL,
    OutcomeMessage = NULL,
    UpdatedUtc = SYSUTCDATETIME()
OUTPUT inserted.HostDeploymentId,
       inserted.HostTemplateId
INTO @claimed(HostDeploymentId, HostTemplateId)
FROM omp.HostDeployments d
INNER JOIN NextDeployment n ON n.HostDeploymentId = d.HostDeploymentId;

SELECT c.HostDeploymentId,
       c.HostTemplateId,
       ht.TemplateKey
FROM @claimed c
LEFT JOIN omp.HostTemplates ht ON ht.HostTemplateId = c.HostTemplateId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@pendingStatus", HostDeploymentStatuses.Pending);
        cmd.Parameters.AddWithValue("@runningStatus", HostDeploymentStatuses.Running);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new HostDeploymentWorkItem(
            rdr.GetInt64(0),
            rdr.IsDBNull(1) ? null : rdr.GetInt32(1),
            rdr.IsDBNull(2) ? null : rdr.GetString(2));
    }

    public async Task CompleteHostDeploymentAsync(
        long hostDeploymentId,
        bool succeeded,
        string outcomeMessage,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.HostDeployments
SET Status = @status,
    CompletedUtc = SYSUTCDATETIME(),
    OutcomeMessage = @outcomeMessage,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostDeploymentId = @hostDeploymentId
  AND Status = @runningStatus;";

        var safeMessage = string.IsNullOrWhiteSpace(outcomeMessage)
            ? null
            : outcomeMessage.Trim();
        if (safeMessage?.Length > 4000)
        {
            safeMessage = safeMessage[..4000];
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostDeploymentId", hostDeploymentId);
        cmd.Parameters.AddWithValue("@status", succeeded ? HostDeploymentStatuses.Succeeded : HostDeploymentStatuses.Failed);
        cmd.Parameters.AddWithValue("@runningStatus", HostDeploymentStatuses.Running);
        cmd.Parameters.AddWithValue("@outcomeMessage", string.IsNullOrWhiteSpace(safeMessage) ? DBNull.Value : safeMessage);
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

    public async Task<IReadOnlyList<ArtifactConfigurationFileDescriptor>> GetArtifactConfigurationFilesAsync(
        int artifactId,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.ArtifactConfigurationFiles', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS int) AS ArtifactConfigurationFileId,
        CAST(NULL AS int) AS ArtifactId,
        CAST(NULL AS nvarchar(400)) AS RelativePath,
        CAST(NULL AS nvarchar(max)) AS FileContent;
    RETURN;
END;

SELECT ArtifactConfigurationFileId,
       ArtifactId,
       RelativePath,
       FileContent
FROM omp.ArtifactConfigurationFiles
WHERE ArtifactId = @artifactId
  AND IsEnabled = 1
ORDER BY RelativePath, ArtifactConfigurationFileId;";

        var rows = new List<ArtifactConfigurationFileDescriptor>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ArtifactConfigurationFileDescriptor
            {
                ArtifactConfigurationFileId = rdr.GetInt32(0),
                ArtifactId = rdr.GetInt32(1),
                RelativePath = rdr.GetString(2),
                FileContent = rdr.GetString(3)
            });
        }

        return rows;
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
      -- Host-neutral app instances are provisioned on every enabled host while
      -- still remaining one logical app entry in the portal/topbar.
      AND (ai.HostId = @hostId OR ai.HostId IS NULL)
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

    public async Task<IReadOnlyList<WebAppDeploymentDescriptor>> GetDesiredWebAppDeploymentsAsync(
        string hostKey,
        int maxDeployments,
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
        CAST(NULL AS uniqueidentifier) AS AppInstanceId,
        CAST(NULL AS nvarchar(100)) AS AppInstanceKey,
        CAST(NULL AS nvarchar(200)) AS DisplayName,
        CAST(NULL AS nvarchar(256)) AS RoutePath,
        CAST(NULL AS nvarchar(500)) AS InstallPath,
        CAST(NULL AS nvarchar(150)) AS InstallationName,
        CAST(NULL AS int) AS ArtifactId,
        CAST(NULL AS nvarchar(50)) AS Version,
        CAST(NULL AS nvarchar(100)) AS TargetName,
        CAST(NULL AS nvarchar(500)) AS SourceLocalPath,
        CAST(NULL AS nvarchar(128)) AS ContentSha256,
        CAST(NULL AS int) AS DeployedArtifactId,
        CAST(NULL AS tinyint) AS DeploymentState,
        CAST(NULL AS nvarchar(500)) AS DeployedSourceLocalPath,
        CAST(NULL AS nvarchar(500)) AS DeployedTargetPath,
        CAST(NULL AS nvarchar(200)) AS DeployedRuntimeName;
    RETURN;
END;

SELECT TOP (@maxDeployments)
    @hostId AS HostId,
    ai.AppInstanceId,
    ai.AppInstanceKey,
    ai.DisplayName,
    ai.RoutePath,
    ai.InstallPath,
    ai.InstallationName,
    ar.ArtifactId,
    ar.Version,
    ar.TargetName,
    has.LocalPath AS SourceLocalPath,
    has.ContentSha256,
    hds.ArtifactId AS DeployedArtifactId,
    hds.DeploymentState,
    hds.SourceLocalPath AS DeployedSourceLocalPath,
    hds.TargetPath AS DeployedTargetPath,
    hds.RuntimeName AS DeployedRuntimeName
FROM omp.AppInstances ai
INNER JOIN omp.Artifacts ar ON ar.ArtifactId = ai.ArtifactId
INNER JOIN omp.HostArtifactStates has
    ON has.HostId = @hostId
   AND has.ArtifactId = ar.ArtifactId
LEFT JOIN omp.HostAppDeploymentStates hds
    ON hds.HostId = @hostId
   AND hds.AppInstanceId = ai.AppInstanceId
-- Host-neutral web apps are still applied per host through
-- HostAppDeploymentStates(HostId, AppInstanceId).
WHERE (ai.HostId = @hostId OR ai.HostId IS NULL)
  AND ai.IsEnabled = 1
  AND ai.IsAllowed = 1
  AND ai.DesiredState = 1
  AND ar.IsEnabled = 1
  AND ar.PackageType = N'web-app'
  AND has.ProvisioningState = @succeededState
  AND has.LocalPath IS NOT NULL
  AND LTRIM(RTRIM(has.LocalPath)) <> N''
ORDER BY ai.SortOrder, ai.AppInstanceKey;";

        var result = new List<WebAppDeploymentDescriptor>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@maxDeployments", maxDeployments);
        cmd.Parameters.AddWithValue("@succeededState", ArtifactProvisioningState.Succeeded);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            result.Add(new WebAppDeploymentDescriptor
            {
                HostId = rdr.GetGuid(0),
                AppInstanceId = rdr.GetGuid(1),
                AppInstanceKey = rdr.GetString(2),
                DisplayName = rdr.GetString(3),
                RoutePath = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                InstallPath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                InstallationName = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                ArtifactId = rdr.GetInt32(7),
                Version = rdr.GetString(8),
                TargetName = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                SourceLocalPath = rdr.GetString(10),
                ContentSha256 = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                DeployedArtifactId = rdr.IsDBNull(12) ? null : rdr.GetInt32(12),
                DeploymentState = rdr.IsDBNull(13) ? null : rdr.GetByte(13),
                DeployedSourceLocalPath = rdr.IsDBNull(14) ? null : rdr.GetString(14),
                DeployedTargetPath = rdr.IsDBNull(15) ? null : rdr.GetString(15),
                DeployedRuntimeName = rdr.IsDBNull(16) ? null : rdr.GetString(16)
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<ServiceAppDeploymentDescriptor>> GetDesiredServiceAppDeploymentsAsync(
        string hostKey,
        int maxDeployments,
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
        CAST(NULL AS uniqueidentifier) AS AppInstanceId,
        CAST(NULL AS nvarchar(100)) AS AppInstanceKey,
        CAST(NULL AS nvarchar(200)) AS DisplayName,
        CAST(NULL AS nvarchar(1000)) AS Description,
        CAST(NULL AS nvarchar(500)) AS InstallPath,
        CAST(NULL AS nvarchar(150)) AS InstallationName,
        CAST(NULL AS int) AS ArtifactId,
        CAST(NULL AS nvarchar(50)) AS Version,
        CAST(NULL AS nvarchar(100)) AS TargetName,
        CAST(NULL AS nvarchar(500)) AS SourceLocalPath,
        CAST(NULL AS nvarchar(128)) AS ContentSha256,
        CAST(NULL AS int) AS DeployedArtifactId,
        CAST(NULL AS tinyint) AS DeploymentState,
        CAST(NULL AS nvarchar(500)) AS DeployedSourceLocalPath,
        CAST(NULL AS nvarchar(500)) AS DeployedTargetPath,
        CAST(NULL AS nvarchar(200)) AS DeployedRuntimeName;
    RETURN;
END;

SELECT TOP (@maxDeployments)
    @hostId AS HostId,
    ai.AppInstanceId,
    ai.AppInstanceKey,
    ai.DisplayName,
    ai.Description,
    ai.InstallPath,
    ai.InstallationName,
    ar.ArtifactId,
    ar.Version,
    ar.TargetName,
    has.LocalPath AS SourceLocalPath,
    has.ContentSha256,
    hds.ArtifactId AS DeployedArtifactId,
    hds.DeploymentState,
    hds.SourceLocalPath AS DeployedSourceLocalPath,
    hds.TargetPath AS DeployedTargetPath,
    hds.RuntimeName AS DeployedRuntimeName
FROM omp.AppInstances ai
INNER JOIN omp.Artifacts ar ON ar.ArtifactId = ai.ArtifactId
INNER JOIN omp.HostArtifactStates has
    ON has.HostId = @hostId
   AND has.ArtifactId = ar.ArtifactId
LEFT JOIN omp.HostAppDeploymentStates hds
    ON hds.HostId = @hostId
   AND hds.AppInstanceId = ai.AppInstanceId
WHERE ai.HostId = @hostId
  AND ai.IsEnabled = 1
  AND ai.IsAllowed = 1
  AND ai.DesiredState = 1
  AND ar.IsEnabled = 1
  AND ar.PackageType = N'service-app'
  AND has.ProvisioningState = @succeededState
  AND has.LocalPath IS NOT NULL
  AND LTRIM(RTRIM(has.LocalPath)) <> N''
ORDER BY ai.SortOrder, ai.AppInstanceKey;";

        var result = new List<ServiceAppDeploymentDescriptor>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@maxDeployments", maxDeployments);
        cmd.Parameters.AddWithValue("@succeededState", ArtifactProvisioningState.Succeeded);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            result.Add(new ServiceAppDeploymentDescriptor
            {
                HostId = rdr.GetGuid(0),
                AppInstanceId = rdr.GetGuid(1),
                AppInstanceKey = rdr.GetString(2),
                DisplayName = rdr.GetString(3),
                Description = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                InstallPath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                InstallationName = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                ArtifactId = rdr.GetInt32(7),
                Version = rdr.GetString(8),
                TargetName = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                SourceLocalPath = rdr.GetString(10),
                ContentSha256 = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                DeployedArtifactId = rdr.IsDBNull(12) ? null : rdr.GetInt32(12),
                DeploymentState = rdr.IsDBNull(13) ? null : rdr.GetByte(13),
                DeployedSourceLocalPath = rdr.IsDBNull(14) ? null : rdr.GetString(14),
                DeployedTargetPath = rdr.IsDBNull(15) ? null : rdr.GetString(15),
                DeployedRuntimeName = rdr.IsDBNull(16) ? null : rdr.GetString(16)
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

    public async Task PublishAppDeploymentResultAsync(
        WebAppDeploymentDescriptor deployment,
        AppDeploymentResult result,
        CancellationToken ct)
        => await PublishAppDeploymentResultCoreAsync(
            deployment.HostId,
            deployment.AppInstanceId,
            deployment.ArtifactId,
            deployment.SourceLocalPath,
            deployment.ContentSha256,
            result,
            ct);

    public async Task PublishAppDeploymentResultAsync(
        ServiceAppDeploymentDescriptor deployment,
        AppDeploymentResult result,
        CancellationToken ct)
        => await PublishAppDeploymentResultCoreAsync(
            deployment.HostId,
            deployment.AppInstanceId,
            deployment.ArtifactId,
            deployment.SourceLocalPath,
            deployment.ContentSha256,
            result,
            ct);

    private async Task PublishAppDeploymentResultCoreAsync(
        Guid hostId,
        Guid appInstanceId,
        int artifactId,
        string sourceLocalPath,
        string? contentSha256,
        AppDeploymentResult result,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();

MERGE omp.HostAppDeploymentStates AS target
USING (SELECT @hostId AS HostId, @appInstanceId AS AppInstanceId) AS source
ON target.HostId = source.HostId
AND target.AppInstanceId = source.AppInstanceId
WHEN MATCHED THEN
    UPDATE SET
        ArtifactId = @artifactId,
        DeploymentState = @state,
        SourceLocalPath = @sourceLocalPath,
        TargetPath = @targetPath,
        RuntimeName = @runtimeName,
        ContentSha256 = @contentSha256,
        LastCheckedUtc = @nowUtc,
        LastAppliedUtc = CASE WHEN @applied = 1 THEN @nowUtc ELSE target.LastAppliedUtc END,
        LastError = @lastError,
        UpdatedUtc = @nowUtc
WHEN NOT MATCHED THEN
    INSERT
    (
        HostId,
        AppInstanceId,
        ArtifactId,
        DeploymentState,
        SourceLocalPath,
        TargetPath,
        RuntimeName,
        ContentSha256,
        LastCheckedUtc,
        LastAppliedUtc,
        LastError,
        CreatedUtc,
        UpdatedUtc
    )
    VALUES
    (
        @hostId,
        @appInstanceId,
        @artifactId,
        @state,
        @sourceLocalPath,
        @targetPath,
        @runtimeName,
        @contentSha256,
        @nowUtc,
        CASE WHEN @applied = 1 THEN @nowUtc ELSE NULL END,
        @lastError,
        @nowUtc,
        @nowUtc
    );";

        var safeMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? null
            : result.ErrorMessage.Trim();
        if (safeMessage?.Length > 4000)
        {
            safeMessage = safeMessage[..4000];
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        cmd.Parameters.AddWithValue("@state", result.State);
        cmd.Parameters.AddWithValue("@sourceLocalPath", sourceLocalPath);
        cmd.Parameters.AddWithValue("@targetPath", string.IsNullOrWhiteSpace(result.TargetPath) ? (object)DBNull.Value : result.TargetPath);
        cmd.Parameters.AddWithValue("@runtimeName", string.IsNullOrWhiteSpace(result.RuntimeName) ? (object)DBNull.Value : result.RuntimeName);
        cmd.Parameters.AddWithValue("@contentSha256", string.IsNullOrWhiteSpace(contentSha256) ? (object)DBNull.Value : contentSha256);
        cmd.Parameters.AddWithValue("@applied", result.Applied);
        cmd.Parameters.AddWithValue("@lastError", string.IsNullOrWhiteSpace(safeMessage) ? DBNull.Value : safeMessage);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
