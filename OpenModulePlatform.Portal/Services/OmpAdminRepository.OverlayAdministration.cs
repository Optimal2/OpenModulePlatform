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

    public static string OverlayEditToken(string hash, DateTime updated) => $"{hash}:{updated.Ticks}";
}
