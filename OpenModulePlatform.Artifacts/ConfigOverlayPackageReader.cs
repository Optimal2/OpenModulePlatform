using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenModulePlatform.Artifacts;

public enum PortableConfigObjectKind
{
    Unknown,
    ModuleDefinition,
    HostConfiguration,
    ConfigOverlay,
    HostConfigurationPackage,
    ConfigOverlayPackage
}

public sealed record PortableHostConfigurationDocument(
    string HostKey,
    string ConfigurationVersion,
    int FormatVersion,
    string ConfigurationJson,
    string ConfigurationSha256,
    string? DisplayName,
    string? Description,
    string? SourceName);

public sealed record PortableConfigOverlayDocument(
    string OverlayKey,
    string OverlayVersion,
    string HostKey,
    int FormatVersion,
    string OverlayJson,
    string OverlaySha256,
    string? ModuleKey,
    string? ModuleDefinitionVersion,
    string? AppKey,
    string? PackageType,
    string? TargetName,
    string? ArtifactVersion,
    string? SourceName,
    IReadOnlyList<PortableConfigOverlayConfigurationFile> ConfigurationFiles,
    int SqlScriptCount = 0);

public sealed record PortableConfigOverlayConfigurationFile(
    string RelativePath,
    string FileContent);

/// <summary>
/// Reads the host-specific configuration objects that are intentionally kept
/// outside global module definitions and global artifact binaries.
/// </summary>
public sealed class ConfigOverlayPackageReader
{
    public const string HostConfigurationManifestEntryName = "omp-host-config.json";
    public const string ConfigOverlayManifestEntryName = "omp-config-overlay.json";
    public const string ConfigOverlaySqlScriptsWarning =
        "This config overlay contains sqlScripts. OpenModulePlatform does not execute SQL from config overlays; apply it manually if it is required.";

    private const int MaxJsonBytes = 1024 * 1024 * 5;
    private const int MaxExternalFileBytes = 1024 * 1024 * 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static PortableConfigObjectKind DetectFileKind(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > MaxJsonBytes)
            {
                return PortableConfigObjectKind.Unknown;
            }

