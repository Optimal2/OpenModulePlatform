using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Imports and exports shared widget runtime data for universal packages.
/// Widget definitions are handled separately; this service transports
/// widget_data JSON and remappable widget_binary_data rows.
/// </summary>
public sealed class PortalWidgetRuntimeDataPackageService
{
    private const string PackageObjectType = "omp.portal.widget-runtime-data";
    private const string WidgetDataFolder = "widget-data";
    private const string BinaryFolder = "binary";
    private const string BinaryDataIdPropertyName = "binaryDataId";
    private const string BinaryDataHashPropertyName = "binaryDataHash";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly SqlConnectionFactory _db;

    public PortalWidgetRuntimeDataPackageService(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<WidgetRuntimeDataPackageExportResult?> ExportAsync(
        IReadOnlyList<int> widgetIds,
        string? packageVersion,
        CancellationToken ct)
    {
        var normalizedWidgetIds = widgetIds
            .Where(static id => id > 0)
            .Distinct()
            .Order()
            .ToArray();
        if (normalizedWidgetIds.Length == 0)
        {
            return null;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var documents = await GetWidgetDataDocumentsAsync(conn, normalizedWidgetIds, ct);
        if (documents.Count == 0)
        {
            return null;
        }

        var binaryReferences = documents
            .Select(static document => CollectBinaryDataReferences(document.JsonData))
            .Aggregate(BinaryDataReferences.Empty, static (left, right) => left.Merge(right));
        var binaryRows = await GetBinaryRowsAsync(conn, binaryReferences.BinaryDataIds, binaryReferences.ContentHashes, ct);
        var binaryRowsById = binaryRows.ToDictionary(static row => row.BinaryDataId);

        var tempRoot = CreateTempRoot("portal-widget-runtime-data-export");
        var firstWidgetKey = documents
            .Select(static document => document.WidgetKey)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .First();
        var fileName = $"{SanitizePathSegment(firstWidgetKey)}__widget-data.zip";
        var packagePath = Path.Join(tempRoot, fileName);

        try
        {
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                var manifest = new JsonObject
                {
                    ["formatVersion"] = 1,
                    ["objectType"] = PackageObjectType
                };
                if (!string.IsNullOrWhiteSpace(packageVersion))
                {
                    manifest["packageVersion"] = packageVersion.Trim();
                }

                var dataArray = new JsonArray();
                foreach (var document in documents)
                {
                    var jsonData = AddBinaryDataHashes(document.JsonData, binaryRowsById);
                    dataArray.Add(new JsonObject
                    {
                        ["widgetKey"] = document.WidgetKey,
                        ["dataKey"] = document.DataKey,
                        ["jsonData"] = jsonData
                    });
                }

                manifest["data"] = dataArray;

                var binaryArray = new JsonArray();
                var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in binaryRows)
                {
                    var entryName = BuildBinaryEntryName(row.BinaryDataId, row.FileName, usedEntryNames);
                    await AddBytesEntryAsync(archive, entryName, row.Data, ct);
                    binaryArray.Add(new JsonObject
                    {
                        ["sourceBinaryDataId"] = row.BinaryDataId,
                        ["ownerRef"] = row.OwnerRef,
                        ["fileName"] = row.FileName,
                        ["contentType"] = row.ContentType,
                        ["contentLength"] = row.ContentLength,
                        ["contentHash"] = Convert.ToHexString(row.ContentHash).ToLowerInvariant(),
                        ["isEnabled"] = row.IsEnabled,
                        ["path"] = entryName
                    });
                }

                manifest["binaryData"] = binaryArray;
                await AddTextEntryAsync(
                    archive,
                    WidgetRuntimeDataPackageReader.ManifestEntryName,
                    manifest.ToJsonString(JsonOptions),
                    ct);
            }

            return new WidgetRuntimeDataPackageExportResult(
                packagePath,
                fileName,
                string.IsNullOrWhiteSpace(packageVersion) ? null : packageVersion.Trim(),
                documents.Count,
                binaryRows.Count);
        }
        catch
        {
            TryDelete(tempRoot);
            throw;
        }
    }

