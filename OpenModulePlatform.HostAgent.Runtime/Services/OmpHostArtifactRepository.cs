using System.Security.Cryptography;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed partial class OmpHostArtifactRepository
{
    private const string BootstrapPortalAdminPrincipalPlaceholder = "__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__";
    private const int HostAgentRuntimeServiceNameMaxLength = 200;
    private const int HostAgentRuntimeVersionMaxLength = 50;
    private const int HostAgentRuntimeInstallPathMaxLength = 500;
    private const int HostAgentRuntimeModeMaxLength = 40;
    private const int HostAgentRuntimeStatusMessageMaxLength = 1000;
    private const int StoredDiagnosticMessageMaxLength = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    // Short repository-local alias for the SQL connection factory; all database access still creates scoped SqlConnection instances.
    private readonly SqlConnectionFactory _db;

    public OmpHostArtifactRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public string GetConfiguredConnectionString()
        => _db.GetConnectionString();

    public async Task<HostAgentLeaseResult> TryAcquireHostAgentLeaseAsync(
        string hostKey,
        string serviceName,
        string runtimeMode,
        bool forceTakeover,
        int leaseSeconds,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();
DECLARE @leaseUntilUtc datetime2(3) = DATEADD(second, @leaseSeconds, @nowUtc);
DECLARE @hostId uniqueidentifier;
DECLARE @existingServiceName nvarchar(200);
DECLARE @existingLeaseUntilUtc datetime2(3);

SELECT @hostId = HostId
FROM omp.Hosts
WHERE HostKey = @hostKey
  AND IsEnabled = 1;

IF @hostId IS NULL
BEGIN
    SELECT
        CAST(0 AS bit) AS Acquired,
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS uniqueidentifier) AS LeaseToken,
        CAST(NULL AS nvarchar(200)) AS ActiveServiceName;
    RETURN;
END;

SELECT
    @existingServiceName = ServiceName,
    @existingLeaseUntilUtc = LeaseUntilUtc
FROM omp.HostAgentLeases WITH (UPDLOCK, HOLDLOCK)
WHERE HostId = @hostId;

IF @existingServiceName IS NULL
BEGIN
    INSERT INTO omp.HostAgentLeases(HostId, ServiceName, LeaseToken, RuntimeMode, LeaseUntilUtc)
    VALUES(@hostId, @serviceName, @leaseToken, @runtimeMode, @leaseUntilUtc);

    SELECT CAST(1 AS bit) AS Acquired, @hostId AS HostId, @leaseToken AS LeaseToken, @serviceName AS ActiveServiceName;
    RETURN;
END;

IF @forceTakeover = 1
   OR @existingServiceName = @serviceName
   OR @existingLeaseUntilUtc < @nowUtc
BEGIN
    UPDATE omp.HostAgentLeases
    SET ServiceName = @serviceName,
        LeaseToken = @leaseToken,
        RuntimeMode = @runtimeMode,
        LeaseUntilUtc = @leaseUntilUtc,
        UpdatedUtc = @nowUtc
    WHERE HostId = @hostId;

    SELECT CAST(1 AS bit) AS Acquired, @hostId AS HostId, @leaseToken AS LeaseToken, @serviceName AS ActiveServiceName;
    RETURN;
END;

SELECT CAST(0 AS bit) AS Acquired, @hostId AS HostId, CAST(NULL AS uniqueidentifier) AS LeaseToken, @existingServiceName AS ActiveServiceName;";

        var token = Guid.NewGuid();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@serviceName", serviceName);
        cmd.Parameters.AddWithValue("@runtimeMode", runtimeMode);
        cmd.Parameters.AddWithValue("@forceTakeover", forceTakeover);
        cmd.Parameters.AddWithValue("@leaseSeconds", Math.Max(5, leaseSeconds));
        cmd.Parameters.AddWithValue("@leaseToken", token);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return new HostAgentLeaseResult(false, null, null, null);
        }

        return new HostAgentLeaseResult(
            rdr.GetBoolean(0),
            rdr.IsDBNull(1) ? null : rdr.GetGuid(1),
            rdr.IsDBNull(2) ? null : rdr.GetGuid(2),
            rdr.IsDBNull(3) ? null : rdr.GetString(3));
    }

    public async Task ReleaseHostAgentLeaseAsync(
        Guid hostId,
        string serviceName,
        CancellationToken ct)
    {
        const string sql = @"
DELETE FROM omp.HostAgentLeases
WHERE HostId = @hostId
  AND ServiceName = @serviceName;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        cmd.Parameters.AddWithValue("@serviceName", serviceName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PublishHostAgentRuntimeStateAsync(
        Guid hostId,
        HostAgentProcessContext process,
        string runtimeMode,
        int? artifactId,
        string? installPath,
        bool isActive,
        string? statusMessage,
        CancellationToken ct,
        bool preserveExistingStatusMessage = false)
    {
        const string sql = @"
DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();

IF @isActive = 1
BEGIN
    UPDATE omp.HostAgentRuntimeStates
    SET IsActive = 0,
        UpdatedUtc = @nowUtc
    WHERE HostId = @hostId
      AND ServiceName <> @serviceName
      AND IsActive = 1;
END;

MERGE omp.HostAgentRuntimeStates AS target
USING (SELECT @hostId AS HostId, @serviceName AS ServiceName) AS source
ON target.HostId = source.HostId
AND target.ServiceName = source.ServiceName
WHEN MATCHED THEN
    UPDATE SET
        Version = @version,
        ArtifactId = @artifactId,
        InstallPath = @installPath,
        ProcessId = @processId,
        RuntimeMode = @runtimeMode,
        IsActive = @isActive,
        TakeoverFromServiceName = @takeoverFromServiceName,
        LastSeenUtc = @nowUtc,
        StatusMessage = CASE WHEN @preserveExistingStatusMessage = 1 THEN target.StatusMessage ELSE @statusMessage END,
        UpdatedUtc = @nowUtc
WHEN NOT MATCHED THEN
    INSERT
    (
        HostId,
        ServiceName,
        Version,
        ArtifactId,
        InstallPath,
        ProcessId,
        RuntimeMode,
        IsActive,
        TakeoverFromServiceName,
        LastSeenUtc,
        StatusMessage,
        CreatedUtc,
        UpdatedUtc
    )
    VALUES
    (
        @hostId,
        @serviceName,
        @version,
        @artifactId,
        @installPath,
        @processId,
        @runtimeMode,
        @isActive,
        @takeoverFromServiceName,
        @nowUtc,
        @statusMessage,
        @nowUtc,
        @nowUtc
    );";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@hostId", SqlDbType.UniqueIdentifier, hostId);
        Add(cmd, "@serviceName", SqlDbType.NVarChar, HostAgentRuntimeServiceNameMaxLength, process.ServiceName);
        Add(cmd, "@version", SqlDbType.NVarChar, HostAgentRuntimeVersionMaxLength, NullIfWhiteSpace(process.Version));
        Add(cmd, "@artifactId", SqlDbType.Int, artifactId);
        Add(cmd, "@installPath", SqlDbType.NVarChar, HostAgentRuntimeInstallPathMaxLength, NullIfWhiteSpace(installPath));
        Add(cmd, "@processId", SqlDbType.Int, process.ProcessId);
        Add(cmd, "@runtimeMode", SqlDbType.NVarChar, HostAgentRuntimeModeMaxLength, runtimeMode);
        Add(cmd, "@isActive", SqlDbType.Bit, isActive);
        var takeoverFromServiceName = string.Equals(runtimeMode, HostAgentRuntimeMode.Normal, StringComparison.OrdinalIgnoreCase)
            ? null
            : process.TakeoverFromServiceName;
        Add(cmd, "@takeoverFromServiceName", SqlDbType.NVarChar, HostAgentRuntimeServiceNameMaxLength, NullIfWhiteSpace(takeoverFromServiceName));
        Add(cmd, "@statusMessage", SqlDbType.NVarChar, HostAgentRuntimeStatusMessageMaxLength, NullIfWhiteSpace(Truncate(statusMessage ?? string.Empty, HostAgentRuntimeStatusMessageMaxLength)));
        Add(cmd, "@preserveExistingStatusMessage", SqlDbType.Bit, preserveExistingStatusMessage);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkHostAgentRetiredAsync(
        Guid hostId,
        string serviceName,
        string retiredByServiceName,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.HostAgentRuntimeStates
SET RuntimeMode = N'Quiesced',
    IsActive = 0,
    QuiescedUtc = COALESCE(QuiescedUtc, SYSUTCDATETIME()),
    StatusMessage = @statusMessage,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostId = @hostId
  AND ServiceName = @serviceName;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        cmd.Parameters.AddWithValue("@serviceName", serviceName);
        var retiredBy = SanitizeStatusMessageFragment(retiredByServiceName);
        Add(cmd, "@statusMessage", SqlDbType.NVarChar, HostAgentRuntimeStatusMessageMaxLength, Truncate($"Retired by {retiredBy}.", HostAgentRuntimeStatusMessageMaxLength));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkHostAgentQuiescedAsync(
        Guid hostId,
        string serviceName,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.HostAgentRuntimeStates
SET RuntimeMode = N'Quiesced',
    IsActive = 0,
    QuiescedUtc = SYSUTCDATETIME(),
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostId = @hostId
  AND ServiceName = @serviceName;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        cmd.Parameters.AddWithValue("@serviceName", serviceName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RequestHostAgentQuiesceAsync(
        Guid hostId,
        string serviceName,
        string requestedBy,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.HostAgentRuntimeStates
SET RuntimeMode = N'Quiescing',
    QuiesceRequestedUtc = SYSUTCDATETIME(),
    StatusMessage = @requestedBy,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostId = @hostId
  AND ServiceName = @serviceName;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        cmd.Parameters.AddWithValue("@serviceName", serviceName);
        cmd.Parameters.AddWithValue("@requestedBy", Truncate(requestedBy, 1000));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<HostAgentUpgradeDescriptor?> GetDesiredHostAgentUpgradeAsync(
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
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS int) AS ArtifactId,
        CAST(NULL AS nvarchar(50)) AS Version,
        CAST(NULL AS nvarchar(50)) AS PackageType,
        CAST(NULL AS nvarchar(100)) AS TargetName,
        CAST(NULL AS nvarchar(400)) AS RelativePath,
        CAST(NULL AS nvarchar(128)) AS Sha256,
        CAST(NULL AS nvarchar(160)) AS ServiceNamePrefix,
        CAST(NULL AS nvarchar(500)) AS InstallRoot,
        CAST(NULL AS nvarchar(500)) AS SourceLocalPath,
        CAST(NULL AS nvarchar(128)) AS ContentSha256;
    RETURN;
END;

SELECT TOP (1)
    @hostId AS HostId,
    ar.ArtifactId,
    ar.Version,
    ar.PackageType,
    ar.TargetName,
    ar.RelativePath,
    ar.Sha256,
    desired.ServiceNamePrefix,
    desired.InstallRoot,
    has.LocalPath AS SourceLocalPath,
    has.ContentSha256
FROM omp.HostAgentDesiredStates desired
INNER JOIN omp.Artifacts ar ON ar.ArtifactId = desired.ArtifactId
LEFT JOIN omp.HostArtifactStates has
    ON has.HostId = @hostId
   AND has.ArtifactId = ar.ArtifactId
WHERE desired.HostId = @hostId
  AND desired.IsEnabled = 1
  AND ar.IsEnabled = 1
ORDER BY desired.UpdatedUtc DESC;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new HostAgentUpgradeDescriptor
        {
            HostId = rdr.GetGuid(0),
            ArtifactId = rdr.GetInt32(1),
            Version = rdr.GetString(2),
            PackageType = rdr.GetString(3),
            TargetName = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            RelativePath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            Sha256 = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            ServiceNamePrefix = rdr.IsDBNull(7) ? null : rdr.GetString(7),
            InstallRoot = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            SourceLocalPath = rdr.IsDBNull(9) ? null : rdr.GetString(9),
            ContentSha256 = rdr.IsDBNull(10) ? null : rdr.GetString(10)
        };
    }

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

    public async Task<ModuleDefinitionSaveResult> SaveImportedModuleDefinitionAsync(
        ModuleDefinitionImportDocument input,
        bool replaceExisting,
        CancellationToken ct)
    {
        const string findSql = @"
SELECT ModuleDefinitionDocumentId,
       DefinitionSha256
FROM omp.ModuleDefinitionDocuments
WHERE ModuleKey = @moduleKey
  AND DefinitionVersion = @definitionVersion;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            int? existingId = null;
            string? existingSha256 = null;
            await using (var find = new SqlCommand(findSql, conn, tx))
            {
                Add(find, "@moduleKey", input.ModuleKey);
                Add(find, "@definitionVersion", input.DefinitionVersion);
                await using var rdr = await find.ExecuteReaderAsync(ct);
                if (await rdr.ReadAsync(ct))
                {
                    existingId = rdr.GetInt32(0);
                    existingSha256 = rdr.GetString(1);
                }
            }

            var isIdentical = existingId.HasValue
                && string.Equals(existingSha256, input.DefinitionSha256, StringComparison.OrdinalIgnoreCase);
            if (existingId.HasValue && !replaceExisting && !isIdentical)
            {
                throw new InvalidOperationException(
                    "A module definition with the same module key and version already exists, but the imported JSON is different. Use a new definition version for unattended HostAgent folder imports.");
            }

            var documentId = existingId ?? await InsertModuleDefinitionDocumentAsync(conn, tx, input, ct);
            if (existingId.HasValue && (replaceExisting || !isIdentical))
            {
                await UpdateModuleDefinitionDocumentAsync(conn, tx, documentId, input, ct);
            }

            await ReplaceModuleDefinitionCompatibilityAsync(conn, tx, documentId, input.CompatibleArtifacts, ct);
            await tx.CommitAsync(ct);

            return new ModuleDefinitionSaveResult(
                documentId,
                !existingId.HasValue,
                existingId.HasValue && replaceExisting && !isIdentical,
                existingId.HasValue && isIdentical);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<(int DocumentId, bool Created, bool Replaced, bool WasIdentical)> SaveImportedHostConfigurationAsync(
        PortableHostConfigurationDocument input,
        bool replaceExisting,
        CancellationToken ct)
    {
        const string findSql = @"
SELECT HostConfigurationDocumentId,
       ConfigurationSha256
FROM omp.HostConfigurationDocuments
WHERE HostKey = @hostKey
  AND ConfigurationVersion = @configurationVersion;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            int? existingId = null;
            string? existingSha256 = null;
            await using (var find = new SqlCommand(findSql, conn, tx))
            {
                Add(find, "@hostKey", input.HostKey);
                Add(find, "@configurationVersion", input.ConfigurationVersion);
                await using var rdr = await find.ExecuteReaderAsync(ct);
                if (await rdr.ReadAsync(ct))
                {
                    existingId = rdr.GetInt32(0);
                    existingSha256 = rdr.GetString(1);
                }
            }

            var isIdentical = existingId.HasValue
                && string.Equals(existingSha256, input.ConfigurationSha256, StringComparison.OrdinalIgnoreCase);
            if (existingId.HasValue && !replaceExisting && !isIdentical)
            {
                throw new InvalidOperationException(
                    "A host configuration with the same host key and version already exists, but the imported JSON is different. Use a new configuration version for unattended HostAgent folder imports.");
            }

            var documentId = existingId ?? await InsertHostConfigurationDocumentAsync(conn, tx, input, ct);
            if (existingId.HasValue && (replaceExisting || !isIdentical))
            {
                await UpdateHostConfigurationDocumentAsync(conn, tx, documentId, input, ct);
            }

            await tx.CommitAsync(ct);
            return (documentId, !existingId.HasValue, existingId.HasValue && replaceExisting && !isIdentical, existingId.HasValue && isIdentical);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<(int DocumentId, bool Created, bool Replaced, bool WasIdentical)> SaveImportedConfigOverlayAsync(
        PortableConfigOverlayDocument input,
        bool replaceExisting,
        CancellationToken ct)
    {
        const string findSql = @"
SELECT ConfigOverlayDocumentId,
       OverlaySha256
FROM omp.ConfigOverlayDocuments
WHERE OverlayKey = @overlayKey
  AND HostKey = @hostKey
  AND OverlayVersion = @overlayVersion;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            int? existingId = null;
            string? existingSha256 = null;
            await using (var find = new SqlCommand(findSql, conn, tx))
            {
                Add(find, "@overlayKey", input.OverlayKey);
                Add(find, "@hostKey", input.HostKey);
                Add(find, "@overlayVersion", input.OverlayVersion);
                await using var rdr = await find.ExecuteReaderAsync(ct);
                if (await rdr.ReadAsync(ct))
                {
                    existingId = rdr.GetInt32(0);
                    existingSha256 = rdr.GetString(1);
                }
            }

            var isIdentical = existingId.HasValue
                && string.Equals(existingSha256, input.OverlaySha256, StringComparison.OrdinalIgnoreCase);
            if (existingId.HasValue && !replaceExisting && !isIdentical)
            {
                throw new InvalidOperationException(
                    "A config overlay with the same overlay key, host key, and version already exists, but the imported JSON is different. Use a new overlay version for unattended HostAgent folder imports.");
            }

            var documentId = existingId ?? await InsertConfigOverlayDocumentAsync(conn, tx, input, ct);
            if (existingId.HasValue && (replaceExisting || !isIdentical))
            {
                await UpdateConfigOverlayDocumentAsync(conn, tx, documentId, input, ct);
            }

            await ReplaceConfigOverlayConfigurationFilesAsync(conn, tx, documentId, input.ConfigurationFiles, ct);
            await tx.CommitAsync(ct);
            return (documentId, !existingId.HasValue, existingId.HasValue && replaceExisting && !isIdentical, existingId.HasValue && isIdentical);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<(int CreatedCount, int UpdatedCount, int SkippedCount, int PermissionRowCount)> SaveImportedDashboardWidgetsAsync(
        PortableDashboardWidgetPackage input,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            await EnsureDashboardWidgetTablesAsync(conn, tx, ct);
            var created = 0;
            var updated = 0;
            var skipped = 0;
            var permissionRows = 0;

            foreach (var widget in input.Widgets)
            {
                var permissionIds = await ResolveDashboardWidgetPermissionIdsAsync(conn, tx, widget.PermissionNames, ct);
                var roleIds = await ResolveDashboardWidgetRoleIdsAsync(conn, tx, widget.RoleNames, ct);
                var existing = await FindDashboardWidgetAsync(conn, tx, widget, ct);
                if (existing is not null)
                {
                    var versionComparison = CompareArtifactVersions(widget.WidgetVersion, existing.WidgetVersion);
                    if (versionComparison < 0)
                    {
                        skipped++;
                        continue;
                    }

                    if (versionComparison == 0)
                    {
                        if (DashboardWidgetMatches(existing, widget, permissionIds, roleIds))
                        {
                            skipped++;
                            continue;
                        }

                        throw new InvalidOperationException(
                            $"Dashboard widget '{widget.WidgetKey}' already exists with version {existing.WidgetVersion}, but the imported content is different. Use a new widgetVersion for unattended HostAgent folder imports.");
                    }

                    await UpdateDashboardWidgetAsync(conn, tx, existing.WidgetId, widget, ct);
                    await ReplaceDashboardWidgetPermissionRowsAsync(conn, tx, existing.WidgetId, permissionIds, roleIds, ct);
                    updated++;
                    permissionRows += permissionIds.Count + roleIds.Count;
                }
                else
                {
                    var widgetId = await InsertDashboardWidgetAsync(conn, tx, widget, ct);
                    await ReplaceDashboardWidgetPermissionRowsAsync(conn, tx, widgetId, permissionIds, roleIds, ct);
                    created++;
                    permissionRows += permissionIds.Count + roleIds.Count;
                }
            }

            await tx.CommitAsync(ct);
            return (created, updated, skipped, permissionRows);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<(int DataDocumentCount, int InsertedBinaryDataCount, int ReusedBinaryDataCount)> SaveImportedWidgetRuntimeDataAsync(
        PortableWidgetRuntimeDataPackage input,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            await EnsureWidgetRuntimeDataTablesAsync(conn, tx, ct);

            var insertedBinaryRows = 0;
            var reusedBinaryRows = 0;
            var binaryIdMap = new Dictionary<long, long>();
            var binaryHashMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var binary in input.BinaryData)
            {
                var targetId = await FindExistingWidgetRuntimeBinaryDataIdAsync(conn, tx, binary, ct);
                if (targetId.HasValue)
                {
                    reusedBinaryRows++;
                    await UpdateWidgetRuntimeBinaryDataEnabledStateAsync(conn, tx, targetId.Value, binary.IsEnabled, ct);
                }
                else
                {
                    targetId = await InsertWidgetRuntimeBinaryDataAsync(conn, tx, binary, ct);
                    insertedBinaryRows++;
                }

                if (binary.SourceBinaryDataId > 0)
                {
                    binaryIdMap[binary.SourceBinaryDataId] = targetId.Value;
                }

                binaryHashMap[ToSha256Hex(binary.ContentHash)] = targetId.Value;
            }

            var dataDocuments = 0;
            foreach (var document in input.DataDocuments)
            {
                var widgetId = await GetWidgetRuntimeDataWidgetIdAsync(conn, tx, document.WidgetKey, ct)
                    ?? throw new InvalidOperationException($"Dashboard widget '{document.WidgetKey}' was not found. Import the widget definition before importing widget runtime data.");
                var jsonData = RemapWidgetRuntimeBinaryDataReferences(document.JsonData, binaryIdMap, binaryHashMap);
                await UpsertWidgetRuntimeDataAsync(conn, tx, widgetId, document.DataKey, jsonData, ct);
                dataDocuments++;
            }

            await tx.CommitAsync(ct);
            return (dataDocuments, insertedBinaryRows, reusedBinaryRows);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<string?> GetAppliedModuleDefinitionVersionAsync(
        string moduleKey,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) DefinitionVersion
FROM omp.ModuleDefinitionDocuments
WHERE ModuleKey = @moduleKey
  AND IsApplied = 1
ORDER BY AppliedUtc DESC, UpdatedUtc DESC, ModuleDefinitionDocumentId DESC;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@moduleKey", moduleKey);

        var value = await cmd.ExecuteScalarAsync(ct);
        return value is string version && !string.IsNullOrWhiteSpace(version)
            ? version
            : null;
    }

    public async Task<bool> ApplyImportedModuleDefinitionAsync(
        int moduleDefinitionDocumentId,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @DefinitionJson nvarchar(max);
DECLARE @ModuleKey nvarchar(100);
DECLARE @ModuleDisplayName nvarchar(200);
DECLARE @ModuleType nvarchar(50);
DECLARE @SchemaName nvarchar(128);
DECLARE @Description nvarchar(500);
DECLARE @SortOrder int;
DECLARE @IsEnabled bit;
DECLARE @ModuleId int;

SELECT @DefinitionJson = DefinitionJson,
       @ModuleKey = ModuleKey
FROM omp.ModuleDefinitionDocuments
WHERE ModuleDefinitionDocumentId = @moduleDefinitionDocumentId;

IF @DefinitionJson IS NULL
BEGIN
    THROW 53230, N'Module definition document was not found.', 1;
END;

SELECT @ModuleDisplayName = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.displayName'), N''),
       @ModuleType = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.moduleType'), N''),
       @SchemaName = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.schemaName'), N''),
       @Description = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.description'), N''),
       @SortOrder = TRY_CONVERT(int, JSON_VALUE(@DefinitionJson, N'$.module.sortOrder')),
       @IsEnabled = TRY_CONVERT(bit, JSON_VALUE(@DefinitionJson, N'$.module.isEnabled'));

IF @ModuleDisplayName IS NOT NULL
BEGIN
    MERGE omp.Modules AS target
    USING
    (
        SELECT @ModuleKey AS ModuleKey,
               @ModuleDisplayName AS DisplayName,
               COALESCE(@ModuleType, N'WebAppModule') AS ModuleType,
               COALESCE(@SchemaName, @ModuleKey) AS SchemaName,
               @Description AS Description,
               COALESCE(@SortOrder, 0) AS SortOrder,
               COALESCE(@IsEnabled, CONVERT(bit, 1)) AS IsEnabled
    ) AS source
    ON target.ModuleKey = source.ModuleKey
    WHEN MATCHED THEN
        UPDATE SET DisplayName = source.DisplayName,
                   ModuleType = source.ModuleType,
                   SchemaName = source.SchemaName,
                   Description = source.Description,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(ModuleKey, DisplayName, ModuleType, SchemaName, Description, SortOrder, IsEnabled)
        VALUES(source.ModuleKey, source.DisplayName, source.ModuleType, source.SchemaName, source.Description, source.SortOrder, source.IsEnabled);
END;

SELECT @ModuleId = ModuleId
FROM omp.Modules
WHERE ModuleKey = @ModuleKey;

IF @ModuleId IS NOT NULL
BEGIN
    ;WITH AppRows AS
    (
        SELECT AppKey,
               COALESCE(NULLIF(DisplayName, N''), AppKey) AS DisplayName,
               COALESCE(NULLIF(AppType, N''), N'WebApp') AS AppType,
               COALESCE(AllowMultipleActiveInstances, CONVERT(bit, 0)) AS AllowMultipleActiveInstances,
               NULLIF(Description, N'') AS Description,
               COALESCE(SortOrder, 0) AS SortOrder,
               COALESCE(IsEnabled, CONVERT(bit, 1)) AS IsEnabled
        FROM OPENJSON(@DefinitionJson, N'$.apps')
        WITH
        (
            AppKey nvarchar(100) N'$.appKey',
            DisplayName nvarchar(200) N'$.displayName',
            AppType nvarchar(50) N'$.appType',
            AllowMultipleActiveInstances bit N'$.allowMultipleActiveInstances',
            Description nvarchar(500) N'$.description',
            SortOrder int N'$.sortOrder',
            IsEnabled bit N'$.isEnabled'
        )
        WHERE AppKey IS NOT NULL
    )
    MERGE omp.Apps AS target
    USING AppRows AS source
    ON target.ModuleId = @ModuleId
    AND target.AppKey = source.AppKey
    WHEN MATCHED THEN
        UPDATE SET DisplayName = source.DisplayName,
                   AppType = source.AppType,
                   AllowMultipleActiveInstances = source.AllowMultipleActiveInstances,
                   Description = source.Description,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(ModuleId, AppKey, DisplayName, AppType, AllowMultipleActiveInstances, Description, SortOrder, IsEnabled)
        VALUES(@ModuleId, source.AppKey, source.DisplayName, source.AppType, source.AllowMultipleActiveInstances, source.Description, source.SortOrder, source.IsEnabled);
END;

UPDATE omp.ModuleDefinitionDocuments
SET IsApplied = CASE WHEN ModuleDefinitionDocumentId = @moduleDefinitionDocumentId THEN 1 ELSE 0 END,
    AppliedUtc = CASE WHEN ModuleDefinitionDocumentId = @moduleDefinitionDocumentId THEN SYSUTCDATETIME() ELSE AppliedUtc END,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ModuleKey = @ModuleKey;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            await using var cmd = new SqlCommand(sql, conn, tx);
            Add(cmd, "@moduleDefinitionDocumentId", moduleDefinitionDocumentId);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<int> ExecuteImportedModuleDefinitionSqlRepairsAsync(
        int moduleDefinitionDocumentId,
        CancellationToken ct)
    {
        var definitionJson = await GetModuleDefinitionJsonAsync(moduleDefinitionDocumentId, ct);
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            return 0;
        }

        var scripts = ReadPortableSqlScripts(definitionJson)
            .OrderBy(static script => script.Order)
            .ThenBy(static script => script.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scripts.Count == 0)
        {
            return 0;
        }

        // HostAgent may receive platform-core repairs through universal-package imports.
        // Keep the same embedded SQL, hash, idempotency, and safety checks for those scripts.
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await AcquireModuleDefinitionSqlExecutionLockAsync(conn, ct);

        var validationScripts = scripts.Where(IsValidationScript).ToList();
        if (validationScripts.Count > 0)
        {
            var needsRepair = false;
            foreach (var validationScript in validationScripts)
            {
                var validationSql = ResolvePortableSqlText(validationScript)
                    ?? throw new InvalidOperationException(
                        $"Module definition validation script '{validationScript.Key}' has no SQL content.");
                var validationSha256 = ComputeTextSha256(validationSql);
                if (!string.IsNullOrWhiteSpace(validationScript.Sha256)
                    && !string.Equals(validationScript.Sha256, validationSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Module definition validation script '{validationScript.Key}' content does not match its declared SHA-256.");
                }

                var validationSafety = ValidateReadOnlyModuleDefinitionSql(validationSql);
                if (validationSafety is not null)
                {
                    throw new InvalidOperationException(
                        $"Module definition validation script '{validationScript.Key}' was blocked: {validationSafety}");
                }

                try
                {
                    var validation = await ExecuteModuleDefinitionValidationSqlAsync(conn, validationSql, ct);
                    needsRepair = needsRepair || !validation.IsHealthy;
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    needsRepair = true;
                }
            }

            if (!needsRepair)
            {
                return 0;
            }
        }

        var executed = 0;
        foreach (var script in scripts.Where(static script => !IsValidationScript(script)))
        {
            if (!string.Equals(script.Execution, "idempotent", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Module definition SQL script '{script.Key}' is not idempotent and cannot be executed by HostAgent folder import.");
            }

            var originalSqlText = ResolvePortableSqlText(script);
            if (string.IsNullOrWhiteSpace(originalSqlText))
            {
                continue;
            }

            var scriptSha256 = ComputeTextSha256(originalSqlText);
            if (!string.IsNullOrWhiteSpace(script.Sha256)
                && !string.Equals(script.Sha256, scriptSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Module definition SQL script '{script.Key}' content does not match its declared SHA-256.");
            }

            var sqlText = await PatchBootstrapPortalAdminPrincipalAsync(conn, originalSqlText, ct);
            var safety = ValidateSafeModuleDefinitionSql(sqlText);
            if (safety is not null)
            {
                throw new InvalidOperationException($"Module definition SQL script '{script.Key}' was blocked: {safety}");
            }

            var executionId = await InsertModuleDefinitionSqlExecutionAsync(
                conn,
                moduleDefinitionDocumentId,
                script,
                scriptSha256,
                ct);

            try
            {
                await ExecuteSqlBatchesAsync(conn, sqlText, ct);
                await CompleteModuleDefinitionSqlExecutionAsync(conn, executionId, "Succeeded", null, ct);
                executed++;
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                await CompleteModuleDefinitionSqlExecutionAsync(conn, executionId, "Failed", ex.Message, ct);
                throw new InvalidOperationException($"Module definition SQL script '{script.Key}' failed: {ex.Message}", ex);
            }
        }

        return executed;
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
       ar.TargetName,
       ar.RelativePath,
       ar.Sha256
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
            rdr.IsDBNull(4) ? null : rdr.GetString(4),
            rdr.IsDBNull(5) ? null : rdr.GetString(5),
            rdr.IsDBNull(6) ? null : rdr.GetString(6));
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
       ar.TargetName,
       ar.RelativePath,
       ar.Sha256
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
            rdr.IsDBNull(4) ? null : rdr.GetString(4),
            rdr.IsDBNull(5) ? null : rdr.GetString(5),
            rdr.IsDBNull(6) ? null : rdr.GetString(6));
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

    public async Task UpdateImportedArtifactMetadataAsync(
        int artifactId,
        string relativePath,
        string sha256,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.Artifacts
SET RelativePath = @relativePath,
    Sha256 = @sha256,
    IsEnabled = 1,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ArtifactId = @artifactId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);
        cmd.Parameters.AddWithValue("@sha256", sha256);
        await cmd.ExecuteNonQueryAsync(ct);
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
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var target = await ReadArtifactAutoApplyTargetAsync(conn, artifactId, ct);
        if (target is null || !IsArtifactPackageCompatibleWithAppType(target.PackageType, target.AppType))
        {
            return (0, 0, 0);
        }

        var templateRowsUpdated = await ApplyArtifactToIntRowsAsync(
            conn,
            artifactId,
            target.Version,
            @"
SELECT tai.InstanceTemplateAppInstanceId,
       currentArtifact.Version
FROM omp.InstanceTemplateAppInstances tai
LEFT JOIN omp.Artifacts currentArtifact
    ON currentArtifact.ArtifactId = tai.DesiredArtifactId
WHERE tai.AppId = @appId
  AND tai.IsEnabled = 1
  AND ISNULL(tai.DesiredArtifactId, -1) <> @artifactId;",
            @"
UPDATE omp.InstanceTemplateAppInstances
SET DesiredArtifactId = @artifactId,
    UpdatedUtc = SYSUTCDATETIME()
WHERE InstanceTemplateAppInstanceId = @rowId;",
            target.AppId,
            ct);

        var appRowsUpdated = await ApplyArtifactToGuidRowsAsync(
            conn,
            artifactId,
            target.Version,
            @"
SELECT ai.AppInstanceId,
       currentArtifact.Version
FROM omp.AppInstances ai
LEFT JOIN omp.Artifacts currentArtifact
    ON currentArtifact.ArtifactId = ai.ArtifactId
WHERE ai.AppId = @appId
  AND ai.IsEnabled = 1
  AND ISNULL(ai.ArtifactId, -1) <> @artifactId;",
            @"
UPDATE omp.AppInstances
SET ArtifactId = @artifactId,
    UpdatedUtc = SYSUTCDATETIME()
WHERE AppInstanceId = @rowId;",
            target.AppId,
            ct);

        var workerRowsUpdated = await ApplyArtifactToGuidRowsAsync(
            conn,
            artifactId,
            target.Version,
            @"
SELECT wi.WorkerInstanceId,
       currentArtifact.Version
FROM omp.WorkerInstances wi
INNER JOIN omp.AppInstances ai
    ON ai.AppInstanceId = wi.AppInstanceId
LEFT JOIN omp.Artifacts currentArtifact
    ON currentArtifact.ArtifactId = wi.ArtifactId
WHERE ai.AppId = @appId
  AND wi.IsEnabled = 1
  AND wi.ArtifactId IS NOT NULL
  AND wi.ArtifactId <> @artifactId;",
            @"
UPDATE omp.WorkerInstances
SET ArtifactId = @artifactId,
    UpdatedUtc = SYSUTCDATETIME()
WHERE WorkerInstanceId = @rowId;",
            target.AppId,
            ct);

        return (templateRowsUpdated, appRowsUpdated, workerRowsUpdated);
    }

    public async Task<int> ApplyImportedHostAgentArtifactToCurrentHostAsync(
        int artifactId,
        string hostKey,
        string? serviceNamePrefix,
        string? installRoot,
        CancellationToken ct)
    {
        const string contextSql = @"
SELECT h.HostId,
       targetArtifact.Version,
       desired.ArtifactId,
       currentArtifact.Version
FROM omp.Hosts h
CROSS JOIN omp.Artifacts targetArtifact
LEFT JOIN omp.HostAgentDesiredStates desired
    ON desired.HostId = h.HostId
LEFT JOIN omp.Artifacts currentArtifact
    ON currentArtifact.ArtifactId = desired.ArtifactId
WHERE h.HostKey = @hostKey
  AND h.IsEnabled = 1
  AND targetArtifact.ArtifactId = @artifactId
  AND targetArtifact.PackageType = N'host-agent'
  AND targetArtifact.IsEnabled = 1;";

        const string sql = @"
DECLARE @hostId uniqueidentifier;
DECLARE @changes table(ActionName nvarchar(10) NOT NULL);

SELECT @hostId = HostId
FROM omp.Hosts
WHERE HostKey = @hostKey
  AND IsEnabled = 1;

IF @hostId IS NULL
BEGIN
    SELECT 0;
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM omp.Artifacts
    WHERE ArtifactId = @artifactId
      AND PackageType = N'host-agent'
      AND IsEnabled = 1
)
BEGIN
    SELECT 0;
    RETURN;
END;

MERGE omp.HostAgentDesiredStates AS target
USING
(
    SELECT
        @hostId AS HostId,
        @artifactId AS ArtifactId,
        NULLIF(@serviceNamePrefix, N'') AS ServiceNamePrefix,
        NULLIF(@installRoot, N'') AS InstallRoot
) AS source
ON target.HostId = source.HostId
WHEN MATCHED AND
(
       target.ArtifactId <> source.ArtifactId
    OR (source.ServiceNamePrefix IS NOT NULL AND ISNULL(target.ServiceNamePrefix, N'') <> source.ServiceNamePrefix)
    OR (source.InstallRoot IS NOT NULL AND ISNULL(target.InstallRoot, N'') <> source.InstallRoot)
    OR target.IsEnabled = 0
)
    THEN UPDATE SET
        ArtifactId = source.ArtifactId,
        ServiceNamePrefix = COALESCE(source.ServiceNamePrefix, target.ServiceNamePrefix),
        InstallRoot = COALESCE(source.InstallRoot, target.InstallRoot),
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(HostId, ArtifactId, ServiceNamePrefix, InstallRoot, IsEnabled)
    VALUES(source.HostId, source.ArtifactId, source.ServiceNamePrefix, source.InstallRoot, 1)
OUTPUT $action INTO @changes;

SELECT COUNT(1) FROM @changes;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        string targetVersion;
        int? currentArtifactId;
        string? currentVersion;
        await using (var context = new SqlCommand(contextSql, conn))
        {
            context.Parameters.AddWithValue("@artifactId", artifactId);
            context.Parameters.AddWithValue("@hostKey", hostKey);
            await using var rdr = await context.ExecuteReaderAsync(ct);
            if (!await rdr.ReadAsync(ct))
            {
                return 0;
            }

            targetVersion = rdr.GetString(1);
            currentArtifactId = rdr.IsDBNull(2) ? null : rdr.GetInt32(2);
            currentVersion = rdr.IsDBNull(3) ? null : rdr.GetString(3);
        }

        if (currentArtifactId.HasValue
            && currentArtifactId.Value != artifactId
            && !ShouldAutoApplyImportedArtifact(targetVersion, currentVersion))
        {
            return 0;
        }

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@serviceNamePrefix", string.IsNullOrWhiteSpace(serviceNamePrefix) ? string.Empty : serviceNamePrefix.Trim());
        cmd.Parameters.AddWithValue("@installRoot", string.IsNullOrWhiteSpace(installRoot) ? string.Empty : installRoot.Trim());

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int count ? count : 0;
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
        if (safeMessage?.Length > StoredDiagnosticMessageMaxLength)
        {
            safeMessage = safeMessage[..StoredDiagnosticMessageMaxLength];
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

    public async Task<ArtifactRetentionCleanupExecutionResult> ExecuteArtifactRetentionCleanupAsync(
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

DECLARE @StoreEntries TABLE
(
    ArtifactId int NOT NULL,
    Version nvarchar(50) NOT NULL,
    PackageType nvarchar(50) NOT NULL,
    TargetName nvarchar(100) NULL,
    RelativePath nvarchar(400) NOT NULL
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

INSERT INTO @StoreEntries
(
    ArtifactId,
    Version,
    PackageType,
    TargetName,
    RelativePath
)
SELECT ArtifactId,
       Version,
       PackageType,
       TargetName,
       LTRIM(RTRIM(RelativePath))
FROM @DeleteArtifacts
WHERE RelativePath IS NOT NULL
  AND LTRIM(RTRIM(RelativePath)) <> N'';

DELETE s
FROM omp.HostAppDeploymentStates s
INNER JOIN @DeleteArtifacts d ON d.ArtifactId = s.ArtifactId;

DELETE s
FROM omp.HostAgentRuntimeStates s
INNER JOIN @DeleteArtifacts d ON d.ArtifactId = s.ArtifactId;

DELETE s
FROM omp.HostArtifactStates s
INNER JOIN @DeleteArtifacts d ON d.ArtifactId = s.ArtifactId;

DELETE har
FROM omp.HostArtifactRequirements har
INNER JOIN @DeleteArtifacts d ON d.ArtifactId = har.ArtifactId
WHERE har.IsEnabled = 0;

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
SELECT CAST(NULL AS uniqueidentifier),
       N'ArtifactStoreCleanup',
       (
           SELECT 1 AS schemaVersion,
                  JSON_QUERY
                  (
                      (
                          SELECT entry.ArtifactId AS artifactId,
                                 entry.Version AS version,
                                 entry.PackageType AS packageType,
                                 entry.TargetName AS targetName,
                                 entry.RelativePath AS relativePath
                          FROM @StoreEntries entry
                          ORDER BY entry.PackageType,
                                   entry.TargetName,
                                   entry.Version,
                                   entry.ArtifactId
                          FOR JSON PATH
                      )
                  ) AS artifactStoreEntries
           FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
       ),
       CAST(0 AS tinyint),
       @RequestedBy,
       3
WHERE EXISTS
(
    SELECT 1
    FROM @StoreEntries
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
       TotalVersions
FROM @DeleteArtifacts
ORDER BY ModuleKey,
         AppKey,
         PackageType,
         TargetName,
         RetentionRank DESC,
         CreatedUtc,
         ArtifactId;

SELECT ArtifactId,
       Version,
       PackageType,
       TargetName,
       RelativePath
FROM @StoreEntries
ORDER BY PackageType,
         TargetName,
         Version,
         ArtifactId;

SELECT COUNT(1) AS HostCacheEntryCount
FROM @CacheEntries;

SELECT COUNT(1) AS ArtifactStoreEntryCount
FROM @StoreEntries;

SELECT COUNT(1) AS CreatedHostAgentJobCount
FROM @CreatedJobs;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            await using var cmd = new SqlCommand(sql, conn, tx);
            cmd.CommandTimeout = 3600;
            cmd.Parameters.AddWithValue("@MaxVersionsToKeep", Math.Clamp(maxVersionsToKeep, 1, 100));
            cmd.Parameters.AddWithValue("@RequestedBy", string.IsNullOrWhiteSpace(requestedBy) ? DBNull.Value : Truncate(requestedBy, 256));

            var deletedArtifacts = new List<ArtifactRetentionCleanupDeletedArtifact>();
            var storeEntries = new List<ArtifactStoreCleanupJobEntry>();
            var hostCacheEntryCount = 0;
            var artifactStoreEntryCount = 0;
            var createdHostAgentJobCount = 0;

            await using (var rdr = await cmd.ExecuteReaderAsync(ct))
            {
                while (await rdr.ReadAsync(ct))
                {
                    deletedArtifacts.Add(new ArtifactRetentionCleanupDeletedArtifact
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
                        TotalVersions = rdr.GetInt32(9)
                    });
                }

                if (await rdr.NextResultAsync(ct))
                {
                    while (await rdr.ReadAsync(ct))
                    {
                        storeEntries.Add(new ArtifactStoreCleanupJobEntry
                        {
                            ArtifactId = rdr.GetInt32(0),
                            Version = rdr.GetString(1),
                            PackageType = rdr.GetString(2),
                            TargetName = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                            RelativePath = rdr.GetString(4)
                        });
                    }
                }

                if (await rdr.NextResultAsync(ct) && await rdr.ReadAsync(ct))
                {
                    hostCacheEntryCount = rdr.GetInt32(0);
                }

                if (await rdr.NextResultAsync(ct) && await rdr.ReadAsync(ct))
                {
                    artifactStoreEntryCount = rdr.GetInt32(0);
                }

                if (await rdr.NextResultAsync(ct) && await rdr.ReadAsync(ct))
                {
                    createdHostAgentJobCount = rdr.GetInt32(0);
                }
            }

            await tx.CommitAsync(ct);

            return new ArtifactRetentionCleanupExecutionResult
            {
                DeletedArtifacts = deletedArtifacts,
                ArtifactStoreEntries = storeEntries,
                ArtifactStoreEntryCount = artifactStoreEntryCount,
                HostCacheEntryCount = hostCacheEntryCount,
                CreatedHostAgentJobCount = createdHostAgentJobCount
            };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<HostAgentJobWorkItem?> TryClaimNextHostAgentJobAsync(
        string hostKey,
        string serviceName,
        int leaseSeconds,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();
DECLARE @hostId uniqueidentifier;

IF OBJECT_ID(N'omp.HostAgentJobs', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS bigint) AS HostAgentJobId,
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS nvarchar(100)) AS JobType,
        CAST(NULL AS nvarchar(max)) AS PayloadJson,
        CAST(NULL AS nvarchar(256)) AS RequestedBy,
        CAST(NULL AS int) AS AttemptCount;
    RETURN;
END;

SELECT @hostId = HostId
FROM omp.Hosts
WHERE HostKey = @hostKey
  AND IsEnabled = 1;

IF @hostId IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS bigint) AS HostAgentJobId,
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS nvarchar(100)) AS JobType,
        CAST(NULL AS nvarchar(max)) AS PayloadJson,
        CAST(NULL AS nvarchar(256)) AS RequestedBy,
        CAST(NULL AS int) AS AttemptCount;
    RETURN;
END;

UPDATE omp.HostAgentJobs
SET Status = @failedStatus,
    CompletedUtc = @nowUtc,
    LeaseUntilUtc = NULL,
    LastError = COALESCE(NULLIF(LastError, N''), N'HostAgent job lease expired after the maximum number of attempts.'),
    UpdatedUtc = @nowUtc
WHERE (HostId = @hostId OR HostId IS NULL)
  AND Status = @runningStatus
  AND LeaseUntilUtc IS NOT NULL
  AND LeaseUntilUtc < @nowUtc
  AND AttemptCount >= MaxAttempts;

DECLARE @claimed TABLE
(
    HostAgentJobId bigint NOT NULL,
    HostId uniqueidentifier NULL,
    JobType nvarchar(100) NOT NULL,
    PayloadJson nvarchar(max) NULL,
    RequestedBy nvarchar(256) NULL,
    AttemptCount int NOT NULL
);

;WITH NextJob AS
(
    SELECT TOP (1) HostAgentJobId
    FROM omp.HostAgentJobs WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE (HostId = @hostId OR HostId IS NULL)
      AND AttemptCount < MaxAttempts
      AND
      (
          Status = @pendingStatus
          OR
          (
              Status = @runningStatus
              AND LeaseUntilUtc IS NOT NULL
              AND LeaseUntilUtc < @nowUtc
          )
      )
    ORDER BY CASE WHEN HostId = @hostId THEN 0 ELSE 1 END,
             RequestedUtc,
             HostAgentJobId
)
UPDATE job
SET Status = @runningStatus,
    ClaimedByServiceName = @serviceName,
    ClaimedUtc = @nowUtc,
    LeaseUntilUtc = DATEADD(second, @leaseSeconds, @nowUtc),
    StartedUtc = COALESCE(job.StartedUtc, @nowUtc),
    CompletedUtc = NULL,
    AttemptCount = job.AttemptCount + 1,
    LastError = NULL,
    UpdatedUtc = @nowUtc
OUTPUT inserted.HostAgentJobId,
       inserted.HostId,
       inserted.JobType,
       inserted.PayloadJson,
       inserted.RequestedBy,
       inserted.AttemptCount
INTO @claimed(HostAgentJobId, HostId, JobType, PayloadJson, RequestedBy, AttemptCount)
FROM omp.HostAgentJobs job
INNER JOIN NextJob nextJob ON nextJob.HostAgentJobId = job.HostAgentJobId;

SELECT HostAgentJobId,
       HostId,
       JobType,
       PayloadJson,
       RequestedBy,
       AttemptCount
FROM @claimed;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@serviceName", serviceName);
        cmd.Parameters.AddWithValue("@leaseSeconds", Math.Max(30, leaseSeconds));
        cmd.Parameters.AddWithValue("@pendingStatus", HostAgentJobStatuses.Pending);
        cmd.Parameters.AddWithValue("@runningStatus", HostAgentJobStatuses.Running);
        cmd.Parameters.AddWithValue("@failedStatus", HostAgentJobStatuses.Failed);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new HostAgentJobWorkItem(
            rdr.GetInt64(0),
            rdr.IsDBNull(1) ? null : rdr.GetGuid(1),
            rdr.GetString(2),
            rdr.IsDBNull(3) ? null : rdr.GetString(3),
            rdr.IsDBNull(4) ? null : rdr.GetString(4),
            rdr.GetInt32(5));
    }

    public async Task CompleteHostAgentJobAsync(
        long hostAgentJobId,
        byte status,
        string? resultJson,
        string? lastError,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.HostAgentJobs
SET Status = @status,
    CompletedUtc = SYSUTCDATETIME(),
    LeaseUntilUtc = NULL,
    ResultJson = @resultJson,
    LastError = @lastError,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostAgentJobId = @hostAgentJobId
  AND Status = @runningStatus;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostAgentJobId", hostAgentJobId);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@runningStatus", HostAgentJobStatuses.Running);
        cmd.Parameters.AddWithValue("@resultJson", string.IsNullOrWhiteSpace(resultJson) ? DBNull.Value : resultJson);
        cmd.Parameters.AddWithValue("@lastError", string.IsNullOrWhiteSpace(lastError) ? DBNull.Value : Truncate(lastError, StoredDiagnosticMessageMaxLength));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsHostArtifactCachePathInUseAsync(
        Guid hostId,
        int artifactId,
        string localPath,
        CancellationToken ct)
    {
        const string sql = @"
SELECT CASE
    WHEN EXISTS
    (
        SELECT 1
        FROM omp.HostArtifactStates
        WHERE HostId = @hostId
          AND LocalPath = @localPath
    )
    OR EXISTS
    (
        SELECT 1
        FROM omp.HostAppDeploymentStates
        WHERE HostId = @hostId
          AND
          (
              ArtifactId = @artifactId
              OR SourceLocalPath = @localPath
          )
    )
    OR EXISTS
    (
        SELECT 1
        FROM omp.HostAgentRuntimeStates
        WHERE HostId = @hostId
          AND
          (
              ArtifactId = @artifactId
              OR InstallPath = @localPath
          )
          AND IsActive = 1
    )
    THEN CAST(1 AS bit)
    ELSE CAST(0 AS bit)
END;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        cmd.Parameters.AddWithValue("@localPath", localPath);

        var value = await cmd.ExecuteScalarAsync(ct);
        return value is bool isInUse && isInUse;
    }

    public async Task<bool> IsArtifactStoreRelativePathReferencedAsync(
        string relativePath,
        CancellationToken ct)
    {
        const string sql = @"
SELECT CASE
    WHEN EXISTS
    (
        SELECT 1
        FROM omp.Artifacts
        WHERE RelativePath = @relativePath
           OR REPLACE(RelativePath, N'\', N'/') = @relativePath
    )
    THEN CAST(1 AS bit)
    ELSE CAST(0 AS bit)
END;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@relativePath", relativePath.Trim().Replace('\\', '/').Trim('/'));

        var value = await cmd.ExecuteScalarAsync(ct);
        return value is bool isReferenced && isReferenced;
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
        string hostKey,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.ArtifactConfigurationFiles', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS int) AS ArtifactConfigurationFileId,
        CAST(NULL AS int) AS ArtifactId,
        CAST(NULL AS nvarchar(400)) AS RelativePath,
        CAST(NULL AS nvarchar(max)) AS FileContent,
        CAST(NULL AS int) AS SourcePriority,
        CAST(NULL AS datetime2(3)) AS SourceUpdatedUtc;
    RETURN;
END;

DECLARE @ModuleKey nvarchar(100);
DECLARE @AppKey nvarchar(100);
DECLARE @Version nvarchar(50);
DECLARE @PackageType nvarchar(50);
DECLARE @TargetName nvarchar(100);

SELECT @ModuleKey = m.ModuleKey,
       @AppKey = app.AppKey,
       @Version = ar.Version,
       @PackageType = ar.PackageType,
       @TargetName = ar.TargetName
FROM omp.Artifacts ar
INNER JOIN omp.Apps app ON app.AppId = ar.AppId
INNER JOIN omp.Modules m ON m.ModuleId = app.ModuleId
WHERE ar.ArtifactId = @artifactId;

IF OBJECT_ID(N'omp.ConfigOverlayDocuments', N'U') IS NULL
   OR OBJECT_ID(N'omp.ConfigOverlayConfigurationFiles', N'U') IS NULL
BEGIN
    SELECT ArtifactConfigurationFileId,
           ArtifactId,
           RelativePath,
           FileContent,
           CAST(0 AS int) AS SourcePriority,
           UpdatedUtc AS SourceUpdatedUtc
    FROM omp.ArtifactConfigurationFiles
    WHERE ArtifactId = @artifactId
      AND IsEnabled = 1
    ORDER BY RelativePath, SourcePriority, SourceUpdatedUtc, ArtifactConfigurationFileId;
    RETURN;
END;

SELECT ArtifactConfigurationFileId,
       ArtifactId,
       RelativePath,
       FileContent,
       CAST(0 AS int) AS SourcePriority,
       UpdatedUtc AS SourceUpdatedUtc
FROM omp.ArtifactConfigurationFiles
WHERE ArtifactId = @artifactId
  AND IsEnabled = 1

UNION ALL

SELECT overlayFile.ConfigOverlayConfigurationFileId AS ArtifactConfigurationFileId,
       @artifactId AS ArtifactId,
       overlayFile.RelativePath,
       overlayFile.FileContent,
       CAST(1 AS int) AS SourcePriority,
       overlay.UpdatedUtc AS SourceUpdatedUtc
FROM omp.ConfigOverlayDocuments overlay
INNER JOIN omp.ConfigOverlayConfigurationFiles overlayFile
    ON overlayFile.ConfigOverlayDocumentId = overlay.ConfigOverlayDocumentId
WHERE overlay.IsEnabled = 1
  AND overlayFile.IsEnabled = 1
  AND overlay.HostKey = @hostKey
  AND (overlay.ModuleKey IS NULL OR overlay.ModuleKey = @ModuleKey)
  AND (overlay.AppKey IS NULL OR overlay.AppKey = @AppKey)
  AND (overlay.PackageType IS NULL OR overlay.PackageType = @PackageType)
  AND (overlay.TargetName IS NULL OR overlay.TargetName = @TargetName)
  AND (overlay.ArtifactVersion IS NULL OR overlay.ArtifactVersion = @Version)
ORDER BY RelativePath, SourcePriority, SourceUpdatedUtc, ArtifactConfigurationFileId;";

        var rows = new Dictionary<string, ArtifactConfigurationFileDescriptor>(StringComparer.OrdinalIgnoreCase);
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var descriptor = new ArtifactConfigurationFileDescriptor
            {
                ArtifactConfigurationFileId = rdr.GetInt32(0),
                ArtifactId = rdr.GetInt32(1),
                RelativePath = rdr.GetString(2),
                FileContent = rdr.GetString(3)
            };
            rows[descriptor.RelativePath] = descriptor;
        }

        return rows.Values
            .OrderBy(static item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
      -- host-role app instances are provisioned only on hosts with that role.
      -- Both remain one logical app entry in Portal.
      AND
      (
          ai.HostId = @hostId
          OR (ai.HostId IS NULL AND ai.TargetHostTemplateId IS NULL)
          OR
          (
              ai.HostId IS NULL
              AND ai.TargetHostTemplateId IS NOT NULL
              AND EXISTS
              (
                  SELECT 1
                  FROM omp.HostDeploymentAssignments hda
                  WHERE hda.HostId = @hostId
                    AND hda.HostTemplateId = ai.TargetHostTemplateId
                    AND hda.IsActive = 1
              )
          )
      )
      AND ai.IsEnabled = 1
      AND ai.IsAllowed = 1
      AND ar.IsEnabled = 1
      AND ar.RelativePath IS NOT NULL
      AND LTRIM(RTRIM(ar.RelativePath)) <> N''

    UNION ALL

    SELECT
        CASE
            WHEN wi.HostId IS NOT NULL THEN wi.HostId
            WHEN ai.HostId IS NOT NULL THEN ai.HostId
            ELSE @hostId
        END AS HostId,
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
      AND
      (
          wi.HostId = @hostId
          OR (wi.HostId IS NULL AND ai.HostId = @hostId)
          OR
          (
              wi.HostId IS NULL
              AND ai.HostId IS NULL
              AND ai.TargetHostTemplateId IS NOT NULL
              AND EXISTS
              (
                  SELECT 1
                  FROM omp.HostDeploymentAssignments hda
                  WHERE hda.HostId = @hostId
                    AND hda.HostTemplateId = ai.TargetHostTemplateId
                    AND hda.IsActive = 1
              )
          )
      )
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
WHERE
  (
      ai.HostId = @hostId
      OR (ai.HostId IS NULL AND ai.TargetHostTemplateId IS NULL)
      OR
      (
          ai.HostId IS NULL
          AND ai.TargetHostTemplateId IS NOT NULL
          AND EXISTS
          (
              SELECT 1
              FROM omp.HostDeploymentAssignments hda
              WHERE hda.HostId = @hostId
                AND hda.HostTemplateId = ai.TargetHostTemplateId
                AND hda.IsActive = 1
          )
      )
  )
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
        CAST(NULL AS nvarchar(200)) AS DeployedRuntimeName,
        CAST(NULL AS datetime2(3)) AS IdentityRepairRequestedUtc,
        CAST(NULL AS nvarchar(256)) AS IdentityRepairRequestedBy;
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
    hds.RuntimeName AS DeployedRuntimeName,
    hds.IdentityRepairRequestedUtc,
    hds.IdentityRepairRequestedBy
FROM omp.AppInstances ai
INNER JOIN omp.Artifacts ar ON ar.ArtifactId = ai.ArtifactId
INNER JOIN omp.HostArtifactStates has
    ON has.HostId = @hostId
   AND has.ArtifactId = ar.ArtifactId
LEFT JOIN omp.HostAppDeploymentStates hds
    ON hds.HostId = @hostId
   AND hds.AppInstanceId = ai.AppInstanceId
WHERE
  (
      ai.HostId = @hostId
      OR
      (
          ai.HostId IS NULL
          AND ai.TargetHostTemplateId IS NOT NULL
          AND EXISTS
          (
              SELECT 1
              FROM omp.HostDeploymentAssignments hda
              WHERE hda.HostId = @hostId
                AND hda.HostTemplateId = ai.TargetHostTemplateId
                AND hda.IsActive = 1
          )
      )
  )
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
                DeployedRuntimeName = rdr.IsDBNull(17) ? null : rdr.GetString(17),
                IdentityRepairRequestedUtc = rdr.IsDBNull(18) ? null : rdr.GetDateTime(18),
                IdentityRepairRequestedBy = rdr.IsDBNull(19) ? null : rdr.GetString(19)
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
        CredentialAutomationMode = @credentialAutomationMode,
        DesiredRuntimeIdentity = @desiredRuntimeIdentity,
        ActualRuntimeIdentity = @actualRuntimeIdentity,
        IdentityCheckStatus = @identityCheckStatus,
        IdentityRepairRequestedUtc = CASE WHEN @clearIdentityRepairRequest = 1 THEN NULL ELSE target.IdentityRepairRequestedUtc END,
        IdentityRepairRequestedBy = CASE WHEN @clearIdentityRepairRequest = 1 THEN NULL ELSE target.IdentityRepairRequestedBy END,
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
        CredentialAutomationMode,
        DesiredRuntimeIdentity,
        ActualRuntimeIdentity,
        IdentityCheckStatus,
        IdentityRepairRequestedUtc,
        IdentityRepairRequestedBy,
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
        @credentialAutomationMode,
        @desiredRuntimeIdentity,
        @actualRuntimeIdentity,
        @identityCheckStatus,
        NULL,
        NULL,
        @nowUtc,
        CASE WHEN @applied = 1 THEN @nowUtc ELSE NULL END,
        @lastError,
        @nowUtc,
        @nowUtc
    );";

        var safeMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? null
            : result.ErrorMessage.Trim();
        if (safeMessage?.Length > StoredDiagnosticMessageMaxLength)
        {
            safeMessage = safeMessage[..StoredDiagnosticMessageMaxLength];
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
        cmd.Parameters.AddWithValue("@credentialAutomationMode", string.IsNullOrWhiteSpace(result.CredentialAutomationMode) ? (object)DBNull.Value : result.CredentialAutomationMode);
        cmd.Parameters.AddWithValue("@desiredRuntimeIdentity", string.IsNullOrWhiteSpace(result.DesiredRuntimeIdentity) ? (object)DBNull.Value : result.DesiredRuntimeIdentity);
        cmd.Parameters.AddWithValue("@actualRuntimeIdentity", string.IsNullOrWhiteSpace(result.ActualRuntimeIdentity) ? (object)DBNull.Value : result.ActualRuntimeIdentity);
        cmd.Parameters.AddWithValue("@identityCheckStatus", string.IsNullOrWhiteSpace(result.IdentityCheckStatus) ? (object)DBNull.Value : result.IdentityCheckStatus);
        cmd.Parameters.AddWithValue("@clearIdentityRepairRequest", result.ClearIdentityRepairRequest);
        cmd.Parameters.AddWithValue("@applied", result.Applied);
        cmd.Parameters.AddWithValue("@lastError", string.IsNullOrWhiteSpace(safeMessage) ? DBNull.Value : safeMessage);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> InsertModuleDefinitionDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        ModuleDefinitionImportDocument input,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.ModuleDefinitionDocuments
(
    ModuleKey,
    DefinitionVersion,
    FormatVersion,
    DefinitionJson,
    DefinitionSha256,
    SourceName,
    IsApplied
)
VALUES
(
    @moduleKey,
    @definitionVersion,
    @formatVersion,
    @definitionJson,
    @definitionSha256,
    @sourceName,
    0
);

SELECT CAST(SCOPE_IDENTITY() AS int);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        BindModuleDefinitionDocument(cmd, input);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task UpdateModuleDefinitionDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int documentId,
        ModuleDefinitionImportDocument input,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.ModuleDefinitionDocuments
SET FormatVersion = @formatVersion,
    DefinitionJson = @definitionJson,
    DefinitionSha256 = @definitionSha256,
    SourceName = @sourceName,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ModuleDefinitionDocumentId = @moduleDefinitionDocumentId;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@moduleDefinitionDocumentId", documentId);
        BindModuleDefinitionDocument(cmd, input);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReplaceModuleDefinitionCompatibilityAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int documentId,
        IReadOnlyList<ModuleDefinitionArtifactCompatibilityEntry> entries,
        CancellationToken ct)
    {
        const string deleteSql = @"
DELETE FROM omp.ModuleDefinitionArtifactCompatibility
WHERE ModuleDefinitionDocumentId = @moduleDefinitionDocumentId;";

        await using (var delete = new SqlCommand(deleteSql, conn, tx))
        {
            Add(delete, "@moduleDefinitionDocumentId", documentId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        const string insertSql = @"
INSERT INTO omp.ModuleDefinitionArtifactCompatibility
(
    ModuleDefinitionDocumentId,
    AppKey,
    PackageType,
    TargetName,
    RelativePathTemplate,
    MinArtifactVersion,
    MaxArtifactVersion
)
VALUES
(
    @moduleDefinitionDocumentId,
    @appKey,
    @packageType,
    @targetName,
    @relativePathTemplate,
    @minArtifactVersion,
    @maxArtifactVersion
);";

        foreach (var entry in entries)
        {
            await using var insert = new SqlCommand(insertSql, conn, tx);
            Add(insert, "@moduleDefinitionDocumentId", documentId);
            Add(insert, "@appKey", entry.AppKey);
            Add(insert, "@packageType", entry.PackageType);
            Add(insert, "@targetName", entry.TargetName);
            Add(insert, "@relativePathTemplate", entry.RelativePathTemplate);
            Add(insert, "@minArtifactVersion", entry.MinArtifactVersion);
            Add(insert, "@maxArtifactVersion", entry.MaxArtifactVersion);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static void BindModuleDefinitionDocument(
        SqlCommand cmd,
        ModuleDefinitionImportDocument input)
    {
        Add(cmd, "@moduleKey", input.ModuleKey);
        Add(cmd, "@definitionVersion", input.DefinitionVersion);
        Add(cmd, "@formatVersion", input.FormatVersion);
        Add(cmd, "@definitionJson", input.DefinitionJson);
        Add(cmd, "@definitionSha256", input.DefinitionSha256);
        Add(cmd, "@sourceName", input.SourceName);
    }

    private static async Task<int> InsertHostConfigurationDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        PortableHostConfigurationDocument input,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.HostConfigurationDocuments
(
    HostKey,
    ConfigurationVersion,
    FormatVersion,
    ConfigurationJson,
    ConfigurationSha256,
    DisplayName,
    Description,
    SourceName,
    IsActive
)
VALUES
(
    @hostKey,
    @configurationVersion,
    @formatVersion,
    @configurationJson,
    @configurationSha256,
    @displayName,
    @description,
    @sourceName,
    1
);

SELECT CAST(SCOPE_IDENTITY() AS int);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        BindHostConfigurationDocument(cmd, input);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task UpdateHostConfigurationDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int documentId,
        PortableHostConfigurationDocument input,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.HostConfigurationDocuments
SET FormatVersion = @formatVersion,
    ConfigurationJson = @configurationJson,
    ConfigurationSha256 = @configurationSha256,
    DisplayName = @displayName,
    Description = @description,
    SourceName = @sourceName,
    IsActive = 1,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostConfigurationDocumentId = @hostConfigurationDocumentId;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@hostConfigurationDocumentId", documentId);
        BindHostConfigurationDocument(cmd, input);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void BindHostConfigurationDocument(SqlCommand cmd, PortableHostConfigurationDocument input)
    {
        Add(cmd, "@hostKey", input.HostKey);
        Add(cmd, "@configurationVersion", input.ConfigurationVersion);
        Add(cmd, "@formatVersion", input.FormatVersion);
        Add(cmd, "@configurationJson", input.ConfigurationJson);
        Add(cmd, "@configurationSha256", input.ConfigurationSha256);
        Add(cmd, "@displayName", input.DisplayName);
        Add(cmd, "@description", input.Description);
        Add(cmd, "@sourceName", input.SourceName);
    }

    private static async Task<int> InsertConfigOverlayDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        PortableConfigOverlayDocument input,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.ConfigOverlayDocuments
(
    OverlayKey,
    OverlayVersion,
    HostKey,
    ModuleKey,
    ModuleDefinitionVersion,
    AppKey,
    PackageType,
    TargetName,
    ArtifactVersion,
    FormatVersion,
    OverlayJson,
    OverlaySha256,
    SourceName,
    IsEnabled
)
VALUES
(
    @overlayKey,
    @overlayVersion,
    @hostKey,
    @moduleKey,
    @moduleDefinitionVersion,
    @appKey,
    @packageType,
    @targetName,
    @artifactVersion,
    @formatVersion,
    @overlayJson,
    @overlaySha256,
    @sourceName,
    1
);

SELECT CAST(SCOPE_IDENTITY() AS int);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        BindConfigOverlayDocument(cmd, input);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task UpdateConfigOverlayDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int documentId,
        PortableConfigOverlayDocument input,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.ConfigOverlayDocuments
SET ModuleKey = @moduleKey,
    ModuleDefinitionVersion = @moduleDefinitionVersion,
    AppKey = @appKey,
    PackageType = @packageType,
    TargetName = @targetName,
    ArtifactVersion = @artifactVersion,
    FormatVersion = @formatVersion,
    OverlayJson = @overlayJson,
    OverlaySha256 = @overlaySha256,
    SourceName = @sourceName,
    IsEnabled = 1,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ConfigOverlayDocumentId = @configOverlayDocumentId;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@configOverlayDocumentId", documentId);
        BindConfigOverlayDocument(cmd, input);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReplaceConfigOverlayConfigurationFilesAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int documentId,
        IReadOnlyList<PortableConfigOverlayConfigurationFile> configurationFiles,
        CancellationToken ct)
    {
        await using (var delete = new SqlCommand(
            "DELETE FROM omp.ConfigOverlayConfigurationFiles WHERE ConfigOverlayDocumentId = @configOverlayDocumentId;",
            conn,
            tx))
        {
            Add(delete, "@configOverlayDocumentId", documentId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        const string insertSql = @"
INSERT INTO omp.ConfigOverlayConfigurationFiles
(
    ConfigOverlayDocumentId,
    RelativePath,
    FileContent,
    IsEnabled
)
VALUES
(
    @configOverlayDocumentId,
    @relativePath,
    @fileContent,
    1
);";

        foreach (var configurationFile in configurationFiles)
        {
            await using var insert = new SqlCommand(insertSql, conn, tx);
            Add(insert, "@configOverlayDocumentId", documentId);
            Add(insert, "@relativePath", configurationFile.RelativePath);
            Add(insert, "@fileContent", configurationFile.FileContent);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static void BindConfigOverlayDocument(SqlCommand cmd, PortableConfigOverlayDocument input)
    {
        Add(cmd, "@overlayKey", input.OverlayKey);
        Add(cmd, "@overlayVersion", input.OverlayVersion);
        Add(cmd, "@hostKey", input.HostKey);
        Add(cmd, "@moduleKey", input.ModuleKey);
        Add(cmd, "@moduleDefinitionVersion", input.ModuleDefinitionVersion);
        Add(cmd, "@appKey", input.AppKey);
        Add(cmd, "@packageType", input.PackageType);
        Add(cmd, "@targetName", input.TargetName);
        Add(cmd, "@artifactVersion", input.ArtifactVersion);
        Add(cmd, "@formatVersion", input.FormatVersion);
        Add(cmd, "@overlayJson", input.OverlayJson);
        Add(cmd, "@overlaySha256", input.OverlaySha256);
        Add(cmd, "@sourceName", input.SourceName);
    }

    private async Task<string?> GetModuleDefinitionJsonAsync(int moduleDefinitionDocumentId, CancellationToken ct)
    {
        const string sql = @"
SELECT DefinitionJson
FROM omp.ModuleDefinitionDocuments
WHERE ModuleDefinitionDocumentId = @moduleDefinitionDocumentId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@moduleDefinitionDocumentId", moduleDefinitionDocumentId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null || value == DBNull.Value ? null : Convert.ToString(value);
    }

    private static IReadOnlyList<PortableModuleDefinitionSqlScript> ReadPortableSqlScripts(string definitionJson)
    {
        var root = JsonNode.Parse(definitionJson);
        if (root?["sqlScripts"] is not JsonArray items)
        {
            return [];
        }

        var scripts = new List<PortableModuleDefinitionSqlScript>();
        foreach (var item in items.OfType<JsonObject>())
        {
            var key = GetJsonString(item, "key");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            scripts.Add(new PortableModuleDefinitionSqlScript(
                key,
                GetJsonString(item, "phase", "setup"),
                GetJsonString(item, "scope", "module"),
                GetJsonInt(item, "order", 0),
                GetJsonString(item, "execution", "idempotent"),
                NullIfWhiteSpace(GetJsonString(item, "path")),
                NullIfWhiteSpace(GetJsonString(item, "source")),
                NullIfWhiteSpace(GetJsonString(item, "inlineSql")),
                NullIfWhiteSpace(GetJsonString(item, "contentEncoding")),
                NullIfWhiteSpace(GetJsonString(item, "content")),
                NullIfWhiteSpace(GetJsonString(item, "sha256"))));
        }

        return scripts;
    }

    private static bool IsValidationScript(PortableModuleDefinitionSqlScript script)
        => string.Equals(script.Phase, "validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Phase, "validation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Execution, "validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Execution, "validation", StringComparison.OrdinalIgnoreCase);

    private static string? ResolvePortableSqlText(PortableModuleDefinitionSqlScript script)
    {
        if (!string.IsNullOrWhiteSpace(script.InlineSql))
        {
            return script.InlineSql;
        }

        if (string.IsNullOrWhiteSpace(script.Content))
        {
            return null;
        }

        if (string.Equals(script.ContentEncoding, "base64-utf8", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(script.Content));
        }

        return script.Content;
    }

    private static async Task<string> PatchBootstrapPortalAdminPrincipalAsync(
        SqlConnection conn,
        string sqlText,
        CancellationToken ct)
    {
        if (!sqlText.Contains(BootstrapPortalAdminPrincipalPlaceholder, StringComparison.Ordinal))
        {
            return sqlText;
        }

        var principal = await ResolveExistingBootstrapPortalAdminPrincipalAsync(conn, ct)
            ?? throw new InvalidOperationException(
                "The module definition SQL contains a bootstrap PortalAdmin placeholder, but no existing PortalAdmins principal was found to reuse.");

        var principalLiteral = ToSqlUnicodeLiteral(principal.Principal);
        var principalTypeLiteral = ToSqlUnicodeLiteral(principal.PrincipalType);
        var patched = Regex.Replace(
            sqlText,
            @"DECLARE\s+@BootstrapPortalAdminPrincipal\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';",
            $"DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = {principalLiteral};",
            RegexOptions.IgnoreCase);

        // Folder import can execute the same portable SQL as Portal repair. Preserve
        // the active bootstrap principal type instead of assuming all installs use ADUser.
        patched = Regex.Replace(
            patched,
            @"PrincipalType\s*=\s*N'ADUser'",
            $"PrincipalType = {principalTypeLiteral}",
            RegexOptions.IgnoreCase);
        patched = Regex.Replace(
            patched,
            @"VALUES\s*\(\s*@PortalAdminsRoleId\s*,\s*N'ADUser'\s*,\s*@BootstrapPortalAdminPrincipal\s*\)",
            $"VALUES(@PortalAdminsRoleId, {principalTypeLiteral}, @BootstrapPortalAdminPrincipal)",
            RegexOptions.IgnoreCase);

        return patched;
    }

    private static async Task<BootstrapPortalAdminPrincipal?> ResolveExistingBootstrapPortalAdminPrincipalAsync(
        SqlConnection conn,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       rp.PrincipalType,
       rp.Principal
FROM omp.RolePrincipals rp
INNER JOIN omp.Roles r ON r.RoleId = rp.RoleId
WHERE r.Name = N'PortalAdmins'
  AND NULLIF(LTRIM(RTRIM(rp.PrincipalType)), N'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(rp.Principal)), N'') IS NOT NULL
  AND rp.Principal NOT IN (N'__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__', N'REPLACE_ME\UserOrGroup')
ORDER BY CASE rp.PrincipalType WHEN N'ADUser' THEN 0 WHEN N'ADGroup' THEN 1 ELSE 2 END,
         rp.Principal;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new BootstrapPortalAdminPrincipal(rdr.GetString(0), rdr.GetString(1));
    }

    private static string ToSqlUnicodeLiteral(string value)
        => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task AcquireModuleDefinitionSqlExecutionLockAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
DECLARE @Result int;
EXEC @Result = sys.sp_getapplock
    @Resource = N'omp.module-definition-sql-repair',
    @LockMode = N'Exclusive',
    @LockOwner = N'Session',
    @LockTimeout = 0;
SELECT @Result;";

        await using var cmd = new SqlCommand(sql, conn);
        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (result < 0)
        {
            throw new InvalidOperationException("Another module definition SQL repair is already running.");
        }
    }

    private static async Task<long> InsertModuleDefinitionSqlExecutionAsync(
        SqlConnection conn,
        int moduleDefinitionDocumentId,
        PortableModuleDefinitionSqlScript script,
        string scriptSha256,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.ModuleDefinitionSqlExecutions
(
    ModuleDefinitionDocumentId,
    ScriptKey,
    ScriptPhase,
    ScriptOrder,
    ScriptSha256,
    ExecutionStatus
)
VALUES
(
    @moduleDefinitionDocumentId,
    @scriptKey,
    @scriptPhase,
    @scriptOrder,
    @scriptSha256,
    N'Running'
);

SELECT CAST(SCOPE_IDENTITY() AS bigint);";

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@moduleDefinitionDocumentId", moduleDefinitionDocumentId);
        Add(cmd, "@scriptKey", script.Key);
        Add(cmd, "@scriptPhase", script.Phase);
        Add(cmd, "@scriptOrder", script.Order);
        Add(cmd, "@scriptSha256", scriptSha256);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task CompleteModuleDefinitionSqlExecutionAsync(
        SqlConnection conn,
        long executionId,
        string status,
        string? errorMessage,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.ModuleDefinitionSqlExecutions
SET ExecutionStatus = @executionStatus,
    CompletedUtc = SYSUTCDATETIME(),
    ErrorMessage = @errorMessage
WHERE ModuleDefinitionSqlExecutionId = @moduleDefinitionSqlExecutionId;";

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@moduleDefinitionSqlExecutionId", executionId);
        Add(cmd, "@executionStatus", status);
        Add(cmd, "@errorMessage", Truncate(errorMessage ?? string.Empty, StoredDiagnosticMessageMaxLength));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ModuleDefinitionValidationResult> ExecuteModuleDefinitionValidationSqlAsync(
        SqlConnection conn,
        string sqlText,
        CancellationToken ct)
    {
        ModuleDefinitionValidationResult? result = null;
        foreach (var batch in SplitSqlBatches(sqlText))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var cmd = new SqlCommand(batch, conn)
            {
                CommandTimeout = 3600
            };
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            do
            {
                if (await rdr.ReadAsync(ct))
                {
                    result = ReadModuleDefinitionValidationResult(rdr);
                }
            }
            while (await rdr.NextResultAsync(ct));
        }

        return result ?? new ModuleDefinitionValidationResult(
            false,
            "The validation script did not return a result row.");
    }

    private static ModuleDefinitionValidationResult ReadModuleDefinitionValidationResult(SqlDataReader rdr)
    {
        var healthyOrdinal = TryGetOrdinal(rdr, "IsHealthy") ?? 0;
        if (healthyOrdinal >= rdr.FieldCount)
        {
            throw new InvalidOperationException("The validation result must contain an IsHealthy column or at least one column.");
        }

        var messageOrdinal = TryGetOrdinal(rdr, "Message");
        var isHealthy = ConvertValidationBoolean(rdr.GetValue(healthyOrdinal))
            ?? throw new InvalidOperationException("The validation result IsHealthy value must be true/false or 1/0.");
        var message = messageOrdinal.HasValue && !rdr.IsDBNull(messageOrdinal.Value)
            ? Convert.ToString(rdr.GetValue(messageOrdinal.Value))
            : null;

        return new ModuleDefinitionValidationResult(isHealthy, message);
    }

    private static int? TryGetOrdinal(SqlDataReader rdr, string name)
    {
        for (var index = 0; index < rdr.FieldCount; index++)
        {
            if (string.Equals(rdr.GetName(index), name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }

    private static bool? ConvertValidationBoolean(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        if (value is bool boolean)
        {
            return boolean;
        }

        if (value is byte or short or int or long or decimal)
        {
            return Convert.ToDecimal(value) != 0m;
        }

        var text = Convert.ToString(value)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.ToLowerInvariant() switch
        {
            "1" or "true" or "ok" or "healthy" or "pass" or "passed" => true,
            "0" or "false" or "error" or "unhealthy" or "fail" or "failed" => false,
            _ => null
        };
    }

    private static async Task ExecuteSqlBatchesAsync(SqlConnection conn, string sqlText, CancellationToken ct)
    {
        foreach (var batch in SplitSqlBatches(sqlText))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var cmd = new SqlCommand(batch, conn)
            {
                CommandTimeout = 3600
            };
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static IEnumerable<string> SplitSqlBatches(string sqlText)
    {
        var batch = new StringBuilder();
        using var reader = new StringReader(sqlText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (Regex.IsMatch(line, @"^\s*GO\s*(?:--.*)?$", RegexOptions.IgnoreCase))
            {
                yield return batch.ToString();
                batch.Clear();
                continue;
            }

            batch.AppendLine(line);
        }

        yield return batch.ToString();
    }

    private static string? ValidateSafeModuleDefinitionSql(string sqlText)
    {
        if (Regex.IsMatch(sqlText, @"(?im)^\s*USE\s+(?:\[[^\]]+\]|[A-Za-z0-9_]+)\s*;?\s*$"))
        {
            return "Module definition SQL must not contain USE database directives.";
        }

        // Module definition setup scripts are allowed to perform bounded schema maintenance,
        // such as dropping/recreating indexes or constraints. Keep this guard focused on
        // destructive operations that remove data-bearing roots.
        if (Regex.IsMatch(sqlText, @"(?is)\bDROP\s+(?:DATABASE|SCHEMA|TABLE)\b"))
        {
            return "The script contains DROP DATABASE, DROP SCHEMA, or DROP TABLE.";
        }

        if (Regex.IsMatch(sqlText, @"(?is)\bTRUNCATE\s+TABLE\b"))
        {
            return "The script contains TRUNCATE TABLE.";
        }

        var unsafeDeleteStatement = ExtractDeleteStatements(sqlText)
            .FirstOrDefault(static statement => !Regex.IsMatch(statement, @"(?is)\bWHERE\b"));
        if (unsafeDeleteStatement is not null)
        {
            return "The script contains DELETE without a WHERE clause.";
        }

        return null;
    }

    private static IEnumerable<string> ExtractDeleteStatements(string sqlText)
    {
        foreach (var batch in SplitSqlBatches(sqlText))
        {
            foreach (Match match in Regex.Matches(
                batch,
                @"(?ims)\bDELETE\b(?<statement>.*?)(?=;|^\s*(?:GO|INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC(?:UTE)?|GRANT|REVOKE|DENY|SELECT)\b|\z)"))
            {
                if (IsForeignKeyOnDeleteClause(batch, match.Index))
                {
                    continue;
                }

                var statement = ("DELETE" + match.Groups["statement"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(statement))
                {
                    yield return statement;
                }
            }
        }
    }

    private static bool IsForeignKeyOnDeleteClause(string sqlText, int deleteIndex)
    {
        var beforeDelete = sqlText[..deleteIndex].TrimEnd();
        return Regex.IsMatch(beforeDelete, @"(?is)\bON$");
    }

    private static string? ValidateReadOnlyModuleDefinitionSql(string sqlText)
    {
        var safety = ValidateSafeModuleDefinitionSql(sqlText);
        if (safety is not null)
        {
            return safety;
        }

        return Regex.IsMatch(
            sqlText,
            @"(?is)\b(?:INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC(?:UTE)?|GRANT|REVOKE|DENY)\b")
            ? "Validation SQL must be read-only and return an IsHealthy result."
            : null;
    }

    private static string ComputeTextSha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string GetJsonString(JsonObject obj, string propertyName, string defaultValue = "")
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return defaultValue;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.Trim()
            : defaultValue;
    }

    private static int GetJsonInt(JsonObject obj, string propertyName, int defaultValue)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is not JsonValue value)
        {
            return defaultValue;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text)
            && int.TryParse(text, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<ArtifactAutoApplyTarget?> ReadArtifactAutoApplyTargetAsync(
        SqlConnection conn,
        int artifactId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT artifact.ArtifactId,
       app.AppId,
       artifact.Version,
       artifact.PackageType,
       app.AppType
FROM omp.Artifacts artifact
INNER JOIN omp.Apps app
    ON app.AppId = artifact.AppId
WHERE artifact.ArtifactId = @artifactId
  AND artifact.IsEnabled = 1
  AND app.IsEnabled = 1;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ArtifactAutoApplyTarget(
            rdr.GetInt32(0),
            rdr.GetInt32(1),
            rdr.GetString(2),
            rdr.GetString(3),
            rdr.GetString(4));
    }

    private static async Task<int> ApplyArtifactToIntRowsAsync(
        SqlConnection conn,
        int artifactId,
        string targetVersion,
        string selectSql,
        string updateSql,
        int appId,
        CancellationToken ct)
    {
        var rowIds = new List<int>();
        await using (var select = new SqlCommand(selectSql, conn))
        {
            select.Parameters.AddWithValue("@artifactId", artifactId);
            select.Parameters.AddWithValue("@appId", appId);
            await using var rdr = await select.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var currentVersion = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                if (ShouldAutoApplyImportedArtifact(targetVersion, currentVersion))
                {
                    rowIds.Add(rdr.GetInt32(0));
                }
            }
        }

        var updated = 0;
        foreach (var rowId in rowIds)
        {
            await using var update = new SqlCommand(updateSql, conn);
            update.Parameters.AddWithValue("@artifactId", artifactId);
            update.Parameters.AddWithValue("@rowId", rowId);
            updated += await update.ExecuteNonQueryAsync(ct);
        }

        return updated;
    }

    private static async Task<int> ApplyArtifactToGuidRowsAsync(
        SqlConnection conn,
        int artifactId,
        string targetVersion,
        string selectSql,
        string updateSql,
        int appId,
        CancellationToken ct)
    {
        var rowIds = new List<Guid>();
        await using (var select = new SqlCommand(selectSql, conn))
        {
            select.Parameters.AddWithValue("@artifactId", artifactId);
            select.Parameters.AddWithValue("@appId", appId);
            await using var rdr = await select.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var currentVersion = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                if (ShouldAutoApplyImportedArtifact(targetVersion, currentVersion))
                {
                    rowIds.Add(rdr.GetGuid(0));
                }
            }
        }

        var updated = 0;
        foreach (var rowId in rowIds)
        {
            await using var update = new SqlCommand(updateSql, conn);
            update.Parameters.AddWithValue("@artifactId", artifactId);
            update.Parameters.AddWithValue("@rowId", rowId);
            updated += await update.ExecuteNonQueryAsync(ct);
        }

        return updated;
    }

    private static bool IsArtifactPackageCompatibleWithAppType(string packageType, string appType)
        => (packageType.Equals("web-app", StringComparison.OrdinalIgnoreCase)
                && (appType.Equals("Portal", StringComparison.OrdinalIgnoreCase)
                    || appType.Equals("WebApp", StringComparison.OrdinalIgnoreCase)))
            || (packageType.Equals("service-app", StringComparison.OrdinalIgnoreCase)
                && appType.Equals("ServiceApp", StringComparison.OrdinalIgnoreCase))
            || (packageType.Equals("worker", StringComparison.OrdinalIgnoreCase)
                && appType.Equals("Worker", StringComparison.OrdinalIgnoreCase))
            || (packageType.Equals("host-agent", StringComparison.OrdinalIgnoreCase)
                && appType.Equals("HostAgent", StringComparison.OrdinalIgnoreCase))
            || (packageType.Equals("worker-host", StringComparison.OrdinalIgnoreCase)
                && appType.Equals("WorkerHost", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldAutoApplyImportedArtifact(string targetVersion, string? currentVersion)
        => string.IsNullOrWhiteSpace(currentVersion)
            || CompareArtifactVersions(targetVersion, currentVersion) > 0;

    private static async Task EnsureDashboardWidgetTablesAsync(
        SqlConnection conn,
        SqlTransaction tx,
        CancellationToken ct)
    {
        const string sql = @"
SELECT CASE
    WHEN OBJECT_ID(N'omp_portal.widgets', N'U') IS NOT NULL
     AND OBJECT_ID(N'omp_portal.widget_permissions', N'U') IS NOT NULL
     AND OBJECT_ID(N'omp.Permissions', N'U') IS NOT NULL
     AND OBJECT_ID(N'omp.Roles', N'U') IS NOT NULL
     AND COL_LENGTH(N'omp_portal.widgets', N'widget_version') IS NOT NULL
    THEN CAST(1 AS bit)
    ELSE CAST(0 AS bit)
END;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is not bool isAvailable || !isAvailable)
        {
            throw new InvalidOperationException(
                "Portal dashboard widget tables are not available. Apply the Portal module definition before importing dashboard widgets through HostAgent.");
        }
    }

    private static async Task EnsureWidgetRuntimeDataTablesAsync(
        SqlConnection conn,
        SqlTransaction tx,
        CancellationToken ct)
    {
        const string sql = @"
SELECT CASE
    WHEN OBJECT_ID(N'omp_portal.widgets', N'U') IS NOT NULL
     AND OBJECT_ID(N'omp_portal.widget_data', N'U') IS NOT NULL
     AND OBJECT_ID(N'omp_portal.widget_binary_data', N'U') IS NOT NULL
    THEN CAST(1 AS bit)
    ELSE CAST(0 AS bit)
END;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is not bool isAvailable || !isAvailable)
        {
            throw new InvalidOperationException(
                "Portal widget runtime data tables are not available. Apply the Portal module definition before importing widget runtime data through HostAgent.");
        }
    }

    private static async Task<int?> GetWidgetRuntimeDataWidgetIdAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string widgetKey,
        CancellationToken ct)
    {
        const string sql = @"
SELECT widget_id
FROM omp_portal.widgets
WHERE widget_key = @widget_key;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_key", SqlDbType.NVarChar, 200).Value = widgetKey;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null || result == DBNull.Value
            ? null
            : Convert.ToInt32(result);
    }

    private static async Task<long?> FindExistingWidgetRuntimeBinaryDataIdAsync(
        SqlConnection conn,
        SqlTransaction tx,
        PortableWidgetRuntimeBinaryData binary,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) binary_data_id
FROM omp_portal.widget_binary_data
WHERE owner_ref = @owner_ref
  AND content_hash = @content_hash
  AND content_length = @content_length
ORDER BY binary_data_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        AddWidgetRuntimeBinaryIdentityParameters(cmd, binary);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null || result == DBNull.Value
            ? null
            : Convert.ToInt64(result);
    }

    private static async Task UpdateWidgetRuntimeBinaryDataEnabledStateAsync(
        SqlConnection conn,
        SqlTransaction tx,
        long binaryDataId,
        bool isEnabled,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp_portal.widget_binary_data
SET is_enabled = @is_enabled,
    updated_at = SYSUTCDATETIME()
WHERE binary_data_id = @binary_data_id
  AND is_enabled <> @is_enabled;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@binary_data_id", SqlDbType.BigInt).Value = binaryDataId;
        cmd.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = isEnabled;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> InsertWidgetRuntimeBinaryDataAsync(
        SqlConnection conn,
        SqlTransaction tx,
        PortableWidgetRuntimeBinaryData binary,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp_portal.widget_binary_data
(
    owner_ref,
    file_name,
    content_type,
    content_length,
    content_hash,
    data_value,
    is_enabled,
    created_by_user_id,
    created_at,
    updated_at
)
OUTPUT INSERTED.binary_data_id
VALUES
(
    @owner_ref,
    @file_name,
    @content_type,
    @content_length,
    @content_hash,
    @data_value,
    @is_enabled,
    NULL,
    SYSUTCDATETIME(),
    SYSUTCDATETIME()
);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        AddWidgetRuntimeBinaryIdentityParameters(cmd, binary);
        cmd.Parameters.Add("@data_value", SqlDbType.VarBinary, -1).Value = binary.Data;
        cmd.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = binary.IsEnabled;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task UpsertWidgetRuntimeDataAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int widgetId,
        string dataKey,
        string jsonData,
        CancellationToken ct)
    {
        const string sql = @"
MERGE omp_portal.widget_data AS target
USING (SELECT @widget_id AS widget_id, @data_key AS data_key) AS source
ON target.widget_id = source.widget_id
AND target.data_key = source.data_key
WHEN MATCHED THEN
    UPDATE SET json_data = @json_data,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(widget_id, data_key, json_data, updated_at)
    VALUES(@widget_id, @data_key, @json_data, SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
        cmd.Parameters.Add("@data_key", SqlDbType.NVarChar, 128).Value = dataKey;
        cmd.Parameters.Add("@json_data", SqlDbType.NVarChar, -1).Value = jsonData;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddWidgetRuntimeBinaryIdentityParameters(
        SqlCommand cmd,
        PortableWidgetRuntimeBinaryData binary)
    {
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = binary.OwnerRef;
        cmd.Parameters.Add("@content_hash", SqlDbType.VarBinary, 32).Value = binary.ContentHash;
        cmd.Parameters.Add("@content_length", SqlDbType.BigInt).Value = binary.ContentLength;
    }

    private static string RemapWidgetRuntimeBinaryDataReferences(
        JsonNode source,
        IReadOnlyDictionary<long, long> binaryIdMap,
        IReadOnlyDictionary<string, long> binaryHashMap)
    {
        var clone = JsonNode.Parse(source.ToJsonString(JsonOptions))
            ?? throw new InvalidOperationException("Widget runtime data jsonData must be valid JSON.");
        RemapWidgetRuntimeBinaryDataReferencesInPlace(clone, binaryIdMap, binaryHashMap);
        return clone.ToJsonString(JsonOptions);
    }

    private static void RemapWidgetRuntimeBinaryDataReferencesInPlace(
        JsonNode node,
        IReadOnlyDictionary<long, long> binaryIdMap,
        IReadOnlyDictionary<string, long> binaryHashMap)
    {
        if (node is JsonObject obj)
        {
            var binaryDataIdPropertyName = FindJsonPropertyName(obj, "binaryDataId");
            var hashTargetId = TryResolveWidgetRuntimeBinaryHash(obj, binaryHashMap, out var targetFromHash)
                ? targetFromHash
                : (long?)null;
            if (binaryDataIdPropertyName is not null
                && obj[binaryDataIdPropertyName] is JsonValue idValue
                && idValue.TryGetValue<long>(out var sourceId))
            {
                if (sourceId > 0 && binaryIdMap.TryGetValue(sourceId, out var targetId))
                {
                    obj[binaryDataIdPropertyName] = targetId;
                }
                else if (hashTargetId.HasValue)
                {
                    obj[binaryDataIdPropertyName] = hashTargetId.Value;
                }
                else
                {
                    throw new InvalidOperationException($"Widget runtime data references missing binaryDataId {sourceId}.");
                }
            }
            else if (hashTargetId.HasValue)
            {
                obj["binaryDataId"] = hashTargetId.Value;
            }

            foreach (var value in obj.Select(static property => property.Value).Where(static value => value is not null).ToArray())
            {
                RemapWidgetRuntimeBinaryDataReferencesInPlace(value!, binaryIdMap, binaryHashMap);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(static child => child is not null))
            {
                RemapWidgetRuntimeBinaryDataReferencesInPlace(child!, binaryIdMap, binaryHashMap);
            }
        }
    }

    private static bool TryResolveWidgetRuntimeBinaryHash(
        JsonObject obj,
        IReadOnlyDictionary<string, long> binaryHashMap,
        out long targetId)
    {
        targetId = 0;
        var propertyName = FindJsonPropertyName(obj, "binaryDataHash");
        if (propertyName is null
            || obj[propertyName] is not JsonValue hashValue
            || !hashValue.TryGetValue<string>(out var hashText)
            || !TryNormalizeSha256Hex(hashText, out var normalizedHash))
        {
            return false;
        }

        return binaryHashMap.TryGetValue(normalizedHash, out targetId);
    }

    private static string? FindJsonPropertyName(JsonObject obj, string propertyName)
        => obj.Select(static property => property.Key)
            .FirstOrDefault(key => key.Equals(propertyName, StringComparison.OrdinalIgnoreCase));

    private static bool TryNormalizeSha256Hex(string? value, out string normalized)
    {
        normalized = string.Empty;
        var text = value?.Trim();
        if (text?.Length != 64)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromHexString(text);
            if (bytes.Length != 32)
            {
                return false;
            }

            normalized = Convert.ToHexString(bytes).ToLowerInvariant();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToSha256Hex(byte[] hash)
        => Convert.ToHexString(hash).ToLowerInvariant();

    private static async Task<DashboardWidgetSnapshot?> FindDashboardWidgetAsync(
        SqlConnection conn,
        SqlTransaction tx,
        PortableDashboardWidgetDefinition widget,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) widget_id
FROM
(
    SELECT widget_id,
           0 AS match_priority
    FROM omp_portal.widgets
    WHERE widget_key = @widget_key
    UNION ALL
    SELECT widget_id,
           1 AS match_priority
    FROM omp_portal.widgets
    WHERE ((@module_key IS NULL AND module_key IS NULL) OR module_key = @module_key)
      AND widget_key IS NULL
      AND title = @title
      AND widget_type = @widget_type
) AS matches
ORDER BY match_priority,
         widget_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        AddDashboardWidgetIdentityParameters(cmd, widget);
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null || value == DBNull.Value)
        {
            return null;
        }

        return await ReadDashboardWidgetSnapshotAsync(conn, tx, ToInt32Invariant(value), ct);
    }

    private static async Task<DashboardWidgetSnapshot> ReadDashboardWidgetSnapshotAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int widgetId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT w.widget_id,
       w.widget_key,
       w.title,
       w.description,
       w.widget_type,
       w.payload,
       w.module_key,
       w.author,
       w.widget_version,
       p.PermissionId AS permission_id,
       r.RoleId AS role_id
FROM omp_portal.widgets w
LEFT JOIN omp_portal.widget_permissions wp ON wp.widget_id = w.widget_id
LEFT JOIN omp.Permissions p ON p.PermissionId = wp.permission_id
LEFT JOIN omp.Roles r ON r.RoleId = wp.role_id
WHERE w.widget_id = @widget_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        DashboardWidgetSnapshot? snapshot = null;
        var permissionIds = new List<int>();
        var roleIds = new List<int>();
        while (await rdr.ReadAsync(ct))
        {
            snapshot ??= new DashboardWidgetSnapshot(
                rdr.GetInt32(0),
                rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1),
                rdr.GetString(2),
                rdr.IsDBNull(3) ? null : rdr.GetString(3),
                rdr.GetString(4),
                rdr.IsDBNull(5) ? null : rdr.GetString(5),
                rdr.IsDBNull(6) ? null : rdr.GetString(6),
                rdr.IsDBNull(7) ? null : rdr.GetString(7),
                rdr.IsDBNull(8) ? "0.0.0" : rdr.GetString(8),
                permissionIds,
                roleIds);

            if (!rdr.IsDBNull(9))
            {
                AddDistinct(permissionIds, rdr.GetInt32(9));
            }

            if (!rdr.IsDBNull(10))
            {
                AddDistinct(roleIds, rdr.GetInt32(10));
            }
        }

        return snapshot
            ?? throw new InvalidOperationException($"Dashboard widget {widgetId} was not found.");
    }

    private static async Task<int> InsertDashboardWidgetAsync(
        SqlConnection conn,
        SqlTransaction tx,
        PortableDashboardWidgetDefinition widget,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp_portal.widgets(widget_key, title, description, widget_type, payload, module_key, author, widget_version, modified_at)
OUTPUT INSERTED.widget_id
VALUES(@widget_key, @title, @description, @widget_type, @payload, @module_key, @author, @widget_version, SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, conn, tx);
        AddDashboardWidgetParameters(cmd, widget);
        var value = await cmd.ExecuteScalarAsync(ct);
        return ToInt32Invariant(value);
    }

    private static async Task UpdateDashboardWidgetAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int widgetId,
        PortableDashboardWidgetDefinition widget,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp_portal.widgets
SET widget_key = @widget_key,
    title = @title,
    description = @description,
    widget_type = @widget_type,
    payload = @payload,
    module_key = @module_key,
    author = @author,
    widget_version = @widget_version,
    modified_at = SYSUTCDATETIME()
WHERE widget_id = @widget_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
        AddDashboardWidgetParameters(cmd, widget);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReplaceDashboardWidgetPermissionRowsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int widgetId,
        IReadOnlyList<int> permissionIds,
        IReadOnlyList<int> roleIds,
        CancellationToken ct)
    {
        await using (var deleteCmd = new SqlCommand(
            "DELETE FROM omp_portal.widget_permissions WHERE widget_id = @widget_id;",
            conn,
            tx))
        {
            deleteCmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
            await deleteCmd.ExecuteNonQueryAsync(ct);
        }

        const string insertSql = @"
INSERT INTO omp_portal.widget_permissions(widget_id, permission_id, role_id)
VALUES(@widget_id, @permission_id, @role_id);";

        foreach (var permissionId in permissionIds)
        {
            await using var cmd = new SqlCommand(insertSql, conn, tx);
            cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
            cmd.Parameters.Add("@permission_id", SqlDbType.Int).Value = permissionId;
            cmd.Parameters.Add("@role_id", SqlDbType.Int).Value = DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var roleId in roleIds)
        {
            await using var cmd = new SqlCommand(insertSql, conn, tx);
            cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
            cmd.Parameters.Add("@permission_id", SqlDbType.Int).Value = DBNull.Value;
            cmd.Parameters.Add("@role_id", SqlDbType.Int).Value = roleId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<IReadOnlyList<int>> ResolveDashboardWidgetPermissionIdsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        IReadOnlyList<string> names,
        CancellationToken ct)
        => await ResolveDashboardWidgetIdsAsync(
            conn,
            tx,
            "omp.Permissions",
            "PermissionId",
            names,
            "permission",
            ct);

    private static async Task<IReadOnlyList<int>> ResolveDashboardWidgetRoleIdsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        IReadOnlyList<string> names,
        CancellationToken ct)
        => await ResolveDashboardWidgetIdsAsync(
            conn,
            tx,
            "omp.Roles",
            "RoleId",
            names,
            "role",
            ct);

    private static async Task<IReadOnlyList<int>> ResolveDashboardWidgetIdsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string tableName,
        string idColumn,
        IReadOnlyList<string> names,
        string label,
        CancellationToken ct)
    {
        ValidateSqlIdentifierPath(tableName, nameof(tableName));
        ValidateSqlIdentifier(idColumn, nameof(idColumn));

        var ids = new List<int>(names.Count);
        foreach (var name in names)
        {
            var sql = $"SELECT {idColumn} FROM {tableName} WHERE Name = @name;";
            await using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = name;
            var value = await cmd.ExecuteScalarAsync(ct);
            if (value is null || value == DBNull.Value)
            {
                throw new InvalidOperationException($"The dashboard widget {label} '{name}' does not exist.");
            }

            ids.Add(ToInt32Invariant(value));
        }

        return ids;
    }

    private static void ValidateSqlIdentifierPath(string value, string parameterName)
    {
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && parts.All(IsSqlIdentifier))
        {
            return;
        }

        throw new ArgumentException($"Invalid SQL identifier path '{value}'.", parameterName);
    }

    private static void ValidateSqlIdentifier(string value, string parameterName)
    {
        if (IsSqlIdentifier(value))
        {
            return;
        }

        throw new ArgumentException($"Invalid SQL identifier '{value}'.", parameterName);
    }

    private static bool IsSqlIdentifier(string value)
        => Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");

    private static int ToInt32Invariant(object value)
        => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);

    private static void AddDashboardWidgetIdentityParameters(
        SqlCommand cmd,
        PortableDashboardWidgetDefinition widget)
    {
        cmd.Parameters.Add("@module_key", SqlDbType.NVarChar, 100).Value =
            widget.ModuleKey is null ? DBNull.Value : widget.ModuleKey;
        cmd.Parameters.Add("@widget_key", SqlDbType.NVarChar, 200).Value = widget.WidgetKey;
        cmd.Parameters.Add("@title", SqlDbType.NVarChar, 200).Value = widget.Title;
        cmd.Parameters.Add("@widget_type", SqlDbType.NVarChar, 50).Value = widget.WidgetType;
    }

    private static void AddDashboardWidgetParameters(
        SqlCommand cmd,
        PortableDashboardWidgetDefinition widget)
    {
        AddDashboardWidgetIdentityParameters(cmd, widget);
        cmd.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value =
            widget.Description is null ? DBNull.Value : widget.Description;
        cmd.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value =
            widget.Payload is null ? DBNull.Value : widget.Payload;
        cmd.Parameters.Add("@author", SqlDbType.NVarChar, 200).Value =
            widget.Author is null ? DBNull.Value : widget.Author;
        cmd.Parameters.Add("@widget_version", SqlDbType.NVarChar, 50).Value =
            string.IsNullOrWhiteSpace(widget.WidgetVersion) ? "0.0.0" : widget.WidgetVersion;
    }

    private static void AddDistinct(List<int> values, int value)
    {
        if (!values.Contains(value))
        {
            values.Add(value);
        }
    }

    private static bool DashboardWidgetMatches(
        DashboardWidgetSnapshot existing,
        PortableDashboardWidgetDefinition imported,
        IReadOnlyList<int> permissionIds,
        IReadOnlyList<int> roleIds)
        => string.Equals(existing.WidgetKey, imported.WidgetKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Title, imported.Title, StringComparison.Ordinal)
            && string.Equals(existing.Description, imported.Description, StringComparison.Ordinal)
            && string.Equals(existing.WidgetType, imported.WidgetType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Payload, imported.Payload, StringComparison.Ordinal)
            && string.Equals(existing.ModuleKey, imported.ModuleKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Author, imported.Author, StringComparison.Ordinal)
            && IdSetsMatch(existing.PermissionIds, permissionIds)
            && IdSetsMatch(existing.RoleIds, roleIds);

    private static bool IdSetsMatch(IReadOnlyList<int> left, IReadOnlyList<int> right)
        => left.Count == right.Count
            && left.Order().SequenceEqual(right.Order());

    private static void Add(SqlCommand cmd, string name, object? value)
        => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void Add(SqlCommand cmd, string name, SqlDbType sqlDbType, object? value)
        => cmd.Parameters.Add(name, sqlDbType).Value = value ?? DBNull.Value;

    private static void Add(SqlCommand cmd, string name, SqlDbType sqlDbType, int size, object? value)
        => cmd.Parameters.Add(name, sqlDbType, size).Value = value ?? DBNull.Value;

    private static string SanitizeStatusMessageFragment(string? value)
    {
        var rawValue = value?.Trim() ?? string.Empty;
        var builder = new StringBuilder(rawValue.Length);
        var previousWasWhitespace = false;
        // Normalize characters with Select; the remaining loop is stateful because it coalesces consecutive whitespace.
        foreach (var normalized in rawValue.Select(static character => char.IsControl(character) ? ' ' : character))
        {
            if (char.IsWhiteSpace(normalized))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            builder.Append(normalized);
            previousWasWhitespace = false;
        }

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? "unknown HostAgent service"
            : Truncate(sanitized, HostAgentRuntimeServiceNameMaxLength);
    }

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
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

    private sealed record PortableModuleDefinitionSqlScript(
        string Key,
        string Phase,
        string Scope,
        int Order,
        string Execution,
        string? Path,
        string? Source,
        string? InlineSql,
        string? ContentEncoding,
        string? Content,
        string? Sha256);

    private sealed record ModuleDefinitionValidationResult(
        bool IsHealthy,
        string? Message);

    private sealed record BootstrapPortalAdminPrincipal(
        string PrincipalType,
        string Principal);

    private sealed record ArtifactAutoApplyTarget(
        int ArtifactId,
        int AppId,
        string Version,
        string PackageType,
        string AppType);

    private sealed record DashboardWidgetSnapshot(
        int WidgetId,
        string WidgetKey,
        string Title,
        string? Description,
        string WidgetType,
        string? Payload,
        string? ModuleKey,
        string? Author,
        string WidgetVersion,
        IReadOnlyList<int> PermissionIds,
        IReadOnlyList<int> RoleIds);
}
