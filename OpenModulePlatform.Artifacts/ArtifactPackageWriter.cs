using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections;
using OpenModulePlatform.Worker.Abstractions.Models;

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
        string? minModuleDefinitionVersion = null,
        string? minWorkerHostVersion = null)
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
            CreateFromPayloadZip(
                tempPayloadZipPath,
                destinationZipPath,
                configurationFiles,
                minModuleDefinitionVersion,
                minWorkerHostVersion);
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
        string? minModuleDefinitionVersion = null,
        string? minWorkerHostVersion = null)
    {
        if (!File.Exists(payloadZipPath))
        {
            throw new FileNotFoundException("Artifact payload zip was not found.", payloadZipPath);
        }

        var embeddedMinWorkerHostVersion = ReadEmbeddedMinWorkerHostVersion(payloadZipPath);
        var normalizedMinWorkerHostVersion = string.IsNullOrWhiteSpace(minWorkerHostVersion)
            ? embeddedMinWorkerHostVersion
            : minWorkerHostVersion.Trim();
        var compatibilityPayloadPath = Path.Join(Path.GetTempPath(), "OpenModulePlatform", "ArtifactPackages", $"{Guid.NewGuid():N}.payload.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(compatibilityPayloadPath)!);
        var payloadToPackage = compatibilityPayloadPath;

        if (!string.IsNullOrWhiteSpace(minWorkerHostVersion) && embeddedMinWorkerHostVersion is not null
            && !string.Equals(minWorkerHostVersion.Trim(), embeddedMinWorkerHostVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Artifact payload worker-host requirement does not match the requested version.");
        }

        try
        {
            DeterministicArtifactEncoding.NormalizeZip(payloadZipPath, compatibilityPayloadPath);
            if (!string.IsNullOrWhiteSpace(minWorkerHostVersion) && embeddedMinWorkerHostVersion is null)
            {
                AddWorkerPluginCompatibilityManifest(compatibilityPayloadPath, normalizedMinWorkerHostVersion!);
                var normalizedPayload = compatibilityPayloadPath + ".normalized";
                try
                {
                    DeterministicArtifactEncoding.NormalizeZip(compatibilityPayloadPath, normalizedPayload);
                    File.Move(normalizedPayload, compatibilityPayloadPath, overwrite: true);
                }
                finally { TryDelete(normalizedPayload); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationZipPath))!);
            TryDelete(destinationZipPath);

            var normalizedConfigurationFiles = NormalizeConfigurationFiles(configurationFiles);
            var intermediate = destinationZipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var package = ZipFile.Open(intermediate, ZipArchiveMode.Create))
                {
                    WriteFileEntry(package, PayloadEntryName, payloadToPackage);
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
                        workerHost = normalizedMinWorkerHostVersion is null
                            ? null
                            : new
                            {
                                componentKey = WorkerPluginCompatibilityManifest.DefaultWorkerHostComponentKey,
                                minVersion = normalizedMinWorkerHostVersion
                            },
                        configurationFiles = normalizedConfigurationFiles.Select(file => new
                        {
                            file.RelativePath,
                            source = file.SourcePath
                        })
                    };

                    WriteTextEntry(
                        package,
                        ArtifactPackageExtractor.ManifestEntryName,
                        CanonicalJson(manifest));

                    foreach (var file in normalizedConfigurationFiles)
                    {
                        WriteTextEntry(package, file.SourcePath, file.FileContent);
                    }
                }
                DeterministicArtifactEncoding.NormalizeZip(intermediate, destinationZipPath);
            }
            finally { TryDelete(intermediate); }
        }
        finally
        {
            TryDelete(compatibilityPayloadPath);
        }
    }

    private static void AddWorkerPluginCompatibilityManifest(string payloadZipPath, string minWorkerHostVersion)
    {
        using var payload = ZipFile.Open(payloadZipPath, ZipArchiveMode.Update);
        if (payload.Entries.Any(entry => string.Equals(
                entry.FullName.Replace('\\', '/'),
                WorkerPluginCompatibilityManifest.FileName,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Artifact payload already contains reserved compatibility metadata '{WorkerPluginCompatibilityManifest.FileName}'.");
        }

        var manifest = new WorkerPluginCompatibilityManifest
        {
            FormatVersion = 1,
            WorkerHost = new WorkerHostCompatibilityRequirement
            {
                ComponentKey = WorkerPluginCompatibilityManifest.DefaultWorkerHostComponentKey,
                MinVersion = minWorkerHostVersion
            }
        };
        WriteTextEntry(
            payload,
            WorkerPluginCompatibilityManifest.FileName,
            CanonicalJson(manifest));
    }

    private static string? ReadEmbeddedMinWorkerHostVersion(string payloadZipPath)
    {
        using var payload = ZipFile.OpenRead(payloadZipPath);
        var entry = payload.Entries.FirstOrDefault(candidate => string.Equals(
            candidate.FullName.Replace('\\', '/'),
            WorkerPluginCompatibilityManifest.FileName,
            StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<WorkerPluginCompatibilityManifest>(stream)
            ?? throw new InvalidOperationException(
                $"Artifact payload compatibility metadata '{WorkerPluginCompatibilityManifest.FileName}' is empty.");
        if (manifest.FormatVersion != 1 || manifest.WorkerHost is null
            || !string.Equals(
                manifest.WorkerHost.ComponentKey,
                WorkerPluginCompatibilityManifest.DefaultWorkerHostComponentKey,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(manifest.WorkerHost.MinVersion))
        {
            throw new InvalidOperationException(
                $"Artifact payload compatibility metadata '{WorkerPluginCompatibilityManifest.FileName}' is invalid.");
        }

        return manifest.WorkerHost.MinVersion.Trim();
    }

    private static void CreatePayloadZip(string payloadDirectoryPath, string destinationZipPath)
    {
        using var payload = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);

        // R8-P2-12. SearchOption.AllDirectories walks straight through a junction planted in the
        // payload tree, and every file on the other side was then packed into a zip the Portal
        // serves as a download -- an exfiltration path with web delivery attached. Skipping
        // reparse points at enumeration is the only place this can be stopped: by the time a path
        // reaches the file reader it looks like an ordinary file below the payload root.
        var files = Directory.EnumerateFiles(payloadDirectoryPath, "*", OmpReparsePointGuard.RecursiveNoFollow)
            .Where(file => !RuntimeConfigurationFiles.IsRuntimeConfigurationFileName(Path.GetFileName(file)))
            .OrderBy(file => Path.GetRelativePath(payloadDirectoryPath, file), StringComparer.Ordinal);

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(payloadDirectoryPath, file).Replace('\\', '/');
            ValidatePackagePath(relativePath, "payload file");
            WriteFileEntry(payload, relativePath, file);
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

        return FormattableString.Invariant($"configuration/{index + 1:000}-{safeName}");
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
        var entry = package.CreateEntry(entryName, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: false);
        writer.Write(content);
    }

    private static void WriteFileEntry(ZipArchive package, string entryName, string path)
    {
        var entry = package.CreateEntry(entryName, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var input = File.OpenRead(path);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private static string CanonicalJson(object value)
    {
        static object ConvertElement(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => new Hashtable(element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertElement(p.Value))),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertElement).ToArray(),
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number => element.GetInt64(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException("Unsupported generated artifact metadata.")
        };
        return DeterministicArtifactEncoding.Json(ConvertElement(JsonSerializer.SerializeToElement(value, JsonOptions)));
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
