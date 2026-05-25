using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenModulePlatform.Artifacts;

public enum UniversalModulePackageItemKind
{
    Unknown,
    ModuleDefinition,
    ArtifactPackage,
    HostConfiguration,
    ConfigOverlay,
    DashboardWidget
}

public sealed record PortableUniversalModulePackage(
    int FormatVersion,
    string? PackageKey,
    string? PackageVersion,
    string? DisplayName,
    string? Description,
    string? TargetHostProfile,
    string SourceName,
    string ExtractionRoot,
    IReadOnlyList<PortableUniversalModulePackageItem> Items);

public sealed record PortableUniversalModulePackageItem(
    UniversalModulePackageItemKind Kind,
    string Path,
    string ExtractedPath,
    string SourceName);

/// <summary>
/// Reads universal OMP module packages: a neutral container for portable OMP
/// objects such as module definitions, artifact packages, host configurations,
/// config overlays, and dashboard widget JSON files.
/// </summary>
public sealed class UniversalModulePackageReader
{
    public const string ManifestEntryName = "omp-universal-package.json";

    private const int MaxManifestBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static bool IsUniversalPackage(string path)
    {
        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var archive = ZipFile.OpenRead(path);
        return FindEntry(archive, ManifestEntryName) is not null;
    }

    public PortableUniversalModulePackage ExtractToDirectory(
        string zipPath,
        string extractionRoot)
    {
        Directory.CreateDirectory(extractionRoot);
        using var archive = ZipFile.OpenRead(zipPath);
        var manifestEntry = FindEntry(archive, ManifestEntryName)
            ?? throw new InvalidOperationException(
                $"Universal module packages must contain {ManifestEntryName}.");

        ExtractArchiveEntries(archive, extractionRoot);
        var manifest = ReadManifest(manifestEntry);
        var formatVersion = GetInt(manifest, "formatVersion", 1);
        if (formatVersion != 1)
        {
            throw new InvalidOperationException("Universal module package formatVersion must be 1.");
        }

        var items = ReadManifestItems(manifest, extractionRoot);
        if (items.Count == 0)
        {
            items = DiscoverItems(extractionRoot);
        }

        return new PortableUniversalModulePackage(
            formatVersion,
            NullIfWhiteSpace(GetString(manifest, "packageKey")),
            NullIfWhiteSpace(GetString(manifest, "packageVersion")),
            NullIfWhiteSpace(GetString(manifest, "displayName")),
            NullIfWhiteSpace(GetString(manifest, "description")),
            NullIfWhiteSpace(GetString(manifest, "targetHostProfile")),
            Path.GetFileName(zipPath),
            extractionRoot,
            items);
    }

    private static IReadOnlyList<PortableUniversalModulePackageItem> ReadManifestItems(
        JsonObject manifest,
        string extractionRoot)
    {
        if (manifest["items"] is not JsonArray items || items.Count == 0)
        {
            return [];
        }

        var result = new List<PortableUniversalModulePackageItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in items)
        {
            var item = node as JsonObject
                ?? throw new InvalidOperationException("Universal package items must be objects.");
            var path = NormalizePackagePath(
                GetString(item, "path")
                    ?? throw new InvalidOperationException("Universal package items must contain path."));
            if (!seenPaths.Add(path))
            {
                throw new InvalidOperationException(
                    $"Universal package contains duplicate item path '{path}'.");
            }

            var kind = ParseItemKind(
                GetString(item, "kind")
                    ?? GetString(item, "type")
                    ?? InferKindFromPath(path).ToString());
            result.Add(CreateItem(kind, path, extractionRoot));
        }

