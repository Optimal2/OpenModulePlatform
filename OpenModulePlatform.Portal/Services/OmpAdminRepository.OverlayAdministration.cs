using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Services;

public sealed record OverlayEditorSave(int? DocumentId, string? ExpectedHash, string? Actor = null);

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
            documentsExist ? await GetConfigOverlayDocumentsAsync(ct) : [], hasScope,
            AppInstances: await GetAppInstancesAsync(ct),
            FileSummaries: documentsExist ? await GetOverlayFileSummariesAsync(ct) : [],
            HostDeploymentAssignments: await GetOverlayHostDeploymentAssignmentsAsync(ct));
    }

    async Task<OverlayAdminDetail?> IConfigOverlayAdminStore.DetailAsync(int id, CancellationToken ct)
    {
        var summary = (await GetConfigOverlayDocumentsAsync(ct)).SingleOrDefault(o => o.ConfigOverlayDocumentId == id);
        if (summary is null) return null;
        var document = await GetConfigOverlayJsonAsync(id, ct);
        if (document is null) return null;
        var files = await ReadOverlaySourceRowsAsync(id, true, ct);
        // Normalized rows are authoritative, including disabled rows. Rebuild
        // configurationFiles from those rows while preserving any extra per-file
        // metadata (extension keys) the stored document still carries, and hydrate
        // selectors that legacy metadata-only documents never wrote to JSON from
        // the authoritative stored columns.
        var root = JsonNode.Parse(document.Value.Json) as JsonObject ?? new JsonObject();
        var storedFiles = root["configurationFiles"] as JsonArray;
        var storedByPath = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (storedFiles is not null)
            foreach (var node in storedFiles.OfType<JsonObject>())
            {
                var path = node["relativePath"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(path)) storedByPath[path.Trim()] = node;
            }
        var rebuilt = new JsonArray();
        foreach (var file in files)
        {
            JsonObject entry;
            if (storedByPath.TryGetValue(file.RelativePath, out var existing))
            {
                entry = (JsonObject)existing.DeepClone();
            }
            else
            {
                entry = new JsonObject();
                storedByPath[file.RelativePath] = entry;
            }
            entry["relativePath"] = file.RelativePath;
            entry["fileContent"] = file.Content;
            entry["mergeMode"] = file.MergeMode;
            rebuilt.Add(entry);
        }
        root["configurationFiles"] = rebuilt;
        HydrateSelector(root, "hostScope", summary.HostScope);
        HydrateSelector(root, "hostKey", summary.HostKey);
        HydrateSelector(root, "moduleKey", summary.ModuleKey);
        HydrateSelector(root, "moduleDefinitionVersion", summary.ModuleDefinitionVersion);
        HydrateSelector(root, "appKey", summary.AppKey);
        HydrateSelector(root, "packageType", summary.PackageType);
        HydrateSelector(root, "targetName", summary.TargetName);
        HydrateSelector(root, "artifactVersion", summary.ArtifactVersion);
        return new(summary, root.ToJsonString(new() { WriteIndented = true }), files);
    }

    // Fills a selector from the authoritative stored column only when the
    // document JSON is missing it, so legacy metadata-only rows round-trip
    // without silently dropping selectors on the next save.
    private static void HydrateSelector(JsonObject root, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var current = root[name] is JsonValue existing && existing.TryGetValue<string>(out var text) ? text : null;
        if (string.IsNullOrWhiteSpace(current)) root[name] = value;
    }

    // Active host-template assignments, minimal columns, read only for the
    // installation-impact host resolution. Fails soft when the deployment
    // schema is absent, exactly like the other optional snapshot reads.
    private async Task<IReadOnlyList<HostDeploymentAssignmentRow>> GetOverlayHostDeploymentAssignmentsAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        if (!await TableExistsAsync(conn, "omp.HostDeploymentAssignments", ct)
            || !await TableExistsAsync(conn, "omp.HostTemplates", ct))
            return [];
        const string sql = """
            SELECT h.HostKey, t.TemplateKey, a.IsActive
            FROM omp.HostDeploymentAssignments a
            INNER JOIN omp.Hosts h ON h.HostId = a.HostId
            INNER JOIN omp.HostTemplates t ON t.HostTemplateId = a.HostTemplateId;
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<HostDeploymentAssignmentRow>();
        while (await rdr.ReadAsync(ct))
            rows.Add(new HostDeploymentAssignmentRow { HostKey = rdr.GetString(0), HostTemplateKey = rdr.GetString(1), IsActive = rdr.GetBoolean(2) });
        return rows;
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
        string? expectedHash, string actor, CancellationToken ct)
        => (await SaveImportedConfigOverlayAsync(document, id.HasValue, ct, new(id, expectedHash, actor))).DocumentId;

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

    async Task IConfigOverlayAdminStore.ChangeStateAsync(int id, string expectedHash, bool? enabled, string actor, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var hasHostScope = await ColumnExistsAsync(conn, tx, "omp.ConfigOverlayDocuments", "HostScope", ct);
            var hasMerge = await ColumnExistsAsync(conn, tx, "omp.ConfigOverlayConfigurationFiles", "MergeMode", ct);
            var row = await ReadOverlayRowInTxAsync(conn, tx, id, hasHostScope, ct);
            if (expectedHash != OverlayEditToken(row.OverlaySha256, row.UpdatedUtc)) throw new InvalidOperationException("Overlay changed. Reload before saving.");
            var files = await ReadOverlayFileMetaInTxAsync(conn, tx, id, hasMerge, ct);
            IReadOnlyList<int> disabledSiblings = [];
            if (enabled == true)
            {
                disabledSiblings = await ReadEnabledSiblingIdsAsync(conn, tx, row.OverlayKey, row.HostKey, id, ct);
                await using var siblings = new SqlCommand(OverlayEnableSiblingsSql, conn, tx);
                Add(siblings, "@key", row.OverlayKey); Add(siblings, "@host", row.HostKey); Add(siblings, "@id", id);
                await siblings.ExecuteNonQueryAsync(ct);
            }
            // Audit before the mutation: a delete tombstone survives the
            // cascading file-row removal because it is written first. Metadata
            // only; configuration content is never recorded.
            var before = OverlayMetadataJson(row, files);
            var after = enabled.HasValue ? OverlayMetadataJson(row, files, enabled.Value, disabledSiblings) : null;
            await WriteOverlayAuditInTxAsync(conn, tx, actor,
                enabled is null ? "Delete" : enabled.Value ? "Enable" : "Disable", id, before, after, ct);
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
        string hostKey, int artifactId, string previewToken, string actor, CancellationToken ct)
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

            IReadOnlyList<int> disabledSiblings = [];
            if (enabled == true)
            {
                disabledSiblings = await ReadEnabledSiblingIdsAsync(conn, tx, key, host, id, ct);
                await using var siblings = new SqlCommand(OverlayEnableSiblingsSql, conn, tx);
                Add(siblings, "@key", key); Add(siblings, "@host", host); Add(siblings, "@id", id);
                await siblings.ExecuteNonQueryAsync(ct);
            }
            // Audit before the mutation; a delete tombstone survives the
            // cascading file-row removal. Metadata only, never content.
            var files = await ReadOverlayFileMetaInTxAsync(conn, tx, id, hasMerge, ct);
            var before = OverlayMetadataJson(selected, files);
            var after = enabled.HasValue ? OverlayMetadataJson(selected, files, enabled.Value, disabledSiblings) : null;
            await WriteOverlayAuditInTxAsync(conn, tx, actor,
                enabled is null ? "Delete" : enabled.Value ? "Enable" : "Disable", id, before, after, ct);
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

    // Metadata-only per-file row for the saved-overlays list and the audit
    // tombstone. Content is deliberately absent from both.
    private sealed record OverlayFileMetaRow(string RelativePath, string? MergeMode, bool IsEnabled);

    private async Task<IReadOnlyList<OverlayFileSummaryRow>> GetOverlayFileSummariesAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        if (!await TableExistsAsync(conn, "omp.ConfigOverlayConfigurationFiles", ct)) return [];
        var hasMerge = await ColumnExistsAsync(conn, "omp.ConfigOverlayConfigurationFiles", "MergeMode", ct);
        var merge = hasMerge ? "MergeMode" : "CAST(NULL AS nvarchar(20))";
        // Only the fixed identifiers above are interpolated; the requested identity is a parameter.
        var sql = $"SELECT ConfigOverlayDocumentId, RelativePath, {merge}, IsEnabled FROM omp.ConfigOverlayConfigurationFiles ORDER BY ConfigOverlayDocumentId, RelativePath;";
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<OverlayFileSummaryRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3)));
        return rows;
    }

    // Full document row for one id, locked for the mutation transaction. Used
    // by ChangeStateAsync to check the concurrency token and build the audit
    // metadata. Throws when the row is gone.
    private static async Task<ConfigOverlayDocumentRow> ReadOverlayRowInTxAsync(
        SqlConnection conn, SqlTransaction tx, int id, bool hasHostScope, CancellationToken ct)
    {
        var hostScopeExpr = hasHostScope ? "overlay.HostScope" : "CAST(N'Host' AS nvarchar(20))";
        var sql = $@"
SELECT overlay.ConfigOverlayDocumentId, overlay.OverlayKey, overlay.OverlayVersion, overlay.HostKey,
       {hostScopeExpr}, overlay.ModuleKey, overlay.ModuleDefinitionVersion, overlay.AppKey, overlay.PackageType,
       overlay.TargetName, overlay.ArtifactVersion, overlay.OverlaySha256, overlay.SourceName,
       overlay.IsEnabled, overlay.UpdatedUtc
FROM omp.ConfigOverlayDocuments overlay WITH (UPDLOCK, HOLDLOCK)
WHERE overlay.ConfigOverlayDocumentId = @id;";
        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Overlay no longer exists.");
        return new()
        {
            ConfigOverlayDocumentId = reader.GetInt32(0),
            OverlayKey = reader.GetString(1),
            OverlayVersion = reader.GetString(2),
            HostKey = reader.GetString(3),
            HostScope = reader.IsDBNull(4) ? "Host" : reader.GetString(4),
            ModuleKey = reader.IsDBNull(5) ? null : reader.GetString(5),
            ModuleDefinitionVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
            AppKey = reader.IsDBNull(7) ? null : reader.GetString(7),
            PackageType = reader.IsDBNull(8) ? null : reader.GetString(8),
            TargetName = reader.IsDBNull(9) ? null : reader.GetString(9),
            ArtifactVersion = reader.IsDBNull(10) ? null : reader.GetString(10),
            OverlaySha256 = reader.GetString(11),
            SourceName = reader.IsDBNull(12) ? null : reader.GetString(12),
            ConfigurationFileCount = 0,
            IsEnabled = reader.GetBoolean(13),
            UpdatedUtc = reader.GetDateTime(14),
        };
    }

    // Metadata-only file rows for one document, locked for the mutation
    // transaction. Content is never read: the audit tombstone must not carry
    // configuration content, only paths, modes and enabled state.
    private static async Task<IReadOnlyList<OverlayFileMetaRow>> ReadOverlayFileMetaInTxAsync(
        SqlConnection conn, SqlTransaction tx, int id, bool hasMerge, CancellationToken ct)
    {
        var merge = hasMerge ? "MergeMode" : "CAST(NULL AS nvarchar(20))";
        var sql = $"SELECT RelativePath, {merge}, IsEnabled FROM omp.ConfigOverlayConfigurationFiles WITH (UPDLOCK, HOLDLOCK) WHERE ConfigOverlayDocumentId = @id ORDER BY RelativePath;";
        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<OverlayFileMetaRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2)));
        return rows;
    }

    // Enabled sibling ids, read BEFORE the disable statement runs so enabling
    // this row can record exactly which siblings it switched off.
    private static async Task<IReadOnlyList<int>> ReadEnabledSiblingIdsAsync(
        SqlConnection conn, SqlTransaction tx, string key, string host, int id, CancellationToken ct)
    {
        const string sql = """
            SELECT ConfigOverlayDocumentId
            FROM omp.ConfigOverlayDocuments WITH (UPDLOCK, HOLDLOCK)
            WHERE OverlayKey = @key AND HostKey = @host AND ConfigOverlayDocumentId <> @id AND IsEnabled = 1;
            """;
        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@key", key); Add(cmd, "@host", host); Add(cmd, "@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<int>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static async Task WriteOverlayAuditInTxAsync(SqlConnection conn, SqlTransaction tx, string actor,
        string action, int documentId, string? beforeJson, string? afterJson, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO omp.AuditLog(Actor, Action, TargetType, TargetId, BeforeJson, AfterJson)
            VALUES(@actor, @action, @target_type, @target_id, @before_json, @after_json);
            """;
        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@actor", TruncateForAudit(string.IsNullOrWhiteSpace(actor) ? "PortalAdmin" : actor.Trim(), 256));
        Add(cmd, "@action", TruncateForAudit(action, 200));
        Add(cmd, "@target_type", "ConfigOverlay");
        Add(cmd, "@target_id", TruncateForAudit(documentId.ToString(CultureInfo.InvariantCulture), 200));
        Add(cmd, "@before_json", beforeJson);
        Add(cmd, "@after_json", afterJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Audit metadata is identity + selectors + source + enabled state + the
    // file list (paths, modes, enabled). No configuration content, hashes or
    // secrets are ever recorded. The delete tombstone is the "before" shape
    // written before the cascading file-row removal.
    private static string OverlayMetadataJson(ConfigOverlayDocumentRow row, IReadOnlyList<OverlayFileMetaRow> files)
        => JsonSerializer.Serialize(Metadata(row, row.IsEnabled, null, files));

    private static string OverlayMetadataJson(ConfigOverlayDocumentRow row, IReadOnlyList<OverlayFileMetaRow> files,
        bool enabled, IReadOnlyList<int> disabledSiblings)
        => JsonSerializer.Serialize(Metadata(row, enabled, disabledSiblings, files));

    private static object Metadata(ConfigOverlayDocumentRow row, bool enabled, IReadOnlyList<int>? disabledSiblings,
        IReadOnlyList<OverlayFileMetaRow> files)
        => new
        {
            documentId = row.ConfigOverlayDocumentId,
            overlayKey = row.OverlayKey,
            overlayVersion = row.OverlayVersion,
            hostKey = row.HostKey,
            hostScope = row.HostScope,
            moduleKey = row.ModuleKey,
            moduleDefinitionVersion = row.ModuleDefinitionVersion,
            appKey = row.AppKey,
            packageType = row.PackageType,
            targetName = row.TargetName,
            artifactVersion = row.ArtifactVersion,
            sourceName = row.SourceName,
            isEnabled = enabled,
            configurationFileCount = files.Count,
            disabledSiblingDocumentIds = disabledSiblings,
            files = files.Select(f => new { relativePath = f.RelativePath, mergeMode = f.MergeMode, isEnabled = f.IsEnabled }).ToArray(),
        };

    // Save-path audit metadata built from the portable document before the
    // normalized rows exist. Identity + selectors + source + enabled state +
    // the file list (paths, modes). No file content or hashes.
    private static string OverlayMetadataJson(PortableConfigOverlayDocument input, int documentId, bool isEnabled)
        => JsonSerializer.Serialize(new
        {
            documentId,
            overlayKey = input.OverlayKey,
            overlayVersion = input.OverlayVersion,
            hostKey = input.HostKey,
            hostScope = ConfigOverlayHostScopeNames.ToName(input.HostScope),
            moduleKey = input.ModuleKey,
            moduleDefinitionVersion = input.ModuleDefinitionVersion,
            appKey = input.AppKey,
            packageType = input.PackageType,
            targetName = input.TargetName,
            artifactVersion = input.ArtifactVersion,
            sourceName = input.SourceName,
            isEnabled,
            configurationFileCount = input.ConfigurationFiles.Count,
            disabledSiblingDocumentIds = (int[]?)null,
            files = input.ConfigurationFiles.Select(f => new { relativePath = f.RelativePath, mergeMode = f.MergeMode, isEnabled = true }).ToArray(),
        });

    async Task<IReadOnlyList<OverlayDeletedHistoryRow>> IConfigOverlayAdminStore.DeletedHistoryAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        if (!await TableExistsAsync(conn, "omp.AuditLog", ct)) return [];
        const string sql = """
            SELECT TOP (200) TargetId, BeforeJson, CreatedUtc, Actor
            FROM omp.AuditLog
            WHERE TargetType = 'ConfigOverlay' AND Action = 'Delete' AND BeforeJson IS NOT NULL
            ORDER BY CreatedUtc DESC, AuditLogId DESC;
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<OverlayDeletedHistoryRow>();
        while (await reader.ReadAsync(ct))
        {
            var parsed = ParseDeletedHistoryRow(reader.GetString(0), reader.GetString(1), reader.GetDateTime(2), reader.GetString(3));
            if (parsed is not null) rows.Add(parsed);
        }
        return rows;
    }

    // Reconstructs the metadata-only tombstone from the audit "before" JSON.
    // Returns null for rows that are not a valid overlay tombstone, so a
    // malformed legacy audit row is skipped rather than surfaced as a live row.
    private static OverlayDeletedHistoryRow? ParseDeletedHistoryRow(string targetId, string beforeJson, DateTime deletedUtc, string actor)
    {
        try
        {
            var root = JsonNode.Parse(beforeJson) as JsonObject;
            if (root is null) return null;
            var documentId = Int(root, "documentId");
            if (!documentId.HasValue && int.TryParse(targetId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId)) documentId = parsedId;
            if (!documentId.HasValue) return null;
            var files = root["files"] as JsonArray;
            return new OverlayDeletedHistoryRow
            {
                ConfigOverlayDocumentId = documentId.Value,
                OverlayKey = Str(root, "overlayKey") ?? string.Empty,
                OverlayVersion = Str(root, "overlayVersion") ?? string.Empty,
                HostKey = Str(root, "hostKey") ?? string.Empty,
                HostScope = Str(root, "hostScope") ?? "Host",
                ModuleKey = Str(root, "moduleKey"),
                AppKey = Str(root, "appKey"),
                PackageType = Str(root, "packageType"),
                TargetName = Str(root, "targetName"),
                ArtifactVersion = Str(root, "artifactVersion"),
                SourceName = Str(root, "sourceName"),
                ConfigurationFileCount = files?.Count ?? Int(root, "configurationFileCount") ?? 0,
                DeletedUtc = deletedUtc,
                DeletedBy = string.IsNullOrWhiteSpace(actor) ? null : actor,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Int(JsonObject root, string name)
        => root[name]?.GetValue<int?>();

    private static string? Str(JsonObject root, string name)
    {
        var node = root[name];
        // A non-string stored node is an anomalous document, reported as unreadable, never a 500.
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
