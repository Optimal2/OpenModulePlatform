using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenModulePlatform.Artifacts;

public sealed record PortableWidgetRuntimeDataPackage(
    int FormatVersion,
    string SourceName,
    IReadOnlyList<PortableWidgetRuntimeDataDocument> DataDocuments,
    IReadOnlyList<PortableWidgetRuntimeBinaryData> BinaryData);

public sealed record PortableWidgetRuntimeDataDocument(
    string WidgetKey,
    string DataKey,
    JsonNode JsonData);

public sealed record PortableWidgetRuntimeBinaryData(
    long SourceBinaryDataId,
    string OwnerRef,
    string FileName,
    string ContentType,
    long ContentLength,
    byte[] ContentHash,
    bool IsEnabled,
    byte[] Data);

/// <summary>
/// Reads the widget runtime-data object used inside universal module packages.
/// Runtime data is separate from widget definitions because the JSON documents
/// may reference binary database rows that must be remapped during import.
/// </summary>
public sealed class WidgetRuntimeDataPackageReader
{
    public const string ManifestEntryName = "omp-widget-runtime-data.json";

    private const int MaxManifestBytes = 1024 * 1024 * 5;
    private const long MaxBinaryBytes = 512L * 1024L * 1024L;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public PortableWidgetRuntimeDataPackage Read(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var manifestEntry = FindEntry(archive, ManifestEntryName)
            ?? throw new InvalidOperationException(
                $"Widget runtime data packages must contain {ManifestEntryName}.");
        var manifest = ReadManifest(manifestEntry);
        var formatVersion = GetInt(manifest, "formatVersion", 1);
        if (formatVersion != 1)
        {
            throw new InvalidOperationException("Widget runtime data package formatVersion must be 1.");
        }

        var dataDocuments = ReadDataDocuments(manifest);
        var binaryData = ReadBinaryData(archive, manifest);
        if (dataDocuments.Count == 0 && binaryData.Count == 0)
        {
            throw new InvalidOperationException("Widget runtime data packages must contain at least one data or binary row.");
        }

        return new PortableWidgetRuntimeDataPackage(
            formatVersion,
            Path.GetFileName(zipPath),
            dataDocuments,
            binaryData);
    }

    private static IReadOnlyList<PortableWidgetRuntimeDataDocument> ReadDataDocuments(JsonObject manifest)
    {
        if (!TryGetProperty(manifest, "data", out var node) || node is not JsonArray dataArray)
        {
            return [];
        }

        var result = new List<PortableWidgetRuntimeDataDocument>();
        foreach (var dataNode in dataArray)
        {
            var item = dataNode as JsonObject
                ?? throw new InvalidOperationException("Widget runtime data entries must be objects.");
            var widgetKey = GetRequiredString(item, "widgetKey", 200);
            var dataKey = GetRequiredString(item, "dataKey", 128);
            if (!TryGetProperty(item, "jsonData", out var jsonData) || jsonData is null)
            {
                throw new InvalidOperationException("Widget runtime data entries must contain jsonData.");
            }

            var clone = JsonNode.Parse(jsonData.ToJsonString(JsonOptions))
                ?? throw new InvalidOperationException("Widget runtime data jsonData must be valid JSON.");
            result.Add(new PortableWidgetRuntimeDataDocument(widgetKey, dataKey, clone));
        }

        return result;
    }