        return result;
    }

    private static IReadOnlyList<PortableUniversalModulePackageItem> DiscoverItems(string extractionRoot)
    {
        var items = new List<PortableUniversalModulePackageItem>();
        AddDiscoveredItems(items, extractionRoot, "module-definitions", "*.json", UniversalModulePackageItemKind.ModuleDefinition);
        AddDiscoveredItems(items, extractionRoot, "artifacts", "*.zip", UniversalModulePackageItemKind.ArtifactPackage);
        AddDiscoveredItems(items, extractionRoot, "host-configs", "*.json", UniversalModulePackageItemKind.HostConfiguration);
        AddDiscoveredItems(items, extractionRoot, "host-configs", "*.zip", UniversalModulePackageItemKind.HostConfiguration);
        AddDiscoveredItems(items, extractionRoot, "config-overlays", "*.json", UniversalModulePackageItemKind.ConfigOverlay);
        AddDiscoveredItems(items, extractionRoot, "config-overlays", "*.zip", UniversalModulePackageItemKind.ConfigOverlay);
        AddDiscoveredItems(items, extractionRoot, "widgets", "*.json", UniversalModulePackageItemKind.DashboardWidget);
        return items
            .OrderBy(static item => item.Kind)
            .ThenBy(static item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddDiscoveredItems(
        List<PortableUniversalModulePackageItem> items,
        string extractionRoot,
        string relativeFolder,
        string searchPattern,
        UniversalModulePackageItemKind kind)
    {
        var folder = Path.Join(extractionRoot, relativeFolder);
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, searchPattern, SearchOption.AllDirectories))
        {
            var relativePath = NormalizePackagePath(Path.GetRelativePath(extractionRoot, file));
            if (relativePath.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(CreateItem(kind, relativePath, extractionRoot));
        }
    }

    private static PortableUniversalModulePackageItem CreateItem(
        UniversalModulePackageItemKind kind,
        string path,
        string extractionRoot)
    {
        var extractedPath = ResolveUnderRoot(extractionRoot, path);
        return new PortableUniversalModulePackageItem(
            kind,
            path,
            extractedPath,
            Path.GetFileName(path));
    }

    private static UniversalModulePackageItemKind InferKindFromPath(string path)
    {
        var firstSegment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return firstSegment.Trim().ToLowerInvariant() switch
        {
            "module-definition" or "module-definitions" => UniversalModulePackageItemKind.ModuleDefinition,
            "artifact" or "artifacts" => UniversalModulePackageItemKind.ArtifactPackage,
            "host-config" or "host-configs" or "host-configuration" or "host-configurations" => UniversalModulePackageItemKind.HostConfiguration,
            "config-overlay" or "config-overlays" => UniversalModulePackageItemKind.ConfigOverlay,
            "widget" or "widgets" or "dashboard-widgets" => UniversalModulePackageItemKind.DashboardWidget,
            _ => UniversalModulePackageItemKind.Unknown
        };
    }

    private static UniversalModulePackageItemKind ParseItemKind(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "module-definition" or "moduledefinition" or "module" => UniversalModulePackageItemKind.ModuleDefinition,
            "artifact" or "artifact-package" or "artifactpackage" => UniversalModulePackageItemKind.ArtifactPackage,
            "host-config" or "host-configuration" or "hostconfiguration" => UniversalModulePackageItemKind.HostConfiguration,
            "config-overlay" or "configoverlay" or "overlay" => UniversalModulePackageItemKind.ConfigOverlay,
            "widget" or "dashboard-widget" or "dashboardwidget" => UniversalModulePackageItemKind.DashboardWidget,
            _ => UniversalModulePackageItemKind.Unknown
        };

    private static JsonObject ReadManifest(ZipArchiveEntry manifestEntry)
    {
        if (manifestEntry.Length > MaxManifestBytes)
        {
            throw new InvalidOperationException(
                $"Universal module package manifest exceeds the limit of {MaxManifestBytes} bytes.");
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
                ?? throw new InvalidOperationException("Universal module package manifest must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Universal module package manifest must contain valid JSON.", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("Universal module package manifest must be valid UTF-8 text.", ex);
        }
    }

    private static void ExtractArchiveEntries(ZipArchive archive, string extractionRoot)
    {
        foreach (var entry in archive.Entries)
        {
            var entryName = NormalizePackagePath(entry.FullName);
            var destinationPath = ResolveUnderRoot(extractionRoot, entryName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: false);
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
            throw new InvalidOperationException("Universal module package paths must be relative paths.");
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidOperationException("Universal module package paths must stay inside the package.");
        }

        return string.Join('/', segments);
    }

    private static string ResolveUnderRoot(string rootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Expected a relative universal module package path.");
        }

        var fullRoot = Path.GetFullPath(rootPath);
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, comparison))
        {
            throw new InvalidOperationException("The universal module package path escapes the extraction root.");
        }

        return fullPath;
    }

    private static string? GetString(JsonObject obj, string propertyName)
        => obj
            .Where(property => property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Value)
            .OfType<JsonValue>()
            .Select(static value => value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : null)
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
}
