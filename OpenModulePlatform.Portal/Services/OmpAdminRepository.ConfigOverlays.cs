// File: OpenModulePlatform.Portal/Services/OmpAdminRepository.ConfigOverlays.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Services;

public sealed partial class OmpAdminRepository
{
    public async Task<IReadOnlyList<HostConfigurationDocumentRow>> GetHostConfigurationDocumentsAsync(CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.HostConfigurationDocuments', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS int) AS HostConfigurationDocumentId,
        CAST(NULL AS nvarchar(128)) AS HostKey,
        CAST(NULL AS nvarchar(50)) AS ConfigurationVersion,
        CAST(NULL AS nvarchar(200)) AS DisplayName,
        CAST(NULL AS nvarchar(500)) AS Description,
        CAST(NULL AS nvarchar(128)) AS ConfigurationSha256,
        CAST(NULL AS nvarchar(400)) AS SourceName,
        CAST(NULL AS bit) AS IsActive,
        CAST(NULL AS datetime2(3)) AS UpdatedUtc;
    RETURN;
END;

SELECT HostConfigurationDocumentId,
       HostKey,
       ConfigurationVersion,
       DisplayName,
       Description,
       ConfigurationSha256,
       SourceName,
       IsActive,
       UpdatedUtc
FROM omp.HostConfigurationDocuments
ORDER BY HostKey, ConfigurationVersion DESC, HostConfigurationDocumentId DESC;";

        var rows = new List<HostConfigurationDocumentRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostConfigurationDocumentRow
            {
                HostConfigurationDocumentId = rdr.GetInt32(0),
                HostKey = rdr.GetString(1),
                ConfigurationVersion = rdr.GetString(2),
                DisplayName = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                Description = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                ConfigurationSha256 = rdr.GetString(5),
                SourceName = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                IsActive = rdr.GetBoolean(7),
                UpdatedUtc = rdr.GetDateTime(8)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<ConfigOverlayDocumentRow>> GetConfigOverlayDocumentsAsync(CancellationToken ct)
    {
        await using var conn0 = _db.Create();
        await conn0.OpenAsync(ct);
        var hasHostScopeColumn = await ColumnExistsAsync(conn0, "omp.ConfigOverlayDocuments", "HostScope", ct);
        var hostScopeExpr = hasHostScopeColumn ? "overlay.HostScope" : "CAST(N'Host' AS nvarchar(20))";
        var sql = $@"
IF OBJECT_ID(N'omp.ConfigOverlayDocuments', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS int) AS ConfigOverlayDocumentId,
        CAST(NULL AS nvarchar(200)) AS OverlayKey,
        CAST(NULL AS nvarchar(50)) AS OverlayVersion,
        CAST(NULL AS nvarchar(128)) AS HostKey,
        CAST(NULL AS nvarchar(20)) AS HostScope,
        CAST(NULL AS nvarchar(100)) AS ModuleKey,
        CAST(NULL AS nvarchar(50)) AS ModuleDefinitionVersion,
        CAST(NULL AS nvarchar(100)) AS AppKey,
        CAST(NULL AS nvarchar(50)) AS PackageType,
        CAST(NULL AS nvarchar(100)) AS TargetName,
        CAST(NULL AS nvarchar(50)) AS ArtifactVersion,
        CAST(NULL AS nvarchar(128)) AS OverlaySha256,
        CAST(NULL AS nvarchar(400)) AS SourceName,
        CAST(NULL AS int) AS ConfigurationFileCount,
        CAST(NULL AS bit) AS IsEnabled,
        CAST(NULL AS datetime2(3)) AS UpdatedUtc;
    RETURN;
END;

SELECT overlay.ConfigOverlayDocumentId,
       overlay.OverlayKey,
       overlay.OverlayVersion,
       overlay.HostKey,
       {hostScopeExpr} AS HostScope,
       overlay.ModuleKey,
       overlay.ModuleDefinitionVersion,
       overlay.AppKey,
       overlay.PackageType,
       overlay.TargetName,
       overlay.ArtifactVersion,
       overlay.OverlaySha256,
       overlay.SourceName,
       COUNT(fileRow.ConfigOverlayConfigurationFileId) AS ConfigurationFileCount,
       overlay.IsEnabled,
       overlay.UpdatedUtc
FROM omp.ConfigOverlayDocuments overlay
LEFT JOIN omp.ConfigOverlayConfigurationFiles fileRow
    ON fileRow.ConfigOverlayDocumentId = overlay.ConfigOverlayDocumentId
GROUP BY overlay.ConfigOverlayDocumentId,
         overlay.OverlayKey,
         overlay.OverlayVersion,
         overlay.HostKey,
         {hostScopeExpr},
         overlay.ModuleKey,
         overlay.ModuleDefinitionVersion,
         overlay.AppKey,
         overlay.PackageType,
         overlay.TargetName,
         overlay.ArtifactVersion,
         overlay.OverlaySha256,
         overlay.SourceName,
         overlay.IsEnabled,
         overlay.UpdatedUtc
ORDER BY overlay.HostKey, overlay.ModuleKey, overlay.AppKey, overlay.OverlayKey, overlay.OverlayVersion DESC;";

        var rows = new List<ConfigOverlayDocumentRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ConfigOverlayDocumentRow
            {
                ConfigOverlayDocumentId = rdr.GetInt32(0),
                OverlayKey = rdr.GetString(1),
                OverlayVersion = rdr.GetString(2),
                HostKey = rdr.GetString(3),
                HostScope = rdr.IsDBNull(4) ? "Host" : rdr.GetString(4),
                ModuleKey = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                ModuleDefinitionVersion = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                AppKey = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                PackageType = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                TargetName = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                ArtifactVersion = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                OverlaySha256 = rdr.GetString(11),
                SourceName = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                ConfigurationFileCount = rdr.GetInt32(13),
                IsEnabled = rdr.GetBoolean(14),
                UpdatedUtc = rdr.GetDateTime(15)
            });
        }

        return rows;
    }