    public async Task<WidgetRuntimeDataImportResult> ImportAsync(
        string packagePath,
        CancellationToken ct)
    {
        var package = new WidgetRuntimeDataPackageReader().Read(packagePath);

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
            foreach (var binary in package.BinaryData)
            {
                var targetId = await FindExistingBinaryDataIdAsync(conn, tx, binary, ct);
                if (targetId.HasValue)
                {
                    reusedBinaryRows++;
                    await UpdateBinaryDataEnabledStateAsync(conn, tx, targetId.Value, binary.IsEnabled, ct);
                }
                else
                {
                    targetId = await InsertBinaryDataAsync(conn, tx, binary, ct);
                    insertedBinaryRows++;
                }

                if (binary.SourceBinaryDataId > 0)
                {
                    binaryIdMap[binary.SourceBinaryDataId] = targetId.Value;
                }

                binaryHashMap[ToSha256Hex(binary.ContentHash)] = targetId.Value;
            }

            var dataDocuments = 0;
            foreach (var document in package.DataDocuments)
            {
                var widgetId = await GetWidgetIdAsync(conn, tx, document.WidgetKey, ct)
                    ?? throw new InvalidOperationException($"Dashboard widget '{document.WidgetKey}' was not found. Import the widget definition before importing widget runtime data.");
                var jsonData = RemapBinaryDataReferences(document.JsonData, binaryIdMap, binaryHashMap);
                await UpsertWidgetDataAsync(conn, tx, widgetId, document.DataKey, jsonData, ct);
                dataDocuments++;
            }

            await tx.CommitAsync(ct);
            return new WidgetRuntimeDataImportResult(dataDocuments, insertedBinaryRows, reusedBinaryRows);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task<IReadOnlyList<WidgetDataExportRow>> GetWidgetDataDocumentsAsync(
        SqlConnection conn,
        IReadOnlyList<int> widgetIds,
        CancellationToken ct)
    {
        var parameters = widgetIds
            .Select((_, index) => $"@widget_id{index.ToString(CultureInfo.InvariantCulture)}")
            .ToArray();
        var sql = $@"
SELECT w.widget_key,
       wd.data_key,
       wd.json_data
FROM omp_portal.widget_data wd
INNER JOIN omp_portal.widgets w ON w.widget_id = wd.widget_id
WHERE wd.widget_id IN ({string.Join(", ", parameters)})
ORDER BY w.widget_key, wd.data_key;";

        await using var cmd = new SqlCommand(sql, conn);
        for (var i = 0; i < widgetIds.Count; i++)
        {
            cmd.Parameters.Add(parameters[i], SqlDbType.Int).Value = widgetIds[i];
        }

        var rows = new List<WidgetDataExportRow>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new WidgetDataExportRow(
                rdr.GetString(0),
                rdr.GetString(1),
                rdr.GetString(2)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<WidgetBinaryDataExportRow>> GetBinaryRowsAsync(
        SqlConnection conn,
        IReadOnlyList<long> binaryIds,
        IReadOnlyList<string> contentHashes,
        CancellationToken ct)
    {
        if (binaryIds.Count == 0 && contentHashes.Count == 0)
        {
            return [];
        }

        var parameters = binaryIds
            .Select((_, index) => $"@binary_id{index.ToString(CultureInfo.InvariantCulture)}")
            .ToArray();
        var hashParameters = contentHashes
            .Select((_, index) => $"@content_hash{index.ToString(CultureInfo.InvariantCulture)}")
            .ToArray();
        var filters = new List<string>();
        if (parameters.Length > 0)
        {
            filters.Add($"binary_data_id IN ({string.Join(", ", parameters)})");
        }

        if (hashParameters.Length > 0)
        {
            filters.Add($"content_hash IN ({string.Join(", ", hashParameters)})");
        }

        var sql = $@"
SELECT binary_data_id,
       owner_ref,
       file_name,
       content_type,
       content_length,
       content_hash,
       data_value,
       is_enabled
FROM omp_portal.widget_binary_data
WHERE {string.Join(" OR ", filters)}
ORDER BY binary_data_id;";

        await using var cmd = new SqlCommand(sql, conn);
        for (var i = 0; i < binaryIds.Count; i++)
        {
            cmd.Parameters.Add(parameters[i], SqlDbType.BigInt).Value = binaryIds[i];
        }

        for (var i = 0; i < contentHashes.Count; i++)
        {
            cmd.Parameters.Add(hashParameters[i], SqlDbType.VarBinary, 32).Value = Convert.FromHexString(contentHashes[i]);
        }

        var rows = new List<WidgetBinaryDataExportRow>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new WidgetBinaryDataExportRow(
                rdr.GetInt64(0),
                rdr.GetString(1),
                rdr.GetString(2),
                rdr.GetString(3),
                rdr.GetInt64(4),
                (byte[])rdr.GetValue(5),
                (byte[])rdr.GetValue(6),
                rdr.GetBoolean(7)));
        }

        return rows;
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
            throw new InvalidOperationException("Portal widget runtime data tables are not available. Apply the Portal module definition before importing widget runtime data.");
        }
    }