            return DetectJsonKind(File.ReadAllText(path, Encoding.UTF8));
        }

        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return PortableConfigObjectKind.Unknown;
        }

        using var archive = ZipFile.OpenRead(path);
        if (FindEntry(archive, HostConfigurationManifestEntryName) is not null)
        {
            return PortableConfigObjectKind.HostConfigurationPackage;
        }

        if (FindEntry(archive, ConfigOverlayManifestEntryName) is not null)
        {
            return PortableConfigObjectKind.ConfigOverlayPackage;
        }

        return PortableConfigObjectKind.Unknown;
    }

    public static PortableConfigObjectKind DetectJsonKind(string jsonText)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(jsonText) as JsonObject;
        }
        catch (JsonException)
        {
            return PortableConfigObjectKind.Unknown;
        }

        if (root is null)
        {
            return PortableConfigObjectKind.Unknown;
        }

        if (!string.IsNullOrWhiteSpace(GetString(root, "moduleKey"))
            && !string.IsNullOrWhiteSpace(GetString(root, "definitionVersion")))
        {
            return PortableConfigObjectKind.ModuleDefinition;
        }

        if (!string.IsNullOrWhiteSpace(GetString(root, "overlayKey"))
            && !string.IsNullOrWhiteSpace(GetString(root, "overlayVersion"))
            && !string.IsNullOrWhiteSpace(GetString(root, "hostKey")))
        {
            return PortableConfigObjectKind.ConfigOverlay;
        }

        if (!string.IsNullOrWhiteSpace(GetString(root, "hostKey"))
            && !string.IsNullOrWhiteSpace(GetString(root, "configurationVersion")))
        {
            return PortableConfigObjectKind.HostConfiguration;
        }

        return PortableConfigObjectKind.Unknown;
    }

    public async Task<PortableHostConfigurationDocument> ReadHostConfigurationAsync(
        string path,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = FindEntry(archive, HostConfigurationManifestEntryName)
                ?? throw new InvalidOperationException("Host configuration package must contain omp-host-config.json.");
            var json = ReadUtf8Text(entry);
            return ReadHostConfiguration(json, sourceName);
        }

        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Host configuration objects must be .json files or .zip packages.");
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaxJsonBytes)
        {
            throw new InvalidOperationException($"Host configuration JSON exceeds the limit of {MaxJsonBytes} bytes.");
        }

        var jsonText = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        return ReadHostConfiguration(jsonText, sourceName);
    }

    public async Task<PortableConfigOverlayDocument> ReadConfigOverlayAsync(
        string path,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = FindEntry(archive, ConfigOverlayManifestEntryName)
                ?? throw new InvalidOperationException("Config overlay package must contain omp-config-overlay.json.");
            var json = ReadUtf8Text(entry);
            return ReadConfigOverlay(json, sourceName, archive, null);
        }

        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Config overlay objects must be .json files or .zip packages.");
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaxJsonBytes)
        {
            throw new InvalidOperationException($"Config overlay JSON exceeds the limit of {MaxJsonBytes} bytes.");
        }

        var jsonText = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        return ReadConfigOverlay(jsonText, sourceName, null, Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    private static PortableHostConfigurationDocument ReadHostConfiguration(string jsonText, string sourceName)
    {
        var root = ParseJsonObject(jsonText, "Host configuration JSON");
        var hostKey = RequireString(root, "hostKey", "Host configuration");
        var configurationVersion = RequireString(root, "configurationVersion", "Host configuration");
        var normalizedJson = root.ToJsonString(JsonOptions);

        return new PortableHostConfigurationDocument(
            hostKey,
            configurationVersion,
            GetInt(root, "formatVersion", 1),
            normalizedJson,
            ComputeSha256(normalizedJson),
            NullIfWhiteSpace(GetString(root, "displayName")),
            NullIfWhiteSpace(GetString(root, "description")),
            Truncate(sourceName, 400));
    }

    private static PortableConfigOverlayDocument ReadConfigOverlay(
        string jsonText,
        string sourceName,
        ZipArchive? archive,
        string? externalRoot)
    {
        var root = ParseJsonObject(jsonText, "Config overlay JSON");
        var overlayKey = RequireString(root, "overlayKey", "Config overlay");
        var overlayVersion = RequireString(root, "overlayVersion", "Config overlay");
        var hostKey = RequireString(root, "hostKey", "Config overlay");
        var files = NormalizeConfigurationFiles(root, archive, externalRoot);
        var sqlScriptCount = NormalizeSqlScripts(root, archive, externalRoot);

        var normalizedJson = root.ToJsonString(JsonOptions);
        return new PortableConfigOverlayDocument(
            overlayKey,
            overlayVersion,
            hostKey,
            GetInt(root, "formatVersion", 1),
            normalizedJson,
            ComputeSha256(normalizedJson),
            NullIfWhiteSpace(GetString(root, "moduleKey")),
            NullIfWhiteSpace(GetString(root, "moduleDefinitionVersion")),
            NullIfWhiteSpace(GetString(root, "appKey")),
            NullIfWhiteSpace(GetString(root, "packageType")),
            NullIfWhiteSpace(GetString(root, "targetName")),
            NullIfWhiteSpace(GetString(root, "artifactVersion")),
            Truncate(sourceName, 400),
            files,
            sqlScriptCount);
    }

    private static IReadOnlyList<PortableConfigOverlayConfigurationFile> NormalizeConfigurationFiles(
        JsonObject root,
        ZipArchive? archive,
        string? externalRoot)
    {
        if (root["configurationFiles"] is not JsonArray items || items.Count == 0)
        {
            return [];
        }

        var files = new List<PortableConfigOverlayConfigurationFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in items)
        {
            var item = node as JsonObject
                ?? throw new InvalidOperationException("Each config overlay configurationFiles item must be an object.");
            var relativePath = NormalizeRelativeDeploymentPath(
                RequireString(item, "relativePath", "Config overlay configuration file"));
            if (!seen.Add(relativePath))
            {
                throw new InvalidOperationException(
                    $"Config overlay contains duplicate configuration file relative path '{relativePath}'.");
            }

            var content = GetStringPreserveWhitespace(item, "fileContent")
                ?? GetStringPreserveWhitespace(item, "content");
            if (content is null)
            {
                var source = NormalizePackagePath(
                    GetString(item, "source")
                        ?? GetString(item, "path")
                        ?? throw new InvalidOperationException(
                            "Config overlay configurationFiles items must contain fileContent or source."));
                content = ReadExternalText(source, archive, externalRoot, "configuration file");
            }

            item["relativePath"] = relativePath;
            item["fileContent"] = content;
            item.Remove("content");
            item.Remove("source");
            item.Remove("path");
            files.Add(new PortableConfigOverlayConfigurationFile(relativePath, content));
        }

        return files;
    }

    private static int NormalizeSqlScripts(JsonObject root, ZipArchive? archive, string? externalRoot)
    {
        if (root["sqlScripts"] is not JsonArray items)
        {
            return 0;
        }

        foreach (var node in items)
        {
            if (node is not JsonObject item)
            {
                throw new InvalidOperationException("Each config overlay sqlScripts item must be an object.");
            }

            if (!string.IsNullOrWhiteSpace(GetString(item, "inlineSql"))
                || !string.IsNullOrWhiteSpace(GetString(item, "content")))
            {
                continue;
            }

            var source = NormalizePackagePath(
                GetString(item, "source")
                    ?? GetString(item, "path")
                    ?? throw new InvalidOperationException(
                        "Config overlay sqlScripts items must contain inlineSql or source."));
            var sql = ReadExternalText(source, archive, externalRoot, "SQL script");
            item["inlineSql"] = sql;
            item["sha256"] = ComputeSha256(sql);
            item.Remove("content");
            item.Remove("contentEncoding");
        }

        return items.Count;
    }

    private static string ReadExternalText(
        string source,
        ZipArchive? archive,
        string? externalRoot,
        string objectName)
    {
        if (archive is not null)
        {
            var entry = FindEntry(archive, source)
                ?? throw new FileNotFoundException(
                    $"Config overlay package references missing {objectName} '{source}'.",
                    source);
            if (entry.Length > MaxExternalFileBytes)
            {
                throw new InvalidOperationException(
                    $"Config overlay {objectName} '{source}' exceeds the limit of {MaxExternalFileBytes} bytes.");
            }

            return ReadUtf8Text(entry);
        }

        if (string.IsNullOrWhiteSpace(externalRoot))
        {
            throw new InvalidOperationException($"Config overlay {objectName} '{source}' cannot be resolved.");
        }

        var fullRoot = Path.GetFullPath(externalRoot);
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, source.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(fullPath, fullRoot))
        {
            throw new InvalidOperationException($"Config overlay {objectName} path escapes the package root.");
        }

        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException(
                $"Config overlay package references missing {objectName} '{source}'.",
                source);
        }

        if (fileInfo.Length > MaxExternalFileBytes)
        {
            throw new InvalidOperationException(
                $"Config overlay {objectName} '{source}' exceeds the limit of {MaxExternalFileBytes} bytes.");
        }

        return File.ReadAllText(fullPath, Encoding.UTF8);
    }

    private static JsonObject ParseJsonObject(string jsonText, string objectName)
    {
        try
        {
            return JsonNode.Parse(jsonText) as JsonObject
                ?? throw new InvalidOperationException($"{objectName} must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{objectName} must contain valid JSON.", ex);
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string entryName)
    {
        var normalized = NormalizePackagePath(entryName);
        return archive.Entries.FirstOrDefault(candidate =>
            !string.IsNullOrEmpty(candidate.Name)
            && string.Equals(NormalizePackagePath(candidate.FullName), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadUtf8Text(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxExternalFileBytes)
        {
            throw new InvalidOperationException(
                $"Config overlay package entry '{entry.FullName}' exceeds the limit of {MaxExternalFileBytes} bytes.");
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        try
        {
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                $"Config overlay package entry '{entry.FullName}' must be valid UTF-8 text.",
                ex);
        }
    }

    private static string NormalizePackagePath(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("Config overlay package paths must be relative paths.");
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidOperationException("Config overlay package paths must stay inside the package.");
        }

        return string.Join('/', segments);
    }

    private static string NormalizeRelativeDeploymentPath(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("Config overlay relative paths must stay inside the deployed artifact root.");
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidOperationException("Config overlay relative paths must not contain parent directory segments.");
        }

        return string.Join('/', segments);
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullRoot = Path.GetFullPath(root);
        var normalizedRoot = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return Path.GetFullPath(path).StartsWith(normalizedRoot, comparison);
    }

    private static string RequireString(JsonObject obj, string propertyName, string objectName)
        => NullIfWhiteSpace(GetString(obj, propertyName))
            ?? throw new InvalidOperationException($"{objectName} must contain {propertyName}.");

    private static string? GetString(JsonObject obj, string propertyName)
        => obj
            .Where(property => property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Value)
            .OfType<JsonValue>()
            .Select(static value => value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : null)
            .FirstOrDefault(static text => text is not null);

    private static string? GetStringPreserveWhitespace(JsonObject obj, string propertyName)
        => obj
            .Where(property => property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Value)
            .OfType<JsonValue>()
            .Select(static value => value.TryGetValue<string>(out var text) ? text : null)
            .FirstOrDefault(static text => text is not null);

    private static int GetInt(JsonObject obj, string propertyName, int defaultValue)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is not JsonValue value)
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

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string ComputeSha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
