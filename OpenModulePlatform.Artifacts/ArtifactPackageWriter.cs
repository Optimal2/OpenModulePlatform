using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Creates the manifest-based artifact package envelope consumed by Portal,
/// HostAgent import folders, and HostAgent-first bootstrap packages.
/// </summary>
public sealed class ArtifactPackageWriter
{
    public const string PayloadEntryName = "payload/artifact.zip";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public void CreateFromPayloadDirectory(
        string payloadDirectoryPath,
        string destinationZipPath,
        IReadOnlyList<ArtifactPackageConfigurationFile> configurationFiles,
        string? minModuleDefinitionVersion = null)
    {
        if (!Directory.Exists(payloadDirectoryPath))
        {
            throw new DirectoryNotFoundException($"Artifact payload directory was not found: {payloadDirectoryPath}");
        }

        var tempPayloadZipPath = Path.Join(
            Path.GetTempPath(),
            "OpenModulePlatform",
            "ArtifactPackages",
            $"{Guid.NewGuid():N}.payload.zip");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempPayloadZipPath)!);
            CreatePayloadZip(payloadDirectoryPath, tempPayloadZipPath);
            CreateFromPayloadZip(tempPayloadZipPath, destinationZipPath, configurationFiles, minModuleDefinitionVersion);
        }
        finally
        {
            TryDelete(tempPayloadZipPath);
        }
    }

    public void CreateFromPayloadZip(
        string payloadZipPath,
        string destinationZipPath,
        IReadOnlyList<ArtifactPackageConfigurationFile> configurationFiles,
        string? minModuleDefinitionVersion = null)
    {
        if (!File.Exists(payloadZipPath))
        {
            throw new FileNotFoundException("Artifact payload zip was not found.", payloadZipPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationZipPath))!);
        TryDelete(destinationZipPath);

        var normalizedConfigurationFiles = NormalizeConfigurationFiles(configurationFiles);
        using var package = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);

        package.CreateEntryFromFile(payloadZipPath, PayloadEntryName, CompressionLevel.NoCompression);
        var manifest = new
        {
            formatVersion = 1,
            payload = new
            {
                type = "zip",
                path = PayloadEntryName
            },
            moduleDefinition = string.IsNullOrWhiteSpace(minModuleDefinitionVersion)
                ? null
                : new { minVersion = minModuleDefinitionVersion.Trim() },
            configurationFiles = normalizedConfigurationFiles.Select(file => new
            {
                file.RelativePath,
                source = file.SourcePath
            })
        };

        WriteTextEntry(
            package,
            ArtifactPackageExtractor.ManifestEntryName,
            JsonSerializer.Serialize(manifest, JsonOptions));

        foreach (var file in normalizedConfigurationFiles)
        {
            WriteTextEntry(package, file.SourcePath, file.FileContent);
        }
    }

    private static void CreatePayloadZip(string payloadDirectoryPath, string destinationZipPath)
    {
        using var payload = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);
        var files = Directory.EnumerateFiles(payloadDirectoryPath, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(payloadDirectoryPath, file), StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(payloadDirectoryPath, file).Replace('\\', '/');
            ValidatePackagePath(relativePath, "payload file");
            payload.CreateEntryFromFile(file, relativePath, CompressionLevel.Optimal);
        }
    }

    private static IReadOnlyList<NormalizedConfigurationFile> NormalizeConfigurationFiles(
        IReadOnlyList<ArtifactPackageConfigurationFile> configurationFiles)
    {
        if (configurationFiles.Count == 0)
        {
            return [];
        }

        var rows = new List<NormalizedConfigurationFile>(configurationFiles.Count);
        var seenRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in configurationFiles)
        {
            var relativePath = NormalizeDeploymentPath(file.RelativePath);
            if (!seenRelativePaths.Add(relativePath))
            {
                throw new InvalidOperationException(
                    $"Artifact package contains duplicate configuration relative path '{relativePath}'.");
            }

            rows.Add(new NormalizedConfigurationFile(
                relativePath,
                BuildConfigurationSourcePath(relativePath, rows.Count),
                file.FileContent ?? string.Empty));
        }

        return rows;
    }

    private static string BuildConfigurationSourcePath(string relativePath, int index)
    {
        var fileName = relativePath.Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"config-{index + 1}.txt";
        }

        var safeName = new string(fileName.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '.' or '_' or '+' or '-'
                ? ch
                : '-').ToArray());

        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = $"config-{index + 1}.txt";
        }

        return $"configuration/{index + 1:000}-{safeName}";
    }

    private static string NormalizeDeploymentPath(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        ValidatePackagePath(normalized, "configuration relative path");
        return normalized;
    }

    private static void ValidatePackagePath(string value, string purpose)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains(':', StringComparison.Ordinal)
            || value.IndexOf('\0') >= 0
            || value.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invalid {purpose} path.");
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidOperationException($"{purpose} path must stay inside the package.");
        }
    }

    private static void WriteTextEntry(ZipArchive package, string entryName, string content)
    {
        var entry = package.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: false);
        writer.Write(content);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup for temporary package payloads.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup for temporary package payloads.
        }
    }

    private sealed record NormalizedConfigurationFile(
        string RelativePath,
        string SourcePath,
        string FileContent);
}
