using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenModulePlatform.Artifacts;

public sealed class ArtifactPackageExtractor
{
    public const string ManifestEntryName = "omp-artifact-package.json";

    private readonly Action<string>? _validatePayloadEntry;

    public ArtifactPackageExtractor(Action<string>? validatePayloadEntry = null)
    {
        _validatePayloadEntry = validatePayloadEntry;
    }

    public ArtifactPackageExtractionResult Extract(string zipPath, string stagingPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var manifestEntry = archive.Entries.FirstOrDefault(
            entry => string.Equals(
                NormalizeZipEntryName(entry.FullName),
                ManifestEntryName,
                StringComparison.OrdinalIgnoreCase));

        return manifestEntry is null
            ? ExtractLegacyArtifact(archive, stagingPath)
            : ExtractManifestEnvelope(archive, manifestEntry, stagingPath);
    }

    private ArtifactPackageExtractionResult ExtractLegacyArtifact(
        ZipArchive archive,
        string stagingPath)
    {
        var fileCount = ExtractArchiveEntries(
            archive,
            stagingPath,
            string.Empty,
            _validatePayloadEntry);

        if (fileCount == 0)
        {
            throw new InvalidOperationException("The artifact zip must contain at least one file.");
        }

        return new ArtifactPackageExtractionResult(
            stagingPath,
            [],
            UsesManifestEnvelope: false);
    }

