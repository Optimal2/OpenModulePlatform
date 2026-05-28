// File: OpenModulePlatform.Portal/Services/PortalBlankWidgetService.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Stores shared blank-widget image metadata and media in the Portal database.
/// </summary>
public sealed class PortalBlankWidgetService
{
    public const long MaxImageBytes = 10L * 1024L * 1024L;
    public const long MaxZipBytes = 512L * 1024L * 1024L;

    private const string WidgetKey = "blank-rectangle";
    private const string DataKey = "blank-widget-images";
    private const string OwnerRef = "widget:blank-widget";
    private const string DocumentFormat = "omp.portal.blank-widget-images.v1";

    private static readonly HashSet<char> InvalidFileNameChars = [.. Path.GetInvalidFileNameChars()];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly SqlConnectionFactory _db;

    public PortalBlankWidgetService(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<BlankWidgetImageList> GetImagesAsync(Func<long, string> imageUrlFactory, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var document = await ReadDocumentAsync(conn, tx: null, ct);
        if (document.Images.Count == 0)
        {
            return new BlankWidgetImageList([]);
        }

        var media = await GetEnabledMediaMapAsync(conn, tx: null, document.Images.Select(image => image.BinaryDataId), ct);
        var images = document.Images
            .Where(image => media.ContainsKey(image.BinaryDataId))
            .OrderBy(image => image.SortOrder)
            .ThenBy(image => image.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(image => new BlankWidgetImage(
                image.BinaryDataId,
                CleanForDisplay(image.DisplayName, 200) ?? media[image.BinaryDataId].FileName,
                imageUrlFactory(image.BinaryDataId),
                media[image.BinaryDataId].FileName,
                media[image.BinaryDataId].ContentType,
                string.IsNullOrWhiteSpace(image.BinaryDataHash)
                    ? media[image.BinaryDataId].ContentHash
                    : image.BinaryDataHash))
            .ToArray();

        return new BlankWidgetImageList(images);
    }

    public async Task<BlankWidgetBinaryFile?> GetImageFileAsync(long binaryDataId, CancellationToken ct)
    {
        const string sql = @"
SELECT file_name,
       content_type,
       data_value
FROM omp_portal.widget_binary_data
WHERE binary_data_id = @binary_data_id
  AND owner_ref = @owner_ref
  AND is_enabled = 1;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@binary_data_id", SqlDbType.BigInt).Value = binaryDataId;
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = OwnerRef;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new BlankWidgetBinaryFile(
            rdr.GetString(0),
            rdr.GetString(1),
            (byte[])rdr.GetValue(2));
    }

    public async Task<BlankWidgetMutationResult> AddImageAsync(
        IFormFile file,
        string? displayName,
        int? userId,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            throw new InvalidOperationException("Upload one image or GIF file.");
        }

        var bytes = await ReadUploadBytesAsync(file, MaxImageBytes, ct);
        var contentType = DetectImageContentType(bytes, file.FileName);
        var image = new ImportedImage(
            CleanForDisplay(displayName, 200) ?? GetFileStem(file.FileName),
            CleanFileName(file.FileName, contentType),
            contentType,
            bytes);

        return await ImportImagesAsync([image], userId, ct);
    }

    public async Task<BlankWidgetMutationResult> ImportZipAsync(IFormFile file, int? userId, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            throw new InvalidOperationException("Upload a zip file.");
        }

        if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Upload a zip file.");
        }

        if (file.Length > MaxZipBytes)
        {
            throw new InvalidOperationException($"The zip file exceeds the limit of {MaxZipBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
        }

        await using var stream = file.OpenReadStream();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var metadata = ReadImagesJsonMetadata(archive);
        var imageEntries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && IsSupportedImageFileName(entry.Name))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (imageEntries.Length == 0)
        {
            throw new InvalidOperationException("The zip file does not contain any image or GIF files.");
        }

        var imported = new List<ImportedImage>();
        foreach (var entry in imageEntries)
        {
            if (entry.Length > MaxImageBytes)
            {
                throw new InvalidOperationException($"The image file '{entry.Name}' exceeds the limit of {MaxImageBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
            }

            await using var entryStream = entry.Open();
            var bytes = await ReadStreamBytesAsync(entryStream, MaxImageBytes, ct);
            var contentType = DetectImageContentType(bytes, entry.Name);
            var cleanFileName = CleanFileName(entry.Name, contentType);
            imported.Add(new ImportedImage(
                FindMetadataDisplayName(cleanFileName, metadata) ?? GetFileStem(cleanFileName),
                cleanFileName,
                contentType,
                bytes));
        }

        return await ImportImagesAsync(imported, userId, ct);
    }