    private static IReadOnlyList<PortableWidgetRuntimeBinaryData> ReadBinaryData(
        ZipArchive archive,
        JsonObject manifest)
    {
        if (!TryGetProperty(manifest, "binaryData", out var node) || node is not JsonArray binaryArray)
        {
            return [];
        }

        var result = new List<PortableWidgetRuntimeBinaryData>();
        var seenSourceIds = new HashSet<long>();
        foreach (var binaryNode in binaryArray)
        {
            var item = binaryNode as JsonObject
                ?? throw new InvalidOperationException("Widget runtime binary data entries must be objects.");
            var sourceBinaryDataId = GetRequiredLong(item, "sourceBinaryDataId");
            if (sourceBinaryDataId <= 0 || !seenSourceIds.Add(sourceBinaryDataId))
            {
                throw new InvalidOperationException("Widget runtime binary data sourceBinaryDataId values must be positive and unique.");
            }

            var ownerRef = GetRequiredString(item, "ownerRef", 128);
            var fileName = Path.GetFileName(GetRequiredString(item, "fileName", 260).Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new InvalidOperationException("Widget runtime binary data fileName values must not be empty.");
            }

            var contentType = GetRequiredString(item, "contentType", 128);
            var contentLength = GetRequiredLong(item, "contentLength");
            if (contentLength < 0 || contentLength > MaxBinaryBytes)
            {
                throw new InvalidOperationException($"Widget runtime binary data exceeds the limit of {MaxBinaryBytes} bytes.");
            }

            var contentHash = ParseSha256(GetRequiredString(item, "contentHash", 64));
            var isEnabled = GetBool(item, "isEnabled", defaultValue: true);
            var path = NormalizePackagePath(GetRequiredString(item, "path", 512));
            var entry = FindEntry(archive, path)
                ?? throw new InvalidOperationException($"Widget runtime binary data entry '{path}' was not found.");
            if (entry.Length != contentLength)
            {
                throw new InvalidOperationException($"Widget runtime binary data entry '{path}' has an unexpected length.");
            }

            var bytes = ReadEntryBytes(entry, MaxBinaryBytes);
            var actualHash = SHA256.HashData(bytes);
            if (!actualHash.SequenceEqual(contentHash))
            {
                throw new InvalidOperationException($"Widget runtime binary data entry '{path}' has an unexpected SHA-256 hash.");
            }

            result.Add(new PortableWidgetRuntimeBinaryData(
                sourceBinaryDataId,
                ownerRef,
                fileName,
                contentType,
                contentLength,
                contentHash,
                isEnabled,
                bytes));
        }

        return result;
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry, long maxBytes)
    {
        using var stream = entry.Open();
        using var target = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException($"Widget runtime binary data exceeds the limit of {maxBytes} bytes.");
            }

            target.Write(buffer, 0, read);
        }

        return target.ToArray();
    }

    private static JsonObject ReadManifest(ZipArchiveEntry manifestEntry)
    {
        if (manifestEntry.Length > MaxManifestBytes)
        {
            throw new InvalidOperationException(
                $"Widget runtime data package manifest exceeds the limit of {MaxManifestBytes} bytes.");
        }

        using var stream = manifestEntry.Open();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        try
        {
            var text = reader.ReadToEnd();
            return JsonNode.Parse(text) as JsonObject
                ?? throw new InvalidOperationException("Widget runtime data package manifest must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Widget runtime data package manifest must contain valid JSON.", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("Widget runtime data package manifest must be valid UTF-8 text.", ex);
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string entryName)
    {
        var normalized = NormalizePackagePath(entryName);
        return archive.Entries.FirstOrDefault(candidate =>
            !string.IsNullOrEmpty(candidate.Name)
            && string.Equals(NormalizePackagePath(candidate.FullName), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePackagePath(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("Widget runtime data package paths must be relative paths.");
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidOperationException("Widget runtime data package paths must stay inside the package.");
        }

        return string.Join('/', segments);
    }

    private static byte[] ParseSha256(string value)
    {
        try
        {
            var bytes = Convert.FromHexString(value);
            if (bytes.Length == 32)
            {
                return bytes;
            }
        }
        catch (FormatException)
        {
            // Fall through to the normalized error below.
        }

        throw new InvalidOperationException("Widget runtime binary data contentHash values must be 64-character SHA-256 hex strings.");
    }

    private static string GetRequiredString(JsonObject obj, string propertyName, int maxLength)
    {
        if (!TryGetProperty(obj, propertyName, out var node)
            || node is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Widget runtime data packages must contain {propertyName}.");
        }

        var trimmed = text.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new InvalidOperationException($"Widget runtime data package {propertyName} exceeds {maxLength} characters.");
        }

        return trimmed;
    }

    private static long GetRequiredLong(JsonObject obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var node) || node is not JsonValue value)
        {
            throw new InvalidOperationException($"Widget runtime data packages must contain {propertyName}.");
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Widget runtime data package {propertyName} must be a number.");
    }

    private static int GetInt(JsonObject obj, string propertyName, int defaultValue)
    {
        if (!TryGetProperty(obj, propertyName, out var node) || node is not JsonValue value)
        {
            return defaultValue;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static bool GetBool(JsonObject obj, string propertyName, bool defaultValue)
    {
        if (!TryGetProperty(obj, propertyName, out var node) || node is not JsonValue value)
        {
            return defaultValue;
        }

        if (value.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }

        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static bool TryGetProperty(JsonObject obj, string propertyName, out JsonNode? value)
    {
        foreach (var property in obj)
        {
            if (property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