    private ArtifactPackageExtractionResult ExtractManifestEnvelope(
        ZipArchive archive,
        ZipArchiveEntry manifestEntry,
        string stagingPath)
    {
        var manifest = ReadJsonObject(manifestEntry);
        var formatVersion = manifest["formatVersion"]?.GetValue<int>() ?? 0;
        if (formatVersion != 1)
        {
            throw new InvalidOperationException("Artifact package manifest formatVersion must be 1.");
        }

        var payload = manifest["payload"]?.AsObject()
            ?? throw new InvalidOperationException("Artifact package manifest must contain a payload object.");
        var payloadPath = NormalizeZipEntryName(
            payload["path"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Artifact package payload.path is required."));
        var payloadType = payload["type"]?.GetValue<string>()?.Trim();

        var artifactContentPath = Path.Join(stagingPath, "artifact-content");
        var fileCount = IsZipPayload(payloadPath, payloadType)
            ? ExtractNestedPayloadZip(archive, payloadPath, artifactContentPath, stagingPath)
            : ExtractArchiveEntries(archive, artifactContentPath, EnsureDirectoryPrefix(payloadPath), _validatePayloadEntry);

        if (fileCount == 0)
        {
            throw new InvalidOperationException("Artifact package payload must contain at least one file.");
        }

        return new ArtifactPackageExtractionResult(
            artifactContentPath,
            ReadConfigurationFiles(archive, manifest),
            UsesManifestEnvelope: true);
    }

    private int ExtractNestedPayloadZip(
        ZipArchive archive,
        string payloadPath,
        string artifactContentPath,
        string stagingPath)
    {
        var payloadEntry = FindRequiredFileEntry(archive, payloadPath);
        var nestedZipPath = Path.Join(stagingPath, "artifact-payload.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedZipPath)!);

        using (var source = payloadEntry.Open())
        using (var target = File.Create(nestedZipPath))
        {
            source.CopyTo(target);
        }

        using var payloadArchive = ZipFile.OpenRead(nestedZipPath);
        return ExtractArchiveEntries(
            payloadArchive,
            artifactContentPath,
            string.Empty,
            _validatePayloadEntry);
    }

    private static int ExtractArchiveEntries(
        ZipArchive archive,
        string targetRoot,
        string requiredPrefix,
        Action<string>? validateEntry)
    {
        var fileCount = 0;
        Directory.CreateDirectory(targetRoot);

        foreach (var entry in archive.Entries)
        {
            var entryName = NormalizeZipEntryName(entry.FullName);
            if (!string.IsNullOrEmpty(requiredPrefix)
                && !entryName.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativeName = string.IsNullOrEmpty(requiredPrefix)
                ? entryName
                : entryName[requiredPrefix.Length..];
            if (relativeName.Length == 0)
            {
                continue;
            }

            var entryPath = ResolveZipEntryPath(targetRoot, relativeName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(entryPath);
                continue;
            }

            validateEntry?.Invoke(relativeName);
            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            entry.ExtractToFile(entryPath, overwrite: false);
            fileCount++;
        }

        return fileCount;
    }

    private static IReadOnlyList<ArtifactPackageConfigurationFile> ReadConfigurationFiles(
        ZipArchive archive,
        JsonObject manifest)
    {
        var nodes = manifest["configurationFiles"]?.AsArray();
        if (nodes is null || nodes.Count == 0)
        {
            return [];
        }

        var files = new List<ArtifactPackageConfigurationFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var item = node?.AsObject()
                ?? throw new InvalidOperationException("Each artifact package configurationFiles item must be an object.");
            var relativePath = NormalizeRelativeDeploymentPath(
                item["relativePath"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Artifact package configurationFiles[].relativePath is required."));
            var sourcePath = NormalizeZipEntryName(
                item["source"]?.GetValue<string>()
                    ?? item["path"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Artifact package configurationFiles[].source is required."));

            if (!seenPaths.Add(relativePath))
            {
                throw new InvalidOperationException(
                    $"Artifact package contains duplicate configuration relative path '{relativePath}'.");
            }

            var sourceEntry = FindRequiredFileEntry(archive, sourcePath);
            files.Add(new ArtifactPackageConfigurationFile(
                relativePath,
                ReadUtf8Text(sourceEntry)));
        }

        return files;
    }

    private static JsonObject ReadJsonObject(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        try
        {
            var text = reader.ReadToEnd();
            var node = JsonNode.Parse(text);
            return node as JsonObject
                ?? throw new InvalidOperationException("Artifact package manifest must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Artifact package manifest must contain valid JSON.", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("Artifact package manifest must be valid UTF-8 text.", ex);
        }
    }

    private static string ReadUtf8Text(ZipArchiveEntry entry)
    {
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
                $"Artifact package configuration file '{entry.FullName}' must be valid UTF-8 text.",
                ex);
        }
    }

    private static ZipArchiveEntry FindRequiredFileEntry(ZipArchive archive, string entryPath)
    {
        var entry = archive.Entries.FirstOrDefault(candidate =>
            !string.IsNullOrEmpty(candidate.Name)
            && string.Equals(NormalizeZipEntryName(candidate.FullName), entryPath, StringComparison.OrdinalIgnoreCase));

        return entry ?? throw new InvalidOperationException(
            $"Artifact package references missing file '{entryPath}'.");
    }

    private static bool IsZipPayload(string payloadPath, string? payloadType)
    {
        if (!string.IsNullOrWhiteSpace(payloadType))
        {
            if (payloadType.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (payloadType.Equals("directory", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw new InvalidOperationException("Artifact package payload.type must be 'directory' or 'zip'.");
        }

        return payloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureDirectoryPrefix(string path)
        => path.EndsWith("/", StringComparison.Ordinal) ? path : path + "/";

    private static string NormalizeZipEntryName(string fullName)
    {
        var normalized = fullName.Replace('\\', '/').Trim();
        if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The artifact package contains an invalid entry path.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidOperationException("The artifact package contains a path that escapes the package root.");
        }

        if (normalized.Contains(':', StringComparison.Ordinal) || normalized.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("The artifact package contains a rooted or invalid entry path.");
        }

        return string.Join('/', segments);
    }

    private static string NormalizeRelativeDeploymentPath(string relativePath)
    {
        var normalized = relativePath.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("Configuration file relative paths must stay inside the deployed artifact root.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidOperationException("Configuration file relative paths must not contain rooted paths or parent directory segments.");
        }

        return string.Join('/', segments);
    }

    private static string ResolveZipEntryPath(string rootPath, string relativeEntryPath)
    {
        if (Path.IsPathRooted(relativeEntryPath))
        {
            throw new InvalidOperationException("Expected a relative artifact package path.");
        }

        var fullRoot = Path.GetFullPath(rootPath);
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, relativeEntryPath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedRoot = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, comparison))
        {
            throw new InvalidOperationException("The artifact package path escapes the extraction root.");
        }

        return fullPath;
    }
}