    private async Task<BlankWidgetMutationResult> ImportImagesAsync(
        IReadOnlyList<ImportedImage> images,
        int? userId,
        CancellationToken ct)
    {
        if (images.Count == 0)
        {
            return new BlankWidgetMutationResult(0, 0, []);
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            var widgetId = await GetWidgetIdAsync(conn, tx, ct)
                ?? throw new InvalidOperationException("The blank widget definition is missing.");
            var document = await ReadDocumentAsync(conn, tx, ct);
            var existingImageIds = document.Images
                .Select(image => image.BinaryDataId)
                .ToHashSet();

            var addedImages = 0;
            var reusedImages = 0;
            var importedIds = new List<long>();
            foreach (var image in images)
            {
                var hash = SHA256.HashData(image.Bytes);
                var binaryDataId = await FindExistingBinaryDataIdAsync(conn, tx, hash, image.Bytes.LongLength, ct);
                if (!binaryDataId.HasValue)
                {
                    binaryDataId = await InsertBinaryDataAsync(conn, tx, image, hash, userId, ct);
                    addedImages++;
                }
                else
                {
                    reusedImages++;
                }

                importedIds.Add(binaryDataId.Value);
                if (existingImageIds.Add(binaryDataId.Value))
                {
                    document.Images.Add(new BlankWidgetImageDocumentItem
                    {
                        BinaryDataId = binaryDataId.Value,
                        BinaryDataHash = ToSha256Hex(hash),
                        DisplayName = image.DisplayName,
                        SortOrder = document.Images.Count + 1
                    });
                }
            }

            await UpsertDocumentAsync(conn, tx, widgetId, document, ct);
            await tx.CommitAsync(ct);
            return new BlankWidgetMutationResult(addedImages, reusedImages, importedIds);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<int?> GetWidgetIdAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        const string sql = @"
SELECT widget_id
FROM omp_portal.widgets
WHERE widget_key = @widget_key;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_key", SqlDbType.NVarChar, 200).Value = WidgetKey;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null || result == DBNull.Value
            ? null
            : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private async Task<BlankWidgetDataDocument> ReadDocumentAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        CancellationToken ct)
    {
        const string sql = @"
SELECT wd.json_data
FROM omp_portal.widget_data wd
INNER JOIN omp_portal.widgets w ON w.widget_id = wd.widget_id
WHERE w.widget_key = @widget_key
  AND wd.data_key = @data_key;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_key", SqlDbType.NVarChar, 200).Value = WidgetKey;
        cmd.Parameters.Add("@data_key", SqlDbType.NVarChar, 128).Value = DataKey;
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BlankWidgetDataDocument();
        }

        try
        {
            var document = JsonSerializer.Deserialize<BlankWidgetDataDocument>(json, JsonOptions)
                ?? new BlankWidgetDataDocument();
            document.Format = DocumentFormat;
            document.Images ??= [];
            return document;
        }
        catch (JsonException)
        {
            return new BlankWidgetDataDocument();
        }
    }

    private static async Task<IReadOnlyDictionary<long, BlankWidgetMediaRow>> GetEnabledMediaMapAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        IEnumerable<long> binaryDataIds,
        CancellationToken ct)
    {
        var ids = binaryDataIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<long, BlankWidgetMediaRow>();
        }

        var parameters = ids
            .Select((_, index) => $"@id{index.ToString(CultureInfo.InvariantCulture)}")
            .ToArray();
        var sql = $@"
SELECT data.binary_data_id,
       data.file_name,
       data.content_type,
       data.content_hash
FROM omp_portal.widget_binary_data AS data
WHERE data.owner_ref = @owner_ref
  AND data.is_enabled = 1
  AND data.binary_data_id IN ({string.Join(", ", parameters)});";

        var media = new Dictionary<long, BlankWidgetMediaRow>();
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = OwnerRef;
        for (var i = 0; i < ids.Length; i++)
        {
            cmd.Parameters.Add(parameters[i], SqlDbType.BigInt).Value = ids[i];
        }

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            media[rdr.GetInt64(0)] = new BlankWidgetMediaRow(
                rdr.GetString(1),
                rdr.GetString(2),
                ToSha256Hex((byte[])rdr.GetValue(3)));
        }

        return media;
    }

    private static async Task<long?> FindExistingBinaryDataIdAsync(
        SqlConnection conn,
        SqlTransaction tx,
        byte[] hash,
        long contentLength,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) binary_data_id
FROM omp_portal.widget_binary_data
WHERE owner_ref = @owner_ref
  AND content_hash = @content_hash
  AND content_length = @content_length
  AND is_enabled = 1