    private static async Task<int?> GetWidgetIdAsync(
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
            : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long?> FindExistingBinaryDataIdAsync(
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
        AddBinaryIdentityParameters(cmd, binary);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null || result == DBNull.Value
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task UpdateBinaryDataEnabledStateAsync(
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

    private static async Task<long> InsertBinaryDataAsync(
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
        AddBinaryIdentityParameters(cmd, binary);
        cmd.Parameters.Add("@data_value", SqlDbType.VarBinary, -1).Value = binary.Data;
        cmd.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = binary.IsEnabled;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task UpsertWidgetDataAsync(
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

    private static void AddBinaryIdentityParameters(
        SqlCommand cmd,
        PortableWidgetRuntimeBinaryData binary)
    {
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = binary.OwnerRef;
        cmd.Parameters.Add("@content_hash", SqlDbType.VarBinary, 32).Value = binary.ContentHash;
        cmd.Parameters.Add("@content_length", SqlDbType.BigInt).Value = binary.ContentLength;
    }

    private static string RemapBinaryDataReferences(
        JsonNode source,
        IReadOnlyDictionary<long, long> binaryIdMap,
        IReadOnlyDictionary<string, long> binaryHashMap)
    {
        var clone = JsonNode.Parse(source.ToJsonString(JsonOptions))
            ?? throw new InvalidOperationException("Widget runtime data jsonData must be valid JSON.");
        RemapBinaryDataReferencesInPlace(clone, binaryIdMap, binaryHashMap);
        return clone.ToJsonString(JsonOptions);
    }

    private static void RemapBinaryDataReferencesInPlace(
        JsonNode node,
        IReadOnlyDictionary<long, long> binaryIdMap,
        IReadOnlyDictionary<string, long> binaryHashMap)
    {
        if (node is JsonObject obj)
        {
            var binaryDataIdPropertyName = FindPropertyName(obj, BinaryDataIdPropertyName);
            var hashTargetId = TryResolveBinaryDataHash(obj, binaryHashMap, out var targetFromHash)
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
                    throw new InvalidOperationException($"Widget runtime data references missing binaryDataId {sourceId.ToString(CultureInfo.InvariantCulture)}.");
                }
            }
            else if (hashTargetId.HasValue)
            {
                obj[BinaryDataIdPropertyName] = hashTargetId.Value;
            }

            foreach (var value in obj.Select(static property => property.Value).Where(static value => value is not null).ToArray())
            {
                RemapBinaryDataReferencesInPlace(value!, binaryIdMap, binaryHashMap);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(static child => child is not null))
            {
                RemapBinaryDataReferencesInPlace(child!, binaryIdMap, binaryHashMap);
            }
        }
    }

    private static JsonNode AddBinaryDataHashes(
        string jsonData,
        IReadOnlyDictionary<long, WidgetBinaryDataExportRow> binaryRowsById)
    {
        var clone = JsonNode.Parse(jsonData)
            ?? throw new InvalidOperationException("Widget runtime data jsonData must be valid JSON.");
        AddBinaryDataHashesInPlace(clone, binaryRowsById);
        return clone;
    }

    private static void AddBinaryDataHashesInPlace(
        JsonNode node,
        IReadOnlyDictionary<long, WidgetBinaryDataExportRow> binaryRowsById)
    {
        if (node is JsonObject obj)
        {
            var binaryDataIdPropertyName = FindPropertyName(obj, BinaryDataIdPropertyName);
            var binaryDataHashPropertyName = FindPropertyName(obj, BinaryDataHashPropertyName);
            if (binaryDataIdPropertyName is not null
                && binaryDataHashPropertyName is null
                && obj[binaryDataIdPropertyName] is JsonValue idValue
                && idValue.TryGetValue<long>(out var sourceId)
                && binaryRowsById.TryGetValue(sourceId, out var row))
            {
                obj[BinaryDataHashPropertyName] = ToSha256Hex(row.ContentHash);
            }

            foreach (var child in obj.Select(static property => property.Value).Where(static value => value is not null).ToArray())
            {
                AddBinaryDataHashesInPlace(child!, binaryRowsById);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(static child => child is not null))
            {
                AddBinaryDataHashesInPlace(child!, binaryRowsById);
            }
        }
    }

    private static BinaryDataReferences CollectBinaryDataReferences(string jsonData)
    {
        try
        {
            var root = JsonNode.Parse(jsonData);
            if (root is null)
            {
                return BinaryDataReferences.Empty;
            }

            var ids = new HashSet<long>();
            var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectBinaryDataReferences(root, ids, hashes);
            return new BinaryDataReferences(ids.Order().ToArray(), hashes.OrderBy(static hash => hash, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (JsonException)
        {
            return BinaryDataReferences.Empty;
        }
    }

    private static void CollectBinaryDataReferences(JsonNode node, HashSet<long> ids, HashSet<string> hashes)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key.Equals(BinaryDataIdPropertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value is JsonValue value
                    && value.TryGetValue<long>(out var id)
                    && id > 0)
                {
                    ids.Add(id);
                    continue;
                }

                if (property.Key.Equals(BinaryDataHashPropertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value is JsonValue hashValue
                    && hashValue.TryGetValue<string>(out var hashText)
                    && TryNormalizeSha256Hex(hashText, out var normalizedHash))
                {
                    hashes.Add(normalizedHash);
                    continue;
                }

                if (property.Value is not null)
                {
                    CollectBinaryDataReferences(property.Value, ids, hashes);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(static child => child is not null))
            {
                CollectBinaryDataReferences(child!, ids, hashes);
            }
        }
    }

    private static bool TryResolveBinaryDataHash(
        JsonObject obj,
        IReadOnlyDictionary<string, long> binaryHashMap,
        out long targetId)
    {
        targetId = 0;
        var propertyName = FindPropertyName(obj, BinaryDataHashPropertyName);
        if (propertyName is null
            || obj[propertyName] is not JsonValue hashValue
            || !hashValue.TryGetValue<string>(out var hashText)
            || !TryNormalizeSha256Hex(hashText, out var normalizedHash))
        {
            return false;
        }

        return binaryHashMap.TryGetValue(normalizedHash, out targetId);
    }

    private static string? FindPropertyName(JsonObject obj, string propertyName)
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

    private static string BuildBinaryEntryName(
        long binaryDataId,
        string fileName,
        HashSet<string> usedEntryNames)
    {
        var baseName = $"{binaryDataId.ToString(CultureInfo.InvariantCulture)}-{SanitizePathSegment(fileName)}";
        var entryName = $"{BinaryFolder}/{baseName}";
        var counter = 2;
        while (!usedEntryNames.Add(entryName))
        {
            var extension = Path.GetExtension(baseName);
            var stem = Path.GetFileNameWithoutExtension(baseName);
            entryName = $"{BinaryFolder}/{stem}-{counter.ToString(CultureInfo.InvariantCulture)}{extension}";
            counter++;
        }

        return entryName;
    }

    private static async Task AddTextEntryAsync(
        ZipArchive archive,
        string entryName,
        string content,
        CancellationToken ct)
        => await AddBytesEntryAsync(
            archive,
            entryName,
            Utf8NoBom.GetBytes(content),
            ct);

    private static async Task AddBytesEntryAsync(
        ZipArchive archive,
        string entryName,
        byte[] content,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(NormalizePackageEntryPath(entryName), CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(content.AsMemory(), ct);
    }

    private static string NormalizePackageEntryPath(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("Widget runtime data package entry paths must be relative paths.");
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidOperationException("Widget runtime data package entry paths must stay inside the package.");
        }

        return string.Join('/', segments);
    }

    private static string CreateTempRoot(string name)
    {
        var path = Path.Join(Path.GetTempPath(), "OpenModulePlatform", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SanitizePathSegment(string value)
    {
        var sanitized = value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '-');
        }

        return sanitized.Replace(' ', '-');
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Temporary export cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary export cleanup is best effort.
        }
    }

    private sealed record WidgetDataExportRow(
        string WidgetKey,
        string DataKey,
        string JsonData);

    private sealed record WidgetBinaryDataExportRow(
        long BinaryDataId,
        string OwnerRef,
        string FileName,
        string ContentType,
        long ContentLength,
        byte[] ContentHash,
        byte[] Data,
        bool IsEnabled);

    private sealed record BinaryDataReferences(
        IReadOnlyList<long> BinaryDataIds,
        IReadOnlyList<string> ContentHashes)
    {
        public static BinaryDataReferences Empty { get; } = new([], []);

        public BinaryDataReferences Merge(BinaryDataReferences other)
            => new(
                BinaryDataIds.Concat(other.BinaryDataIds).Distinct().Order().ToArray(),
                ContentHashes
                    .Concat(other.ContentHashes)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static hash => hash, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
    }
}

public sealed record WidgetRuntimeDataPackageExportResult(
    string PackagePath,
    string FileName,
    string? PackageVersion,
    int DataDocumentCount,
    int BinaryDataCount);

public sealed record WidgetRuntimeDataImportResult(
    int DataDocumentCount,
    int InsertedBinaryDataCount,
    int ReusedBinaryDataCount);