    public async Task<(string HostKey, string ConfigurationVersion, string Json)?> GetHostConfigurationJsonAsync(
        int documentId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT HostKey,
       ConfigurationVersion,
       ConfigurationJson
FROM omp.HostConfigurationDocuments
WHERE HostConfigurationDocumentId = @HostConfigurationDocumentId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@HostConfigurationDocumentId", documentId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        return await rdr.ReadAsync(ct)
            ? (rdr.GetString(0), rdr.GetString(1), rdr.GetString(2))
            : null;
    }

    public async Task<(string OverlayKey, string HostKey, string OverlayVersion, string Json)?> GetConfigOverlayJsonAsync(
        int documentId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT OverlayKey,
       HostKey,
       OverlayVersion,
       OverlayJson
FROM omp.ConfigOverlayDocuments
WHERE ConfigOverlayDocumentId = @ConfigOverlayDocumentId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ConfigOverlayDocumentId", documentId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        return await rdr.ReadAsync(ct)
            ? (rdr.GetString(0), rdr.GetString(1), rdr.GetString(2), rdr.GetString(3))
            : null;
    }

    public async Task<IReadOnlyList<PortableConfigOverlayConfigurationFile>> GetConfigOverlayConfigurationFilesAsync(
        int documentId,
        CancellationToken ct)
    {
        var hasMergeModeColumn = false;
        await using (var probeConn = _db.Create())
        {
            await probeConn.OpenAsync(ct);
            hasMergeModeColumn = await ColumnExistsAsync(probeConn, "omp.ConfigOverlayConfigurationFiles", "MergeMode", ct);
        }

        var mergeExpr = hasMergeModeColumn ? "MergeMode" : "CAST(NULL AS nvarchar(20))";
        var sql = $@"
SELECT RelativePath, FileContent, {mergeExpr}
FROM omp.ConfigOverlayConfigurationFiles
WHERE ConfigOverlayDocumentId = @ConfigOverlayDocumentId AND IsEnabled = 1
ORDER BY RelativePath;";
        var rows = new List<PortableConfigOverlayConfigurationFile>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ConfigOverlayDocumentId", documentId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new PortableConfigOverlayConfigurationFile(
                rdr.GetString(0),
                rdr.GetString(1),
                rdr.IsDBNull(2) ? null : rdr.GetString(2)));
        }

        return rows;
    }

    /// <summary>
    /// Export JSON for a stored overlay. Legacy rows may carry a metadata-only
    /// OverlayJson while the normalized file rows hold the actual content;
    /// export reconstructs configurationFiles from those rows instead of
    /// dropping the settings, without inventing files that are not stored.
    /// </summary>
    public async Task<(string OverlayKey, string HostKey, string HostScope, string OverlayVersion, string Json)?> GetConfigOverlayExportJsonAsync(
        int documentId,
        CancellationToken ct)
    {
        var row = await GetConfigOverlayJsonAsync(documentId, ct);
        if (row is null)
        {
            return null;
        }

        var allRows = await GetConfigOverlayDocumentsAsync(ct);
        var summary = allRows.FirstOrDefault(item => item.ConfigOverlayDocumentId == documentId);
        var hostScope = summary?.HostScope ?? "Host";
        var files = await GetConfigOverlayConfigurationFilesAsync(documentId, ct);
        var json = ConfigOverlayExportJson.Build(row.Value.Json, hostScope, row.Value.HostKey, files);
        return (row.Value.OverlayKey, row.Value.HostKey, hostScope, row.Value.OverlayVersion, json);
    }

    /// <summary>Overlays applicable to a concrete host: its own rows plus AllHosts rows.</summary>
    public async Task<IReadOnlyList<ConfigOverlayListItemDto>> GetConfigOverlaysApplicableToHostAsync(
        string hostKey,
        CancellationToken ct)
    {
        var rows = await GetConfigOverlayDocumentsAsync(ct);
        return rows
            .Where(row => string.Equals(row.HostScope, "AllHosts", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.HostKey, hostKey, StringComparison.OrdinalIgnoreCase))
            .Select(static row => new ConfigOverlayListItemDto(
                row.ConfigOverlayDocumentId,
                row.OverlayKey,
                row.OverlayVersion,
                row.HostKey,
                row.HostScope,
                string.Equals(row.HostScope, "AllHosts", StringComparison.OrdinalIgnoreCase),
                row.ModuleKey,
                row.AppKey,
                row.PackageType,
                row.TargetName,
                row.ArtifactVersion,
                row.ConfigurationFileCount,
                row.IsEnabled,
                row.SourceName,
                row.UpdatedUtc))
            .ToList();
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
WHERE HostKey = @HostKey
  AND ConfigurationVersion = @ConfigurationVersion;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            int? existingId = null;
            string? existingSha256 = null;
            await using (var find = new SqlCommand(findSql, conn, tx))
            {
                Add(find, "@HostKey", input.HostKey);
                Add(find, "@ConfigurationVersion", input.ConfigurationVersion);
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
                    "A host configuration with the same host key and version already exists, but the imported JSON is different.");
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
WHERE OverlayKey = @OverlayKey
  AND HostKey = @HostKey
  AND OverlayVersion = @OverlayVersion;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            var hasHostScopeColumn = await ColumnExistsAsync(conn, tx, "omp.ConfigOverlayDocuments", "HostScope", ct);
            ConfigOverlayHostScopeNames.RequireWritableSchema(input.HostScope, hasHostScopeColumn);
            int? existingId = null;
            string? existingSha256 = null;
            await using (var find = new SqlCommand(findSql, conn, tx))
            {
                Add(find, "@OverlayKey", input.OverlayKey);
                Add(find, "@HostKey", input.HostKey);
                Add(find, "@OverlayVersion", input.OverlayVersion);
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
                    "A config overlay with the same overlay key, host key, and version already exists, but the imported JSON is different.");
            }

            int documentId;
            if (isIdentical)
            {
                // Fully idempotent re-import: no writes at all and never toggle IsEnabled
                // (must not re-enable a manually disabled row nor disable siblings).
                documentId = existingId!.Value;
            }
            else if (existingId.HasValue)
            {
                documentId = existingId.Value;
                // Disable the enabled siblings BEFORE this row is re-enabled: SQL Server
                // checks the filtered unique index per statement, so setting IsEnabled = 1
                // while a sibling is still enabled fails with 2601 mid-transaction.
                await DisableOtherEnabledConfigOverlayDocumentsAsync(conn, tx, input, documentId, ct);
                await UpdateConfigOverlayDocumentAsync(conn, tx, documentId, input, hasHostScopeColumn, ct);
                await ReplaceConfigOverlayConfigurationFilesAsync(conn, tx, documentId, input.ConfigurationFiles, ct);
            }
            else
            {
                var enableImported = await ShouldEnableImportedConfigOverlayAsync(conn, tx, input, ct);
                if (enableImported)
                {
                    // The new row does not exist yet, so -1 excludes nothing and disables
                    // ALL currently enabled siblings before the enabled insert below.
                    await DisableOtherEnabledConfigOverlayDocumentsAsync(conn, tx, input, -1, ct);
                }

                documentId = await InsertConfigOverlayDocumentAsync(conn, tx, input, enableImported, hasHostScopeColumn, ct);
                await ReplaceConfigOverlayConfigurationFilesAsync(conn, tx, documentId, input.ConfigurationFiles, ct);
            }

            await tx.CommitAsync(ct);
            return (documentId, !existingId.HasValue, existingId.HasValue && replaceExisting && !isIdentical, existingId.HasValue && isIdentical);
        }
        catch (SqlException ex) when (IsEnabledSiblingUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            throw new InvalidOperationException(
                $"Another config overlay version for overlay key '{input.OverlayKey}' on host '{input.HostKey}' is still enabled. Disable it before enabling this version.",
                ex);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// True when the failure is the filtered unique index on enabled overlay rows
    /// (2601/2627 naming UX_omp_ConfigOverlayDocuments_Enabled_Key_Host), as opposed
    /// to the per-version unique constraint, which means something else entirely.
    /// </summary>
    private static bool IsEnabledSiblingUniqueViolation(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            if (error.Number is 2601 or 2627
                && error.Message.Contains("UX_omp_ConfigOverlayDocuments_Enabled_Key_Host", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
    @HostKey,
    @ConfigurationVersion,
    @FormatVersion,
    @ConfigurationJson,
    @ConfigurationSha256,
    @DisplayName,
    @Description,
    @SourceName,
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
SET FormatVersion = @FormatVersion,
    ConfigurationJson = @ConfigurationJson,
    ConfigurationSha256 = @ConfigurationSha256,
    DisplayName = @DisplayName,
    Description = @Description,
    SourceName = @SourceName,
    IsActive = 1,
    UpdatedUtc = SYSUTCDATETIME()
WHERE HostConfigurationDocumentId = @HostConfigurationDocumentId;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@HostConfigurationDocumentId", documentId);
        BindHostConfigurationDocument(cmd, input);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void BindHostConfigurationDocument(SqlCommand cmd, PortableHostConfigurationDocument input)
    {
        Add(cmd, "@HostKey", input.HostKey);
        Add(cmd, "@ConfigurationVersion", input.ConfigurationVersion);
        Add(cmd, "@FormatVersion", input.FormatVersion);
        Add(cmd, "@ConfigurationJson", input.ConfigurationJson);
        Add(cmd, "@ConfigurationSha256", input.ConfigurationSha256);
        Add(cmd, "@DisplayName", input.DisplayName);
        Add(cmd, "@Description", input.Description);
        Add(cmd, "@SourceName", input.SourceName);
    }

    /// <summary>
    /// Decides whether a newly imported overlay version should become the single enabled
    /// document for its (OverlayKey, HostKey): true when no row is currently enabled or the
    /// incoming version is strictly newer than every currently enabled row's version.
    /// </summary>
    private static async Task<bool> ShouldEnableImportedConfigOverlayAsync(
        SqlConnection conn,
        SqlTransaction tx,
        PortableConfigOverlayDocument input,
        CancellationToken ct)
    {
        const string sql = @"
SELECT OverlayVersion
FROM omp.ConfigOverlayDocuments
WHERE OverlayKey = @OverlayKey
  AND HostKey = @HostKey
  AND IsEnabled = 1;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@OverlayKey", input.OverlayKey);
        Add(cmd, "@HostKey", input.HostKey);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            if (ArtifactVersionComparer.Compare(input.OverlayVersion, rdr.GetString(0)) <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task DisableOtherEnabledConfigOverlayDocumentsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        PortableConfigOverlayDocument input,
        int documentId,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.ConfigOverlayDocuments
SET IsEnabled = 0,
    UpdatedUtc = SYSUTCDATETIME()
WHERE OverlayKey = @OverlayKey
  AND HostKey = @HostKey
  AND IsEnabled = 1
  AND ConfigOverlayDocumentId <> @ConfigOverlayDocumentId;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@OverlayKey", input.OverlayKey);
        Add(cmd, "@HostKey", input.HostKey);
        Add(cmd, "@ConfigOverlayDocumentId", documentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> InsertConfigOverlayDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        PortableConfigOverlayDocument input,
        bool isEnabled,
        bool hasHostScopeColumn,
        CancellationToken ct)
    {
        var sql = hasHostScopeColumn ? @"
INSERT INTO omp.ConfigOverlayDocuments
(
    OverlayKey,
    OverlayVersion,
    HostKey,
    HostScope,
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
    @OverlayKey,
    @OverlayVersion,
    @HostKey,
    @HostScope,
    @ModuleKey,
    @ModuleDefinitionVersion,
    @AppKey,
    @PackageType,
    @TargetName,
    @ArtifactVersion,
    @FormatVersion,
    @OverlayJson,
    @OverlaySha256,
    @SourceName,
    @IsEnabled
);

SELECT CAST(SCOPE_IDENTITY() AS int);" : @"
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
    @OverlayKey,
    @OverlayVersion,
    @HostKey,
    @ModuleKey,
    @ModuleDefinitionVersion,
    @AppKey,
    @PackageType,
    @TargetName,
    @ArtifactVersion,
    @FormatVersion,
    @OverlayJson,
    @OverlaySha256,
    @SourceName,
    @IsEnabled
);

SELECT CAST(SCOPE_IDENTITY() AS int);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        BindConfigOverlayDocument(cmd, input);
        if (hasHostScopeColumn)
        {
            Add(cmd, "@HostScope", ConfigOverlayHostScopeNames.ToName(input.HostScope));
        }

        Add(cmd, "@IsEnabled", isEnabled);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task UpdateConfigOverlayDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int documentId,
        PortableConfigOverlayDocument input,
        bool hasHostScopeColumn,
        CancellationToken ct)
    {
        var sql = (hasHostScopeColumn ? @"
UPDATE omp.ConfigOverlayDocuments
SET HostScope = @HostScope,
    ModuleKey = @ModuleKey," : @"
UPDATE omp.ConfigOverlayDocuments
SET ModuleKey = @ModuleKey,") + @"
    ModuleDefinitionVersion = @ModuleDefinitionVersion,
    AppKey = @AppKey,
    PackageType = @PackageType,
    TargetName = @TargetName,
    ArtifactVersion = @ArtifactVersion,
    FormatVersion = @FormatVersion,
    OverlayJson = @OverlayJson,
    OverlaySha256 = @OverlaySha256,
    SourceName = @SourceName,
    IsEnabled = 1,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ConfigOverlayDocumentId = @ConfigOverlayDocumentId;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@ConfigOverlayDocumentId", documentId);
        BindConfigOverlayDocument(cmd, input);
        if (hasHostScopeColumn)
        {
            Add(cmd, "@HostScope", ConfigOverlayHostScopeNames.ToName(input.HostScope));
        }

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
            "DELETE FROM omp.ConfigOverlayConfigurationFiles WHERE ConfigOverlayDocumentId = @ConfigOverlayDocumentId;",
            conn,
            tx))
        {
            Add(delete, "@ConfigOverlayDocumentId", documentId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        // The MergeMode column arrives with the 1-setup migration for ADR 0006;
        // a database not yet migrated stores no mode and keeps the implicit
        // default (merge for .json paths, replace otherwise).
        var hasMergeModeColumn = await ColumnExistsAsync(conn, tx, "omp.ConfigOverlayConfigurationFiles", "MergeMode", ct);

        const string insertSql = @"
INSERT INTO omp.ConfigOverlayConfigurationFiles
(
    ConfigOverlayDocumentId,
    RelativePath,
    FileContent,
    MergeMode,
    IsEnabled
)
VALUES
(
    @ConfigOverlayDocumentId,
    @RelativePath,
    @FileContent,
    @MergeMode,
    1
);";

        const string insertSqlWithoutMergeMode = @"
INSERT INTO omp.ConfigOverlayConfigurationFiles
(
    ConfigOverlayDocumentId,
    RelativePath,
    FileContent,
    IsEnabled
)
VALUES
(
    @ConfigOverlayDocumentId,
    @RelativePath,
    @FileContent,
    1
);";

        foreach (var configurationFile in configurationFiles)
        {
            await using var insert = new SqlCommand(
                hasMergeModeColumn ? insertSql : insertSqlWithoutMergeMode,
                conn,
                tx);
            Add(insert, "@ConfigOverlayDocumentId", documentId);
            Add(insert, "@RelativePath", configurationFile.RelativePath);
            Add(insert, "@FileContent", configurationFile.FileContent);
            if (hasMergeModeColumn)
            {
                Add(insert, "@MergeMode", configurationFile.MergeMode);
            }

            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static void BindConfigOverlayDocument(SqlCommand cmd, PortableConfigOverlayDocument input)
    {
        Add(cmd, "@OverlayKey", input.OverlayKey);
        Add(cmd, "@OverlayVersion", input.OverlayVersion);
        Add(cmd, "@HostKey", input.HostKey);
        Add(cmd, "@ModuleKey", input.ModuleKey);
        Add(cmd, "@ModuleDefinitionVersion", input.ModuleDefinitionVersion);
        Add(cmd, "@AppKey", input.AppKey);
        Add(cmd, "@PackageType", input.PackageType);
        Add(cmd, "@TargetName", input.TargetName);
        Add(cmd, "@ArtifactVersion", input.ArtifactVersion);
        Add(cmd, "@FormatVersion", input.FormatVersion);
        Add(cmd, "@OverlayJson", input.OverlayJson);
        Add(cmd, "@OverlaySha256", input.OverlaySha256);
        Add(cmd, "@SourceName", input.SourceName);
    }
}