ORDER BY binary_data_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = OwnerRef;
        cmd.Parameters.Add("@content_hash", SqlDbType.VarBinary, 32).Value = hash;
        cmd.Parameters.Add("@content_length", SqlDbType.BigInt).Value = contentLength;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null || result == DBNull.Value
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long> InsertBinaryDataAsync(
        SqlConnection conn,
        SqlTransaction tx,
        ImportedImage image,
        byte[] hash,
        int? userId,
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
    1,
    @created_by_user_id,
    SYSUTCDATETIME(),
    SYSUTCDATETIME()
);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = OwnerRef;
        cmd.Parameters.Add("@file_name", SqlDbType.NVarChar, 260).Value = image.FileName;
        cmd.Parameters.Add("@content_type", SqlDbType.NVarChar, 128).Value = image.ContentType;
        cmd.Parameters.Add("@content_length", SqlDbType.BigInt).Value = image.Bytes.LongLength;
        cmd.Parameters.Add("@content_hash", SqlDbType.VarBinary, 32).Value = hash;
        cmd.Parameters.Add("@data_value", SqlDbType.VarBinary, -1).Value = image.Bytes;
        cmd.Parameters.Add("@created_by_user_id", SqlDbType.Int).Value = userId.HasValue ? userId.Value : DBNull.Value;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task UpsertDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int widgetId,
        BlankWidgetDataDocument document,
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

        document.Format = DocumentFormat;
        document.Images = document.Images
            .GroupBy(image => image.BinaryDataId)
            .Select(group => group.Last())
            .OrderBy(image => image.SortOrder)
            .ThenBy(image => image.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select((image, index) =>
            {
                image.SortOrder = index + 1;
                return image;
            })
            .ToList();

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
        cmd.Parameters.Add("@data_key", SqlDbType.NVarChar, 128).Value = DataKey;
        cmd.Parameters.Add("@json_data", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(document, JsonOptions);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<byte[]> ReadUploadBytesAsync(IFormFile file, long maxBytes, CancellationToken ct)
    {
        if (file.Length > maxBytes)
        {
            throw new InvalidOperationException($"The image file exceeds the limit of {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
        }

        await using var stream = file.OpenReadStream();
        return await ReadStreamBytesAsync(stream, maxBytes, ct);
    }

    private static async Task<byte[]> ReadStreamBytesAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        await using var target = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException($"The uploaded file exceeds the limit of {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return target.ToArray();
    }

    private static string DetectImageContentType(byte[] bytes, string fileName)
    {
        if (!IsSupportedImageFileName(fileName))
        {
            throw new InvalidOperationException("Upload a GIF, PNG, JPG, or JPEG file.");
        }

        if (bytes.Length >= 6
            && bytes[0] == (byte)'G'
            && bytes[1] == (byte)'I'
            && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'8'
            && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9')
            && bytes[5] == (byte)'a')
        {
            return "image/gif";
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47
            && bytes[4] == 0x0D
            && bytes[5] == 0x0A
            && bytes[6] == 0x1A
            && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        throw new InvalidOperationException("The uploaded file is not a supported image format.");
    }

    private static bool IsSupportedImageFileName(string fileName)
        => fileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static string CleanFileName(string value, string contentType)
    {
        var fileName = Path.GetFileName(value.Replace('\\', '/'));
        var cleaned = string.Concat(fileName.Where(ch => !InvalidFileNameChars.Contains(ch))).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "blank-widget-image";
        }

        if (Path.GetExtension(cleaned).Length == 0)
        {
            cleaned += contentType switch
            {
                "image/gif" => ".gif",
                "image/png" => ".png",
                _ => ".jpg"
            };
        }

        return cleaned.Length > 260 ? cleaned[..260] : cleaned;
    }

    private static string GetFileStem(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value).Trim();
        return string.IsNullOrWhiteSpace(stem) ? "Image" : stem;
    }

    private static string? CleanForDisplay(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static IReadOnlyDictionary<string, string> ReadImagesJsonMetadata(ZipArchive archive)
    {
        var entry = archive.Entries.FirstOrDefault(item =>
            string.Equals(item.Name, "images.json", StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length <= 0 || entry.Length > 1024 * 1024)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var image in images.EnumerateArray())
            {
                var fileName = image.TryGetProperty("fileName", out var fileNameProperty)
                    ? CleanForDisplay(fileNameProperty.GetString(), 260)
                    : null;
                var displayName = image.TryGetProperty("displayName", out var displayNameProperty)
                    ? CleanForDisplay(displayNameProperty.GetString(), 200)
                    : null;
                if (!string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(displayName))
                {
                    result[Path.GetFileName(fileName.Replace('\\', '/'))] = displayName;
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? FindMetadataDisplayName(string fileName, IReadOnlyDictionary<string, string> metadata)
        => metadata.TryGetValue(fileName, out var displayName)
            ? displayName
            : null;

    private static string ToSha256Hex(byte[] hash)
        => Convert.ToHexString(hash).ToLowerInvariant();

    private sealed record ImportedImage(
        string DisplayName,
        string FileName,
        string ContentType,
        byte[] Bytes);

    private sealed record BlankWidgetMediaRow(string FileName, string ContentType, string ContentHash);

    private sealed class BlankWidgetDataDocument
    {
        public string Format { get; set; } = DocumentFormat;

        public List<BlankWidgetImageDocumentItem> Images { get; set; } = [];
    }

    private sealed class BlankWidgetImageDocumentItem
    {
        public long BinaryDataId { get; set; }

        public string BinaryDataHash { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public int SortOrder { get; set; }
    }
}
