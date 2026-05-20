using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class OmpHostArtifactRepository
{
    private readonly SqlConnectionFactory _db;

    public OmpHostArtifactRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public string GetConfiguredConnectionString()
        => _db.GetConnectionString();

    public async Task<ArtifactCompatibilitySlot> RequireCompatibleArtifactSlotAsync(
        int appId,
        string version,
        string packageType,
        string targetName,
        CancellationToken ct)
    {
        const string contextSql = @"
SELECT TOP (1)
       m.ModuleKey,
       a.AppKey,
       d.ModuleDefinitionDocumentId,
       d.DefinitionVersion
FROM omp.Apps a
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
LEFT JOIN omp.ModuleDefinitionDocuments d
    ON d.ModuleKey = m.ModuleKey
   AND d.IsApplied = 1
WHERE a.AppId = @appId
  AND a.IsEnabled = 1
  AND m.IsEnabled = 1
ORDER BY d.AppliedUtc DESC, d.UpdatedUtc DESC, d.ModuleDefinitionDocumentId DESC;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        string moduleKey;
        string appKey;
        int? moduleDefinitionDocumentId;
        string? definitionVersion;

        await using (var context = new SqlCommand(contextSql, conn))
        {
            context.Parameters.AddWithValue("@appId", appId);
            await using var rdr = await context.ExecuteReaderAsync(ct);
            if (!await rdr.ReadAsync(ct))
            {
                throw new InvalidOperationException("The selected app was not found.");
            }

            moduleKey = rdr.GetString(0);
            appKey = rdr.GetString(1);
            moduleDefinitionDocumentId = rdr.IsDBNull(2) ? null : rdr.GetInt32(2);
            definitionVersion = rdr.IsDBNull(3) ? null : rdr.GetString(3);
        }

        if (moduleDefinitionDocumentId is null || string.IsNullOrWhiteSpace(definitionVersion))
        {
            throw new InvalidOperationException(
                $"Module '{moduleKey}' has no applied module definition. Apply the module definition before importing artifacts for app '{appKey}'.");
        }

        const string slotSql = @"
SELECT AppKey,
       PackageType,
       TargetName,
       RelativePathTemplate,
       MinArtifactVersion,
       MaxArtifactVersion
FROM omp.ModuleDefinitionArtifactCompatibility
WHERE ModuleDefinitionDocumentId = @moduleDefinitionDocumentId
  AND AppKey = @appKey
  AND PackageType = @packageType
  AND ((TargetName = @targetName) OR (TargetName IS NULL AND @targetName IS NULL))
ORDER BY ModuleDefinitionArtifactCompatibilityId;";

        var slots = new List<ArtifactCompatibilitySlot>();
        await using (var slotCommand = new SqlCommand(slotSql, conn))
        {
            slotCommand.Parameters.AddWithValue("@moduleDefinitionDocumentId", moduleDefinitionDocumentId.Value);
            slotCommand.Parameters.AddWithValue("@appKey", appKey);
            slotCommand.Parameters.AddWithValue("@packageType", packageType);
            slotCommand.Parameters.AddWithValue("@targetName", string.IsNullOrWhiteSpace(targetName) ? DBNull.Value : targetName.Trim());

            await using var rdr = await slotCommand.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                slots.Add(
                    new ArtifactCompatibilitySlot(
                        moduleKey,
                        definitionVersion,
                        rdr.GetString(0),
                        rdr.GetString(1),
                        rdr.IsDBNull(2) ? null : rdr.GetString(2),
                        rdr.IsDBNull(3) ? null : rdr.GetString(3),
                        rdr.IsDBNull(4) ? null : rdr.GetString(4),
                        rdr.IsDBNull(5) ? null : rdr.GetString(5)));
            }
        }

        if (slots.Count == 0)
        {
            throw new InvalidOperationException(
                $"Module definition '{moduleKey}' version {definitionVersion} does not allow artifacts for app '{appKey}', package type '{packageType}', and target '{targetName}'.");
        }

        var compatible = slots.FirstOrDefault(slot =>
            IsVersionInRange(version, slot.MinArtifactVersion, slot.MaxArtifactVersion));
        if (compatible is null)
        {
            throw new InvalidOperationException(
                $"Artifact version {version} is not compatible with module definition '{moduleKey}' version {definitionVersion}. " +
                $"Allowed range: {FormatArtifactVersionRanges(slots)}.");
        }

        return compatible;
    }

    public async Task<ArtifactZipImportAppDescriptor?> ResolveArtifactZipImportAppAsync(
        string moduleKey,
        string appKey,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       a.AppId,
       m.ModuleKey,
       a.AppKey
FROM omp.Apps a
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
WHERE m.ModuleKey = @moduleKey
  AND a.AppKey = @appKey
  AND m.IsEnabled = 1
  AND a.IsEnabled = 1
ORDER BY a.AppId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@moduleKey", moduleKey);
        cmd.Parameters.AddWithValue("@appKey", appKey);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ArtifactZipImportAppDescriptor(
            rdr.GetInt32(0),
            rdr.GetString(1),
            rdr.GetString(2));
    }

    public async Task<ArtifactZipImportDuplicateInfo?> FindImportedArtifactBySha256Async(
        string sha256,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       ar.ArtifactId,
       a.AppKey,
       ar.Version,
       ar.PackageType,
       ar.TargetName
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
WHERE ar.Sha256 = @sha256
ORDER BY ar.ArtifactId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@sha256", sha256);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ArtifactZipImportDuplicateInfo(
            rdr.GetInt32(0),
            rdr.GetString(1),
            rdr.GetString(2),
            rdr.GetString(3),
            rdr.IsDBNull(4) ? null : rdr.GetString(4));
    }

    public async Task<ArtifactZipImportDuplicateInfo?> FindImportedArtifactByIdentityAsync(
        int appId,
        string version,
        string packageType,
        string targetName,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       ar.ArtifactId,
       a.AppKey,
       ar.Version,
       ar.PackageType,
       ar.TargetName
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
WHERE ar.AppId = @appId
  AND ar.Version = @version
  AND ar.PackageType = @packageType
  AND ((ar.TargetName = @targetName) OR (ar.TargetName IS NULL AND @targetName IS NULL))
ORDER BY ar.ArtifactId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@appId", appId);
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@packageType", packageType);
        cmd.Parameters.AddWithValue("@targetName", targetName);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ArtifactZipImportDuplicateInfo(
            rdr.GetInt32(0),
            rdr.GetString(1),
            rdr.GetString(2),
            rdr.GetString(3),
            rdr.IsDBNull(4) ? null : rdr.GetString(4));
    }

    public async Task<int> RegisterImportedArtifactAsync(
        int appId,
        string version,
        string packageType,
        string targetName,
        string relativePath,
        string sha256,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.Artifacts
(
    AppId,
    Version,
    PackageType,
    TargetName,
    RelativePath,
    Sha256,
    IsEnabled
)
VALUES
(
    @appId,
    @version,
    @packageType,
    @targetName,
    @relativePath,
    @sha256,
    1
);

SELECT CAST(SCOPE_IDENTITY() AS int);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@appId", appId);
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@packageType", packageType);
        cmd.Parameters.AddWithValue("@targetName", targetName);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);
        cmd.Parameters.AddWithValue("@sha256", sha256);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<int> CopyConfigurationFilesFromLatestPreviousArtifactAsync(
        int artifactId,
        int appId,
        string packageType,
        string targetName,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @SourceArtifactId int;

SELECT TOP (1)
       @SourceArtifactId = source.ArtifactId
FROM omp.Artifacts source
WHERE source.ArtifactId <> @artifactId
  AND source.AppId = @appId
  AND source.PackageType = @packageType
  AND ((source.TargetName = @targetName) OR (source.TargetName IS NULL AND @targetName IS NULL))
  AND source.IsEnabled = 1
  AND EXISTS
  (
      SELECT 1
      FROM omp.ArtifactConfigurationFiles sourceFile
      WHERE sourceFile.ArtifactId = source.ArtifactId
  )
ORDER BY source.CreatedUtc DESC, source.ArtifactId DESC;

IF @SourceArtifactId IS NULL
BEGIN
    SELECT CAST(0 AS int);
    RETURN;
END;

INSERT INTO omp.ArtifactConfigurationFiles
(
    ArtifactId,
    RelativePath,
    FileContent,
    IsEnabled
)
SELECT @artifactId,
       sourceFile.RelativePath,
       sourceFile.FileContent,
       sourceFile.IsEnabled
FROM omp.ArtifactConfigurationFiles sourceFile
WHERE sourceFile.ArtifactId = @SourceArtifactId
  AND NOT EXISTS
  (
      SELECT 1
      FROM omp.ArtifactConfigurationFiles targetFile
      WHERE targetFile.ArtifactId = @artifactId
        AND targetFile.RelativePath = sourceFile.RelativePath
  );

SELECT @@ROWCOUNT;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        cmd.Parameters.AddWithValue("@appId", appId);
        cmd.Parameters.AddWithValue("@packageType", packageType);
        cmd.Parameters.AddWithValue("@targetName", targetName);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<int> ReplaceArtifactConfigurationFilesAsync(
        int artifactId,
        IReadOnlyList<ArtifactPackageConfigurationFile> configurationFiles,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using (var delete = new SqlCommand(
            "DELETE FROM omp.ArtifactConfigurationFiles WHERE ArtifactId = @artifactId;",
            conn,
            tx))
        {
            delete.Parameters.AddWithValue("@artifactId", artifactId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        const string insertSql = @"
INSERT INTO omp.ArtifactConfigurationFiles
(
    ArtifactId,
    RelativePath,
    FileContent,
    IsEnabled
)
VALUES
(
    @artifactId,
    @relativePath,
    @fileContent,
    1
);";

        foreach (var configurationFile in configurationFiles)
        {
            await using var insert = new SqlCommand(insertSql, conn, tx);
            insert.Parameters.AddWithValue("@artifactId", artifactId);
            insert.Parameters.AddWithValue("@relativePath", configurationFile.RelativePath);
            insert.Parameters.AddWithValue("@fileContent", configurationFile.FileContent);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return configurationFiles.Count;
    }

    public async Task<(int TemplateAppRowsUpdated, int AppInstanceRowsUpdated, int WorkerInstanceRowsUpdated)> ApplyImportedArtifactToMatchingApplicationsAsync(
        int artifactId,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @AppId int;
DECLARE @TemplateAppRowsUpdated int = 0;
DECLARE @AppInstanceRowsUpdated int = 0;
DECLARE @WorkerInstanceRowsUpdated int = 0;

SELECT @AppId = AppId
FROM omp.Artifacts
WHERE ArtifactId = @artifactId
  AND IsEnabled = 1;

IF @AppId IS NOT NULL
BEGIN
    UPDATE omp.InstanceTemplateAppInstances
    SET DesiredArtifactId = @artifactId,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppId = @AppId
      AND IsEnabled = 1
      AND ISNULL(DesiredArtifactId, -1) <> @artifactId;

    SET @TemplateAppRowsUpdated = @@ROWCOUNT;

    UPDATE omp.AppInstances
    SET ArtifactId = @artifactId,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppId = @AppId
      AND IsEnabled = 1
      AND ISNULL(ArtifactId, -1) <> @artifactId;

    SET @AppInstanceRowsUpdated = @@ROWCOUNT;

    UPDATE wi
    SET ArtifactId = @artifactId,
        UpdatedUtc = SYSUTCDATETIME()
    FROM omp.WorkerInstances wi
    INNER JOIN omp.AppInstances ai ON ai.AppInstanceId = wi.AppInstanceId
    WHERE ai.AppId = @AppId
      AND wi.IsEnabled = 1
      AND wi.ArtifactId IS NOT NULL
      AND wi.ArtifactId <> @artifactId;

    SET @WorkerInstanceRowsUpdated = @@ROWCOUNT;
END;

SELECT @TemplateAppRowsUpdated,
       @AppInstanceRowsUpdated,
       @WorkerInstanceRowsUpdated;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return (0, 0, 0);
        }

        return (rdr.GetInt32(0), rdr.GetInt32(1), rdr.GetInt32(2));
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
        CAST(NULL AS nvarchar(128)) AS HostKey,
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
    @hostKey AS HostKey,
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
                HostKey = rdr.GetString(1),
                AppInstanceId = rdr.GetGuid(2),
                AppInstanceKey = rdr.GetString(3),
                DisplayName = rdr.GetString(4),
                RoutePath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                InstallPath = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                InstallationName = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                ArtifactId = rdr.GetInt32(8),
                Version = rdr.GetString(9),
                TargetName = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                SourceLocalPath = rdr.GetString(11),
                ContentSha256 = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                DeployedArtifactId = rdr.IsDBNull(13) ? null : rdr.GetInt32(13),
                DeploymentState = rdr.IsDBNull(14) ? null : rdr.GetByte(14),
                DeployedSourceLocalPath = rdr.IsDBNull(15) ? null : rdr.GetString(15),
                DeployedTargetPath = rdr.IsDBNull(16) ? null : rdr.GetString(16),
                DeployedRuntimeName = rdr.IsDBNull(17) ? null : rdr.GetString(17)
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
        CAST(NULL AS nvarchar(128)) AS HostKey,
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
    @hostKey AS HostKey,
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
                HostKey = rdr.GetString(1),
                AppInstanceId = rdr.GetGuid(2),
                AppInstanceKey = rdr.GetString(3),
                DisplayName = rdr.GetString(4),
                Description = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                InstallPath = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                InstallationName = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                ArtifactId = rdr.GetInt32(8),
                Version = rdr.GetString(9),
                TargetName = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                SourceLocalPath = rdr.GetString(11),
                ContentSha256 = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                DeployedArtifactId = rdr.IsDBNull(13) ? null : rdr.GetInt32(13),
                DeploymentState = rdr.IsDBNull(14) ? null : rdr.GetByte(14),
                DeployedSourceLocalPath = rdr.IsDBNull(15) ? null : rdr.GetString(15),
                DeployedTargetPath = rdr.IsDBNull(16) ? null : rdr.GetString(16),
                DeployedRuntimeName = rdr.IsDBNull(17) ? null : rdr.GetString(17)
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

    private static bool IsVersionInRange(string version, string? minVersion, string? maxVersion)
    {
        if (!string.IsNullOrWhiteSpace(minVersion)
            && CompareArtifactVersions(version, minVersion) < 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(maxVersion)
            && CompareArtifactVersions(version, maxVersion) > 0)
        {
            return false;
        }

        return true;
    }

    private static int CompareArtifactVersions(string left, string right)
    {
        if (TryParseComparableVersion(left, out var leftVersion)
            && TryParseComparableVersion(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseComparableVersion(string? value, out Version version)
    {
        var text = value?.Trim() ?? string.Empty;
        var suffixIndex = text.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            text = text[..suffixIndex];
        }

        return Version.TryParse(text, out version!);
    }

    private static string FormatArtifactVersionRanges(IEnumerable<ArtifactCompatibilitySlot> slots)
        => string.Join(
            ", ",
            slots.Select(slot =>
            {
                var min = string.IsNullOrWhiteSpace(slot.MinArtifactVersion) ? "*" : slot.MinArtifactVersion;
                var max = string.IsNullOrWhiteSpace(slot.MaxArtifactVersion) ? "*" : slot.MaxArtifactVersion;
                return $"{min}..{max}";
            }));
}
