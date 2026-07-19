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
        const string sql = @"
IF OBJECT_ID(N'omp.ConfigOverlayDocuments', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS int) AS ConfigOverlayDocumentId,
        CAST(NULL AS nvarchar(200)) AS OverlayKey,
        CAST(NULL AS nvarchar(50)) AS OverlayVersion,
        CAST(NULL AS nvarchar(128)) AS HostKey,
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
                ModuleKey = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                ModuleDefinitionVersion = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                AppKey = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                PackageType = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                TargetName = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                ArtifactVersion = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                OverlaySha256 = rdr.GetString(10),
                SourceName = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                ConfigurationFileCount = rdr.GetInt32(12),
                IsEnabled = rdr.GetBoolean(13),
                UpdatedUtc = rdr.GetDateTime(14)
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
                await UpdateConfigOverlayDocumentAsync(conn, tx, documentId, input, ct);
                await DisableOtherEnabledConfigOverlayDocumentsAsync(conn, tx, input, documentId, ct);
                await ReplaceConfigOverlayConfigurationFilesAsync(conn, tx, documentId, input.ConfigurationFiles, ct);
            }
            else
            {
                var enableImported = await ShouldEnableImportedConfigOverlayAsync(conn, tx, input, ct);
                documentId = await InsertConfigOverlayDocumentAsync(conn, tx, input, enableImported, ct);
                if (enableImported)
                {
                    await DisableOtherEnabledConfigOverlayDocumentsAsync(conn, tx, input, documentId, ct);
                }

                await ReplaceConfigOverlayConfigurationFilesAsync(conn, tx, documentId, input.ConfigurationFiles, ct);
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
        Add(cmd, "@IsEnabled", isEnabled);
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
SET ModuleKey = @ModuleKey,
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
    @ConfigOverlayDocumentId,
    @RelativePath,
    @FileContent,
    1
);";

        foreach (var configurationFile in configurationFiles)
        {
            await using var insert = new SqlCommand(insertSql, conn, tx);
            Add(insert, "@ConfigOverlayDocumentId", documentId);
            Add(insert, "@RelativePath", configurationFile.RelativePath);
            Add(insert, "@FileContent", configurationFile.FileContent);
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
