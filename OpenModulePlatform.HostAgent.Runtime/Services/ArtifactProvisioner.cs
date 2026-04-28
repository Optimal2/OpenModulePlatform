using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class ArtifactProvisioner
{
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly ILogger<ArtifactProvisioner> _logger;

    public ArtifactProvisioner(
        IOptionsMonitor<HostAgentSettings> settings,
        ILogger<ArtifactProvisioner> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<ArtifactProvisioningResult> EnsureAsync(
        ArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var settings = _settings.CurrentValue;
        settings.Validate();

        var localPath = ResolveLocalPath(settings, artifact);
        var expectedHash = NormalizeHash(artifact.Sha256);

        if (File.Exists(localPath) || Directory.Exists(localPath))
        {
            var existingHash = await ArtifactHash.ComputeSha256Async(localPath, cancellationToken);
            if (expectedHash is null || string.Equals(existingHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return ArtifactProvisioningResult.Succeeded(localPath, existingHash);
            }

            var corruptPath = $"{localPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            _logger.LogWarning(
                "Local artifact hash mismatch. ArtifactId={ArtifactId}, LocalPath={LocalPath}, ExpectedSha256={ExpectedSha256}, ActualSha256={ActualSha256}. Moving to {CorruptPath}.",
                artifact.ArtifactId,
                localPath,
                expectedHash,
                existingHash,
                corruptPath);

            MoveExisting(localPath, corruptPath);
        }

        var sourcePath = ResolveSourcePath(settings, artifact);
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            return ArtifactProvisioningResult.Failed(
                ArtifactProvisioningState.Failed,
                localPath,
                $"Artifact source path does not exist: '{sourcePath}'.");
        }

        var stagingRoot = CombineUnderRoot(settings.LocalArtifactCacheRoot, ".staging", nameof(settings.LocalArtifactCacheRoot));
        Directory.CreateDirectory(stagingRoot);
        var stagingPath = CombineUnderRoot(stagingRoot, $"artifact-{artifact.ArtifactId}-{Guid.NewGuid():N}", nameof(stagingRoot));

        try
        {
            if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, stagingPath, cancellationToken);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
                File.Copy(sourcePath, stagingPath, overwrite: false);
            }

            var stagedHash = await ArtifactHash.ComputeSha256Async(stagingPath, cancellationToken);
            if (expectedHash is not null && !string.Equals(stagedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return ArtifactProvisioningResult.Failed(
                    ArtifactProvisioningState.HashMismatch,
                    localPath,
                    $"Downloaded artifact hash mismatch. Expected {expectedHash}, actual {stagedHash}.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            if (File.Exists(stagingPath))
            {
                File.Move(stagingPath, localPath, overwrite: false);
            }
            else
            {
                Directory.Move(stagingPath, localPath);
            }

            return ArtifactProvisioningResult.Succeeded(localPath, stagedHash);
        }
        finally
        {
            TryDelete(stagingPath);
        }
    }

    private static string ResolveSourcePath(HostAgentSettings settings, ArtifactDescriptor artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.RelativePath))
        {
            throw new InvalidOperationException($"Artifact '{artifact.ArtifactId}' has no RelativePath.");
        }

        var relativeOrRooted = artifact.RelativePath.Trim();
        return Path.IsPathRooted(relativeOrRooted)
            ? Path.GetFullPath(relativeOrRooted)
            : CombineUnderRoot(settings.CentralArtifactRoot, relativeOrRooted, nameof(artifact.RelativePath));
    }

    private static string ResolveLocalPath(HostAgentSettings settings, ArtifactDescriptor artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.DesiredLocalPath))
        {
            return Path.GetFullPath(artifact.DesiredLocalPath.Trim());
        }

        return CombineUnderRoot(settings.LocalArtifactCacheRoot, artifact.GetCacheRelativePath(), nameof(artifact.RelativePath));
    }

    private static string CombineUnderRoot(string rootPath, string relativePath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException($"Root path for '{parameterName}' is not configured.");
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException($"Relative path for '{parameterName}' is not configured.");
        }

        var trimmedRelativePath = relativePath.Trim();
        if (Path.IsPathRooted(trimmedRelativePath))
        {
            throw new InvalidOperationException($"Expected a relative path for '{parameterName}', but got '{relativePath}'.");
        }

        var fullRoot = Path.GetFullPath(rootPath.Trim());
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, trimmedRelativePath));
        if (!IsSameOrChildPath(fullRoot, fullPath))
        {
            throw new InvalidOperationException($"Path '{relativePath}' escapes root path '{fullRoot}'.");
        }

        return fullPath;
    }

    private static bool IsSameOrChildPath(string rootPath, string candidatePath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(rootPath, candidatePath, comparison))
        {
            return true;
        }

        var normalizedRoot = Path.EndsInDirectorySeparator(rootPath)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        return candidatePath.StartsWith(normalizedRoot, comparison);
    }

    private static string? NormalizeHash(string? hash)
    {
        return string.IsNullOrWhiteSpace(hash) ? null : hash.Trim().ToLowerInvariant();
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeDirectory = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(CombineUnderRoot(targetDirectory, relativeDirectory, nameof(targetDirectory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeFile = Path.GetRelativePath(sourceDirectory, file);
            var targetFile = CombineUnderRoot(targetDirectory, relativeFile, nameof(targetDirectory));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: false);
        }
    }

    private static void MoveExisting(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination, overwrite: false);
            return;
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
    }

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
            // Best effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup only.
        }
    }
}
