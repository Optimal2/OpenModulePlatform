using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class ArtifactZipImportService
{
    private const int HashBufferSize = 1024 * 128;

    private static readonly Regex MetadataTokenPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._+-]*$",
        RegexOptions.Compiled);

    private static readonly string[] RuntimeConfigurationFileNames =
    [
        "odv.site.config.js"
    ];

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ILogger<ArtifactZipImportService> _logger;

    public ArtifactZipImportService(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        ILogger<ArtifactZipImportService> logger)
    {
        _settings = settings;
        _repository = repository;
        _logger = logger;
    }

    public async Task ImportPendingAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        var importSettings = settings.ArtifactZipImport;
        if (!importSettings.IsEnabled)
        {
            return;
        }

        var importPath = Path.GetFullPath(importSettings.ImportPath.Trim());
        var processedPath = Path.GetFullPath(importSettings.ResolveProcessedPath());
        var failedPath = Path.GetFullPath(importSettings.ResolveFailedPath());

        Directory.CreateDirectory(importPath);
        Directory.CreateDirectory(processedPath);
        Directory.CreateDirectory(failedPath);

        var zipPaths = Directory.EnumerateFiles(importPath, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(importSettings.MaxFilesPerCycle)
            .ToList();

        foreach (var zipPath in zipPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ImportOneAsync(settings, importSettings, zipPath, processedPath, failedPath, cancellationToken);
        }
    }

    private async Task ImportOneAsync(
        HostAgentSettings settings,
        HostAgentArtifactZipImportSettings importSettings,
        string zipPath,
        string processedPath,
        string failedPath,
        CancellationToken cancellationToken)
    {
        var storeRoot = Path.GetFullPath(settings.CentralArtifactRoot.Trim());
        var tempRoot = Path.Combine(storeRoot, ".hostagent-import-staging");
        Directory.CreateDirectory(tempRoot);

        var tempZipPath = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.zip");
        var stagingPath = Path.Combine(tempRoot, $"artifact-{Guid.NewGuid():N}");
        string? finalPath = null;
        var movedToFinal = false;
        var artifactRegistered = false;
        var adoptedExistingContent = false;

        try
        {
            if (!await TryCopyReadyZipAsync(zipPath, tempZipPath, cancellationToken))
            {
                return;
            }

            var metadata = TryParseFilenameMetadata(Path.GetFileName(zipPath))
                ?? throw new InvalidOperationException(
                    "Artifact zip filename must match moduleKey__appKey__packageType__targetName__version.zip.");

            var app = await _repository.ResolveArtifactZipImportAppAsync(
                metadata.ModuleKey,
                metadata.AppKey,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"No enabled app was found for module '{metadata.ModuleKey}' and app '{metadata.AppKey}'.");

            var compatibility = await _repository.RequireCompatibleArtifactSlotAsync(
                app.AppId,
                metadata.Version,
                metadata.PackageType,
                metadata.TargetName,
                cancellationToken);

            var relativePath = BuildRelativePath(
                compatibility,
                metadata.TargetName,
                metadata.PackageType,
                metadata.Version);

            var existingIdentity = await _repository.FindImportedArtifactByIdentityAsync(
                app.AppId,
                metadata.Version,
                metadata.PackageType,
                metadata.TargetName,
                cancellationToken);
            if (existingIdentity is not null)
            {
                throw new InvalidOperationException(
                    "An artifact for this app, package type, target, and version already exists: " +
                    $"{existingIdentity.AppKey} {existingIdentity.Version} ({existingIdentity.PackageType}). " +
                    "Use a new version number for changed artifact content.");
            }

            finalPath = ResolveUnderRoot(storeRoot, relativePath);

            var package = new ArtifactPackageExtractor(ValidateArtifactEntryIsNotRuntimeConfiguration)
                .Extract(tempZipPath, stagingPath);
            var contentHash = await ComputeDirectorySha256Async(package.ArtifactContentPath, cancellationToken);
            if (Directory.Exists(finalPath) || File.Exists(finalPath))
            {
                if (File.Exists(finalPath))
                {
                    throw new InvalidOperationException(
                        $"The target artifact path already exists as a file below the artifact store: {relativePath}.");
                }

                var existingContentHash = await ComputeDirectorySha256Async(finalPath, cancellationToken);
                if (!string.Equals(existingContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The target artifact path already exists with different content below the artifact store: {relativePath}.");
                }

                adoptedExistingContent = true;
            }

            if (!adoptedExistingContent)
            {
                var duplicate = await _repository.FindImportedArtifactBySha256Async(contentHash, cancellationToken);
                if (duplicate is not null)
                {
                    throw new InvalidOperationException(
                        $"An artifact with identical extracted content already exists: {duplicate.AppKey} {duplicate.Version} ({duplicate.PackageType}).");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                Directory.Move(package.ArtifactContentPath, finalPath);
                movedToFinal = true;
            }

            var artifactId = await _repository.RegisterImportedArtifactAsync(
                app.AppId,
                metadata.Version,
                metadata.PackageType,
                metadata.TargetName,
                relativePath,
                contentHash,
                cancellationToken);

            artifactRegistered = true;
            var copiedConfigurationFiles = 0;
            if (package.ConfigurationFiles.Count > 0)
            {
                copiedConfigurationFiles = await _repository.ReplaceArtifactConfigurationFilesAsync(
                    artifactId,
                    package.ConfigurationFiles,
                    cancellationToken);
            }
            else if (importSettings.CopyConfigurationFilesFromPreviousVersion)
            {
                copiedConfigurationFiles = await _repository.CopyConfigurationFilesFromLatestPreviousArtifactAsync(
                    artifactId,
                    app.AppId,
                    metadata.PackageType,
                    metadata.TargetName,
                    cancellationToken);
            }

            var application = await _repository.ApplyImportedArtifactToMatchingApplicationsAsync(
                artifactId,
                cancellationToken);
            var hostAgentDesiredRows = 0;
            if (string.Equals(metadata.PackageType, "host-agent", StringComparison.OrdinalIgnoreCase))
            {
                hostAgentDesiredRows = await _repository.ApplyImportedHostAgentArtifactToCurrentHostAsync(
                    artifactId,
                    settings.ResolveHostKey(),
                    settings.SelfUpgrade.ServiceNamePrefix,
                    settings.SelfUpgrade.InstallRoot,
                    cancellationToken);
            }

            var result = new ArtifactZipImportResult(
                artifactId,
                metadata.Version,
                relativePath,
                copiedConfigurationFiles,
                application.TemplateAppRowsUpdated,
                application.AppInstanceRowsUpdated,
                application.WorkerInstanceRowsUpdated,
                hostAgentDesiredRows);

            MoveImportZip(zipPath, processedPath, null);
            _logger.LogInformation(
                "Imported artifact zip. Zip={ZipPath}, ArtifactId={ArtifactId}, Version={Version}, RelativePath={RelativePath}, AdoptedExistingContent={AdoptedExistingContent}, CopiedConfigurationFiles={CopiedConfigurationFiles}, TemplateRows={TemplateRows}, AppInstanceRows={AppInstanceRows}, WorkerInstanceRows={WorkerInstanceRows}, HostAgentDesiredRows={HostAgentDesiredRows}",
                zipPath,
                result.ArtifactId,
                result.Version,
                result.RelativePath,
                adoptedExistingContent,
                result.CopiedConfigurationFileCount,
                result.TemplateAppRowsUpdated,
                result.AppInstanceRowsUpdated,
                result.WorkerInstanceRowsUpdated,
                result.HostAgentDesiredRowsUpdated);
        }
        catch (Exception ex) when (IsExpectedImportFailure(ex))
        {
            if (movedToFinal && !artifactRegistered && !string.IsNullOrWhiteSpace(finalPath))
            {
                TryDelete(finalPath);
            }

            MoveImportZip(zipPath, failedPath, ex.Message);
            _logger.LogWarning(ex, "Artifact zip import failed. Zip={ZipPath}", zipPath);
        }
        finally
        {
            TryDelete(tempZipPath);
            TryDelete(stagingPath);
        }
    }

    private async Task<bool> TryCopyReadyZipAsync(
        string zipPath,
        string tempZipPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.None);
            await using var target = new FileStream(tempZipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Artifact zip is not ready for import. Zip={ZipPath}", zipPath);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Artifact zip is not accessible for import. Zip={ZipPath}", zipPath);
            return false;
        }
    }

    private static void ValidateArtifactEntryIsNotRuntimeConfiguration(string normalizedEntryName)
    {
        var fileName = normalizedEntryName.Split('/').LastOrDefault() ?? string.Empty;
        if (IsRuntimeConfigurationFileName(fileName))
        {
            throw new InvalidOperationException(
                $"The artifact zip contains runtime configuration file '{normalizedEntryName}'. Store runtime configuration in artifact configuration file rows instead.");
        }
    }

    private static bool IsRuntimeConfigurationFileName(string fileName)
    {
        if (string.Equals(fileName, "appsettings.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return RuntimeConfigurationFileNames.Any(
            name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ComputeDirectorySha256Async(string path, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relative);
            sha.TransformBlock(relativeBytes, 0, relativeBytes.Length, null, 0);

            var separator = new byte[] { 0 };
            sha.TransformBlock(separator, 0, separator.Length, null, 0);

            await using var stream = File.OpenRead(file);
            var buffer = new byte[HashBufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static FilenameMetadata? TryParseFilenameMetadata(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = name.Split("__", StringSplitOptions.None);
        if (parts.Length != 5
            || parts.Any(part => part.Length == 0 || !MetadataTokenPattern.IsMatch(part)))
        {
            return null;
        }

        return new FilenameMetadata(parts[0], parts[1], parts[2], parts[3], parts[4]);
    }

    private static string BuildDefaultRelativePath(string targetName, string packageType, string version)
    {
        var sanitizedTarget = SanitizePathSegment(targetName);
        var packageSegment = GetPackagePathSegment(packageType);
        var rootSegment = sanitizedTarget;
        var typedTargetSuffix = "-" + packageSegment;

        if (packageSegment is "web" or "service"
            && sanitizedTarget.EndsWith(typedTargetSuffix, StringComparison.OrdinalIgnoreCase))
        {
            rootSegment = sanitizedTarget[..^typedTargetSuffix.Length];
        }
        else if (packageSegment == "service"
            && sanitizedTarget.EndsWith("-backend", StringComparison.OrdinalIgnoreCase))
        {
            rootSegment = sanitizedTarget[..^"-backend".Length];
            packageSegment = "backend";
        }

        return $"{rootSegment}/{packageSegment}/{SanitizePathSegment(version)}";
    }

    private static string BuildRelativePath(
        ArtifactCompatibilitySlot compatibility,
        string targetName,
        string packageType,
        string version)
    {
        if (string.IsNullOrWhiteSpace(compatibility.RelativePathTemplate))
        {
            return BuildDefaultRelativePath(targetName, packageType, version);
        }

        return compatibility.RelativePathTemplate.Trim()
            .Replace("{moduleKey}", SanitizePathSegment(compatibility.ModuleKey), StringComparison.OrdinalIgnoreCase)
            .Replace("{appKey}", SanitizePathSegment(compatibility.AppKey), StringComparison.OrdinalIgnoreCase)
            .Replace("{targetName}", SanitizePathSegment(targetName), StringComparison.OrdinalIgnoreCase)
            .Replace("{packageType}", GetPackagePathSegment(packageType), StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", SanitizePathSegment(version), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPackagePathSegment(string packageType)
        => packageType.Trim().ToLowerInvariant() switch
        {
            "web-app" => "web",
            "service-app" => "service",
            "host-agent" => "hostagent",
            "worker-host" => "host",
            "worker" => "worker",
            "worker-plugin" => "worker",
            "channel-type" => "channel-type",
            var value => SanitizePathSegment(value)
        };

    private static string SanitizePathSegment(string value)
    {
        var sanitized = value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '-');
        }

        return sanitized.Replace(' ', '-');
    }

    private static string ResolveUnderRoot(string rootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Expected a relative artifact path.");
        }

        var fullRoot = Path.GetFullPath(rootPath);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedRoot = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, comparison))
        {
            throw new InvalidOperationException("The artifact path escapes the configured artifact store root.");
        }

        return fullPath;
    }

    private static void MoveImportZip(string zipPath, string destinationRoot, string? errorMessage)
    {
        try
        {
            Directory.CreateDirectory(destinationRoot);
            var destination = Path.Combine(
                destinationRoot,
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Path.GetFileName(zipPath)}");

            if (File.Exists(zipPath))
            {
                File.Move(zipPath, destination, overwrite: false);
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                File.WriteAllText(
                    destination + ".error.txt",
                    errorMessage.Trim(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (IOException)
        {
            // The next HostAgent cycle can retry or reclassify a file that
            // could not be moved after processing.
        }
        catch (UnauthorizedAccessException)
        {
            // The next HostAgent cycle can retry or reclassify a file that
            // could not be moved after processing.
        }
    }

    private static bool IsExpectedImportFailure(Exception exception)
        => exception is InvalidOperationException
            or InvalidDataException
            or IOException
            or SqlException
            or UnauthorizedAccessException;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup after a failed import.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup after a failed import.
        }
    }

    private sealed record FilenameMetadata(
        string ModuleKey,
        string AppKey,
        string PackageType,
        string TargetName,
        string Version);
}
