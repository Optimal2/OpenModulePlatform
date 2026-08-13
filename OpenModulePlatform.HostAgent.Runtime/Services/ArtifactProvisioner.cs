using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class ArtifactProvisioner
{
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly ILogger<ArtifactProvisioner> _logger;

    // Skip re-hashing an already-verified artifact when its cheap
    // size/mtime signature is unchanged, so the convergence loop does not read
    // and SHA-256 every artifact in full every cycle (R3-D2). Artifacts are
    // immutable, so a matching signature almost always means unchanged content;
    // a periodic forced re-hash still catches rare same-size/mtime corruption.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Signature, string? Hash, DateTimeOffset VerifiedUtc)> _verifiedCache
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ReHashInterval = TimeSpan.FromHours(1);

    public ArtifactProvisioner(
        IOptionsMonitor<HostAgentSettings> settings,
        ILogger<ArtifactProvisioner> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    private void RemoveExistingCorruptCopies(string localPath)
    {
        var directory = Path.GetDirectoryName(localPath);
        var name = Path.GetFileName(localPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var candidate in Directory.EnumerateFileSystemEntries(directory, $"{name}.corrupt-*"))
        {
            try
            {
                if (Directory.Exists(candidate))
                {
                    Directory.Delete(candidate, recursive: true);
                }
                else
                {
                    File.Delete(candidate);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove an earlier quarantined artifact copy '{CorruptPath}'.", candidate);
            }
        }
    }

    // Cheap change signature (stat only, no content read): file length+mtime,
    // or a directory's file count + total length + newest mtime. Returns null
    // if the path cannot be enumerated, which disables the shortcut safely.
    private static string? ComputeCheapSignature(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                return $"f:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }

            if (Directory.Exists(path))
            {
                long count = 0, totalLength = 0, maxTicks = 0;
                foreach (var file in Directory.EnumerateFiles(path, "*", OmpReparsePointGuard.RecursiveNoFollow))
                {
                    var info = new FileInfo(file);
                    count++;
                    totalLength += info.Length;
                    var ticks = info.LastWriteTimeUtc.Ticks;
                    if (ticks > maxTicks)
                    {
                        maxTicks = ticks;
                    }
                }

                return $"d:{count}:{totalLength}:{maxTicks}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _artifactLocks = new();

    public async Task<ArtifactProvisioningResult> EnsureAsync(
        ArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        // Serialize provisioning of the SAME artifact: the RPC EnsureArtifact
        // and the convergence cycle could otherwise both stage it and collide
        // on File.Move(overwrite:false), publishing a spurious Failed status
        // for a correct artifact (R3-D7).
        var gate = _artifactLocks.GetOrAdd(artifact.ArtifactId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await EnsureCoreAsync(artifact, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ArtifactProvisioningResult> EnsureCoreAsync(
        ArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;

        var localPath = ResolveLocalPath(settings, artifact);
        var expectedHash = NormalizeHash(artifact.Sha256);

        // A missing expected hash means content integrity cannot be checked at
        // all. Surface it as an error instead of silently accepting whatever is
        // on disk (R3-D6); the artifact row should always carry a SHA.
        if (expectedHash is null)
        {
            _logger.LogError(
                "Artifact {ArtifactId} has no Sha256 in the catalog; content integrity cannot be verified and any local/downloaded content is accepted unchecked.",
                artifact.ArtifactId);
        }

        if (File.Exists(localPath) || Directory.Exists(localPath))
        {
            var signature = ComputeCheapSignature(localPath);
            if (signature is not null
                && _verifiedCache.TryGetValue(localPath, out var cachedVerification)
                && cachedVerification.Signature == signature
                && DateTimeOffset.UtcNow - cachedVerification.VerifiedUtc < ReHashInterval
                && (expectedHash is null || string.Equals(cachedVerification.Hash, expectedHash, StringComparison.OrdinalIgnoreCase)))
            {
                return ArtifactProvisioningResult.Succeeded(localPath, cachedVerification.Hash);
            }

            var existingHash = await ArtifactHash.ComputeSha256Async(localPath, cancellationToken);
            if (expectedHash is null || string.Equals(existingHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                if (signature is not null)
                {
                    _verifiedCache[localPath] = (signature, existingHash, DateTimeOffset.UtcNow);
                }

                return ArtifactProvisioningResult.Succeeded(localPath, existingHash);
            }

            // Remove any earlier quarantined copies for this artifact first, so
            // repeated mismatches cannot pile up full copies and fill the cache
            // volume (R3-D8); at most one .corrupt-* per artifact survives.
            RemoveExistingCorruptCopies(localPath);

            var corruptPath = $"{localPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            _logger.LogWarning(
                "Local artifact hash mismatch. ArtifactId={ArtifactId}, LocalPath={LocalPath}, ExpectedSha256={ExpectedSha256}, ActualSha256={ActualSha256}. Moving to {CorruptPath}.",
                artifact.ArtifactId,
                localPath,
                expectedHash,
                existingHash,
                corruptPath);

            _verifiedCache.TryRemove(localPath, out _);
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

        // R8-P2-11. This provisioner writes as SYSTEM into the artifact cache and had no reparse
        // guard at all, while the import service two files over has had one since R7-S2. Both the
        // staging tree it creates and the final artifact path it moves into are checked, and the
        // whole path is walked rather than only the leaf: a junction on an intermediate directory
        // redirects everything below it while the leaf looks ordinary.
        OmpReparsePointGuard.EnsureNoReparsePointInPath(
            stagingRoot,
            settings.LocalArtifactCacheRoot,
            "Artifact staging root");
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

            OmpReparsePointGuard.EnsureNoReparsePointInPath(
                Path.GetDirectoryName(localPath)!,
                settings.LocalArtifactCacheRoot,
                "Artifact target directory");
            OmpReparsePointGuard.EnsureNotReparsePoint(localPath, "Artifact target path");
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

        // A rooted RelativePath used to bypass the CentralArtifactRoot
        // confinement entirely, letting a catalog row point the SYSTEM-level
        // HostAgent at any host-local path and copy it into the artifact cache —
        // while the import side (ResolveUnderRoot) treats the store as a hard
        // boundary and rejects rooted paths. Enforce the same boundary here so a
        // tampered catalog row cannot cross it (R4-D7).
        var relativeOrRooted = artifact.RelativePath.Trim();
        if (Path.IsPathRooted(relativeOrRooted))
        {
            throw new InvalidOperationException(
                $"Artifact '{artifact.ArtifactId}' has a rooted RelativePath '{relativeOrRooted}', which is not allowed; "
                + "artifact sources must resolve under the central artifact root.");
        }

        return CombineUnderRoot(settings.CentralArtifactRoot, relativeOrRooted, nameof(artifact.RelativePath));
    }

    private static string ResolveLocalPath(HostAgentSettings settings, ArtifactDescriptor artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.DesiredLocalPath))
        {
            return ResolveExplicitLocalPath(settings.LocalArtifactCacheRoot, artifact.DesiredLocalPath);
        }

        return CombineUnderRoot(settings.LocalArtifactCacheRoot, artifact.GetCacheRelativePath(), nameof(artifact.RelativePath));
    }

    private static string ResolveExplicitLocalPath(string rootPath, string desiredLocalPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException($"Root path for '{nameof(HostAgentSettings.LocalArtifactCacheRoot)}' is not configured.");
        }

        if (string.IsNullOrWhiteSpace(desiredLocalPath))
        {
            throw new InvalidOperationException($"Path for '{nameof(ArtifactDescriptor.DesiredLocalPath)}' is not configured.");
        }

        var fullRoot = Path.GetFullPath(rootPath.Trim());
        var trimmedDesiredPath = desiredLocalPath.Trim();
        var fullPath = Path.IsPathRooted(trimmedDesiredPath)
            ? Path.GetFullPath(trimmedDesiredPath)
            : Path.GetFullPath(Path.Join(fullRoot, trimmedDesiredPath));
        if (!IsSameOrChildPath(fullRoot, fullPath))
        {
            throw new InvalidOperationException(
                $"Path '{desiredLocalPath}' escapes root path '{fullRoot}' for '{nameof(ArtifactDescriptor.DesiredLocalPath)}'.");
        }

        return fullPath;
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

    /// <remarks>
    /// Delegates to the shared helper. This was one of three private copies, not all of which
    /// normalized their inputs, so a path with ".." segments could pass a containment check that
    /// was meant to stop exactly that (R8-P2-16..23).
    /// </remarks>
    private static bool IsSameOrChildPath(string rootPath, string candidatePath)
        => OmpPathContainment.IsSameOrChildPath(rootPath, candidatePath);

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

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", OmpReparsePointGuard.RecursiveNoFollow))
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
