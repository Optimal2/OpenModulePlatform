using System.Data;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Services;

public sealed record OverlayEditorSave(int? DocumentId, string? ExpectedHash);

public sealed partial class OmpAdminRepository : IConfigOverlayAdminStore
{
    async Task<OverlayAdminSnapshot> IConfigOverlayAdminStore.ReadAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        const string sql = """
            SELECT CASE WHEN OBJECT_ID(N'omp.ConfigOverlayDocuments', N'U') IS NOT NULL
                AND COL_LENGTH(N'omp.ConfigOverlayDocuments', N'HostScope') IS NOT NULL
                AND COL_LENGTH(N'omp.ConfigOverlayConfigurationFiles', N'MergeMode') IS NOT NULL
                AND OBJECT_ID(N'omp.ArtifactConfigurationFiles', N'U') IS NOT NULL THEN 1 ELSE 0 END;
            """;
        await using var cmd = new SqlCommand(sql, conn);
        var ready = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
        var documentsExist = await TableExistsAsync(conn, "omp.ConfigOverlayDocuments", ct)
            && await TableExistsAsync(conn, "omp.ConfigOverlayConfigurationFiles", ct);
        var hasScope = await ColumnExistsAsync(conn, "omp.ConfigOverlayDocuments", "HostScope", ct);
        return new(ready, await GetHostsAsync(ct), await GetArtifactsAsync(ct),
            documentsExist ? await GetConfigOverlayDocumentsAsync(ct) : [], hasScope);
    }

    async Task<OverlayAdminDetail?> IConfigOverlayAdminStore.DetailAsync(int id, CancellationToken ct)
    {
        var summary = (await GetConfigOverlayDocumentsAsync(ct)).SingleOrDefault(o => o.ConfigOverlayDocumentId == id);
        if (summary is null) return null;
        var document = await GetConfigOverlayJsonAsync(id, ct);
        if (document is null) return null;
        var files = await ReadOverlaySourceRowsAsync(id, true, ct);
        // Normalized rows are authoritative, including disabled rows. Preserve every other selector/field.
        var root = JsonNode.Parse(document.Value.Json) as JsonObject ?? new JsonObject();
        root["configurationFiles"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject
        {
            ["relativePath"] = f.RelativePath, ["fileContent"] = f.Content, ["mergeMode"] = f.MergeMode
        }).ToArray());
        root["hostScope"] = summary.HostScope;
        root["hostKey"] = summary.HostKey;
        return new(summary, root.ToJsonString(new() { WriteIndented = true }), files);
    }

    Task<IReadOnlyList<OverlaySourceRow>> IConfigOverlayAdminStore.SourcesAsync(int artifactId, CancellationToken ct)
        => ReadOverlaySourceRowsAsync(artifactId, false, ct);

    private async Task<IReadOnlyList<OverlaySourceRow>> ReadOverlaySourceRowsAsync(int id, bool overlay, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        var table = overlay ? "omp.ConfigOverlayConfigurationFiles" : "omp.ArtifactConfigurationFiles";
        if (!await TableExistsAsync(conn, table, ct)) return [];
        var merge = overlay && await ColumnExistsAsync(conn, table, "MergeMode", ct) ? "MergeMode" : "CAST(NULL AS nvarchar(20))";
        var key = overlay ? "ConfigOverlayDocumentId" : "ArtifactId";
        var rowKey = overlay ? "ConfigOverlayConfigurationFileId" : "ArtifactConfigurationFileId";
        // Only the fixed identifiers above are interpolated; the requested identity is a parameter.
        var sql = $"SELECT {rowKey}, RelativePath, FileContent, {merge}, IsEnabled, UpdatedUtc FROM {table} WHERE {key} = @id;";
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var files = new List<OverlaySourceRow>();
        while (await reader.ReadAsync(ct)) files.Add(new(reader.GetInt32(0), overlay ? id : null,
            reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetBoolean(4), reader.GetDateTime(5)));
        return files;
    }

    async Task<int> IConfigOverlayAdminStore.SaveAsync(PortableConfigOverlayDocument document, int? id,
        string? expectedHash, CancellationToken ct)
        => (await SaveImportedConfigOverlayAsync(document, id.HasValue, ct, new(id, expectedHash))).DocumentId;

    internal const string OverlayStateSql = """
        SELECT OverlayKey, HostKey, OverlaySha256, UpdatedUtc
        FROM omp.ConfigOverlayDocuments WITH (UPDLOCK, HOLDLOCK)
        WHERE ConfigOverlayDocumentId = @id;
        """;

    internal const string OverlayEnableSiblingsSql = """
        UPDATE omp.ConfigOverlayDocuments SET IsEnabled = 0, UpdatedUtc = SYSUTCDATETIME()
        WHERE OverlayKey = @key AND HostKey = @host AND ConfigOverlayDocumentId <> @id AND IsEnabled = 1;
        """;
    internal const string OverlayUpdateStateSql = "UPDATE omp.ConfigOverlayDocuments SET IsEnabled = @enabled, UpdatedUtc = SYSUTCDATETIME() WHERE ConfigOverlayDocumentId = @id;";
    internal const string OverlayDeleteSql = "DELETE FROM omp.ConfigOverlayDocuments WHERE ConfigOverlayDocumentId = @id;";

    async Task IConfigOverlayAdminStore.ChangeStateAsync(int id, string expectedHash, bool? enabled, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            string key, host, hash;
            DateTime updated;
            await using (var read = new SqlCommand(OverlayStateSql, conn, tx))
            {
                Add(read, "@id", id);
                await using var reader = await read.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Overlay no longer exists.");
                key = reader.GetString(0); host = reader.GetString(1); hash = reader.GetString(2); updated = reader.GetDateTime(3);
            }
            if (expectedHash != OverlayEditToken(hash, updated)) throw new InvalidOperationException("Overlay changed. Reload before saving.");
            if (enabled == true)
            {
                await using var siblings = new SqlCommand(OverlayEnableSiblingsSql, conn, tx);
                Add(siblings, "@key", key); Add(siblings, "@host", host); Add(siblings, "@id", id);
                await siblings.ExecuteNonQueryAsync(ct);
            }
            var sql = enabled.HasValue ? OverlayUpdateStateSql : OverlayDeleteSql;
            await using var write = new SqlCommand(sql, conn, tx);
            Add(write, "@id", id);
            if (enabled.HasValue) Add(write, "@enabled", enabled.Value);
            await write.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    internal const string PreviewHostSql = "SELECT HostKey, IsEnabled FROM omp.Hosts WITH (UPDLOCK, HOLDLOCK) WHERE HostKey = @hostKey;";
    internal const string PreviewArtifactSql = """
        SELECT ar.ArtifactId, m.ModuleKey, a.AppKey, ar.Version, ar.PackageType, ar.TargetName, ar.IsEnabled
        FROM omp.Artifacts ar WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN omp.Apps a ON a.AppId = ar.AppId
        INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
        WHERE ar.ArtifactId = @id;
        """;
    internal const string PreviewArtifactFilesSql = """
        SELECT ArtifactConfigurationFileId, RelativePath, FileContent, CAST(NULL AS nvarchar(20)), IsEnabled, UpdatedUtc
        FROM omp.ArtifactConfigurationFiles WITH (UPDLOCK, HOLDLOCK) WHERE ArtifactId = @id;
        """;
    internal const string PreviewOverlayFilesSql = """
        SELECT ConfigOverlayConfigurationFileId, RelativePath, FileContent, MergeMode, IsEnabled, UpdatedUtc
        FROM omp.ConfigOverlayConfigurationFiles WITH (UPDLOCK, HOLDLOCK) WHERE ConfigOverlayDocumentId = @id;
        """;
    internal const string PreviewOverlayFilesNoMergeSql = """
        SELECT ConfigOverlayConfigurationFileId, RelativePath, FileContent, CAST(NULL AS nvarchar(20)), IsEnabled, UpdatedUtc
        FROM omp.ConfigOverlayConfigurationFiles WITH (UPDLOCK, HOLDLOCK) WHERE ConfigOverlayDocumentId = @id;
        """;

    // Preview-bound mutation. Everything the preview fingerprint covers is
    // re-read with UPDLOCK/HOLDLOCK in this Serializable transaction, the
    // fingerprint is re-derived and compared, and only then is the row
    // changed — so a sibling, fallback or artifact change after the preview
    // rejects instead of mutating. No schema or store beyond the existing
    // overlay/artifact tables is involved.
    async Task IConfigOverlayAdminStore.ChangeStateFromPreviewAsync(int id, string expectedHash, bool? enabled,
        string hostKey, int artifactId, string previewToken, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            string key, host, hash;
            DateTime updated;
            await using (var read = new SqlCommand(OverlayStateSql, conn, tx))
            {
                Add(read, "@id", id);
                await using var reader = await read.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Overlay no longer exists.");
                key = reader.GetString(0); host = reader.GetString(1); hash = reader.GetString(2); updated = reader.GetDateTime(3);
            }
            if (expectedHash != OverlayEditToken(hash, updated)) throw new InvalidOperationException("Overlay changed. Reload before saving.");

            var hasHostScope = await ColumnExistsAsync(conn, tx, "omp.ConfigOverlayDocuments", "HostScope", ct);
            var hasMerge = await ColumnExistsAsync(conn, tx, "omp.ConfigOverlayConfigurationFiles", "MergeMode", ct);
            var schemaReady = await InTxExistsAsync(conn, tx, "omp.ConfigOverlayDocuments", ct) && hasHostScope
                && hasMerge && await InTxExistsAsync(conn, tx, "omp.ArtifactConfigurationFiles", ct);

            HostRow hostRow;
            await using (var cmd = new SqlCommand(PreviewHostSql, conn, tx))
            {
                Add(cmd, "@hostKey", hostKey);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                var hosts = new List<HostRow>();
                while (await reader.ReadAsync(ct)) hosts.Add(new() { HostKey = reader.GetString(0), IsEnabled = reader.GetBoolean(1) });
                hostRow = hosts.SingleOrDefault() ?? throw new InvalidOperationException("Select a registered host in this installation.");
            }
            ArtifactRow artifactRow;
            await using (var cmd = new SqlCommand(PreviewArtifactSql, conn, tx))
            {
                Add(cmd, "@id", artifactId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Select an artifact in this installation.");
                artifactRow = new()
                {
                    ArtifactId = reader.GetInt32(0), ModuleKey = reader.GetString(1), AppKey = reader.GetString(2),
                    Version = reader.GetString(3), PackageType = reader.GetString(4),
                    TargetName = reader.IsDBNull(5) ? null : reader.GetString(5), IsEnabled = reader.GetBoolean(6),
                };
            }

            var documents = new List<ConfigOverlayDocumentRow>();
            var hostScopeExpr = hasHostScope ? "overlay.HostScope" : "CAST(N'Host' AS nvarchar(20))";
            var documentsSql = $@"
SELECT overlay.ConfigOverlayDocumentId, overlay.OverlayKey, overlay.OverlayVersion, overlay.HostKey,
       {hostScopeExpr}, overlay.ModuleKey, overlay.ModuleDefinitionVersion, overlay.AppKey, overlay.PackageType,
       overlay.TargetName, overlay.ArtifactVersion, overlay.OverlaySha256, overlay.SourceName,
       COUNT(fileRow.ConfigOverlayConfigurationFileId), overlay.IsEnabled, overlay.UpdatedUtc
FROM omp.ConfigOverlayDocuments overlay WITH (UPDLOCK, HOLDLOCK)
LEFT JOIN omp.ConfigOverlayConfigurationFiles fileRow WITH (UPDLOCK, HOLDLOCK)
    ON fileRow.ConfigOverlayDocumentId = overlay.ConfigOverlayDocumentId
GROUP BY overlay.ConfigOverlayDocumentId, overlay.OverlayKey, overlay.OverlayVersion, overlay.HostKey,
       {hostScopeExpr}, overlay.ModuleKey, overlay.ModuleDefinitionVersion, overlay.AppKey, overlay.PackageType,
       overlay.TargetName, overlay.ArtifactVersion, overlay.OverlaySha256, overlay.SourceName,
       overlay.IsEnabled, overlay.UpdatedUtc;";
            await using (var cmd = new SqlCommand(documentsSql, conn, tx))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    documents.Add(new()
                    {
                        ConfigOverlayDocumentId = reader.GetInt32(0), OverlayKey = reader.GetString(1),
                        OverlayVersion = reader.GetString(2), HostKey = reader.GetString(3),
                        HostScope = reader.IsDBNull(4) ? "Host" : reader.GetString(4),
                        ModuleKey = reader.IsDBNull(5) ? null : reader.GetString(5),
                        ModuleDefinitionVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
                        AppKey = reader.IsDBNull(7) ? null : reader.GetString(7),
                        PackageType = reader.IsDBNull(8) ? null : reader.GetString(8),
                        TargetName = reader.IsDBNull(9) ? null : reader.GetString(9),
                        ArtifactVersion = reader.IsDBNull(10) ? null : reader.GetString(10),
                        OverlaySha256 = reader.GetString(11), SourceName = reader.IsDBNull(12) ? null : reader.GetString(12),
                        ConfigurationFileCount = reader.GetInt32(13), IsEnabled = reader.GetBoolean(14), UpdatedUtc = reader.GetDateTime(15),
                    });
            }
            var selected = documents.SingleOrDefault(o => o.ConfigOverlayDocumentId == id)
                ?? throw new InvalidOperationException("Overlay no longer exists.");
            var snapshot = new OverlayAdminSnapshot(schemaReady, [hostRow], [artifactRow], documents, hasHostScope);

            var artifactFiles = new List<OverlaySourceRow>();
            if (await InTxExistsAsync(conn, tx, "omp.ArtifactConfigurationFiles", ct))
                await using (var cmd = new SqlCommand(PreviewArtifactFilesSql, conn, tx))
                {
                    Add(cmd, "@id", artifactId);
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        artifactFiles.Add(new(reader.GetInt32(0), null, reader.GetString(1), reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetDateTime(5)));
                }

            var overlayFiles = new List<OverlaySourceRow>();
            foreach (var overlay in documents)
            {
                if (!overlay.IsEnabled || !ConfigOverlayAdministration.IsRelevantTo(overlay, snapshot, hostRow.HostKey, artifactRow)) continue;
                if (overlay.ArtifactVersion is not null && ArtifactVersionComparer.Compare(artifactRow.Version, overlay.ArtifactVersion) < 0) continue;
                await using var cmd = new SqlCommand(hasMerge ? PreviewOverlayFilesSql : PreviewOverlayFilesNoMergeSql, conn, tx);
                Add(cmd, "@id", overlay.ConfigOverlayDocumentId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    overlayFiles.Add(new(reader.GetInt32(0), overlay.ConfigOverlayDocumentId, reader.GetString(1), reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetDateTime(5)));
            }

            var fingerprint = ConfigOverlayAdministration.ComputePreviewFingerprint(snapshot, selected, hostRow, artifactRow, artifactFiles, overlayFiles);
            var expectedToken = ConfigOverlayAdministration.PreviewActionToken(fingerprint, enabled);
            if (!string.Equals(expectedToken, previewToken, StringComparison.Ordinal))
                throw new InvalidOperationException("The preview is stale. Reload and preview again before changing.");

            if (enabled == true)
            {
                await using var siblings = new SqlCommand(OverlayEnableSiblingsSql, conn, tx);
                Add(siblings, "@key", key); Add(siblings, "@host", host); Add(siblings, "@id", id);
                await siblings.ExecuteNonQueryAsync(ct);
            }
            var sql = enabled.HasValue ? OverlayUpdateStateSql : OverlayDeleteSql;
            await using var write = new SqlCommand(sql, conn, tx);
            Add(write, "@id", id);
            if (enabled.HasValue) Add(write, "@enabled", enabled.Value);
            await write.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    private static async Task<bool> InTxExistsAsync(SqlConnection conn, SqlTransaction tx, string table, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT CAST(CASE WHEN OBJECT_ID(@table, 'U') IS NULL THEN 0 ELSE 1 END AS bit);", conn, tx);
        cmd.Parameters.AddWithValue("@table", table);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    public static string OverlayEditToken(string hash, DateTime updated) => $"{hash}:{updated.Ticks}";
}
