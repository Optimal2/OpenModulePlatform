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

    // R12-F12, host-local half. The missing-hash warning below used to be written at Error
    // on EVERY provisioning of the artifact -- once per artifact per 30-second cycle,
    // forever -- which is loud without being informative: it never says how many artifacts
    // are in that state, so it cannot be driven to zero, and its own repetition buries it.
    // The tracker turns the same observations into one recurring aggregate that names the
    // count and the ids, and the per-artifact line stays at Error only the first time an
    // artifact is seen without a hash (or regresses to it), which is when it is news.
    // The catalog-wide half lives in ArtifactZipImportService; this half is the number that
    // decides whether HostAgent:RequireArtifactHash can be enabled on THIS host, because
    // these are exactly the artifacts the flag would refuse here.
    private readonly ArtifactContentHashGapTracker _hashGapTracker = new();

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

    /// <summary>
    /// A per-artifact gate plus the count of callers currently holding or waiting on it.
    /// </summary>
    private sealed class ArtifactLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Only ever read or written under <see cref="_artifactLocksSync"/>.</summary>
        public int RefCount;
    }

    // The gate used to be a ConcurrentDictionary<int, SemaphoreSlim> that only ever grew:
    // one SemaphoreSlim per distinct ArtifactId, never removed. The HostAgent is a
    // long-running service and every release introduces new artifact ids, so the map -- and
    // the kernel objects behind each semaphore -- accumulated for the lifetime of the
    // process. Reported by GitHub code quality.
    //
    // Entries are now reference counted and removed when the last caller leaves. The
    // counting has to be atomic with the dictionary mutation, otherwise a caller could
    // take a reference to an entry that another thread is in the middle of removing, so a
    // plain lock guards both. The lock is never held across the await: only the
    // bookkeeping runs inside it.
    private readonly Dictionary<int, ArtifactLock> _artifactLocks = [];
    private readonly object _artifactLocksSync = new();

    public async Task<ArtifactProvisioningResult> EnsureAsync(
        ArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        // Serialize provisioning of the SAME artifact: the RPC EnsureArtifact
        // and the convergence cycle could otherwise both stage it and collide
        // on File.Move(overwrite:false), publishing a spurious Failed status
        // for a correct artifact (R3-D7).
        var entry = AcquireArtifactLock(artifact.ArtifactId);

        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            // The wait was cancelled or failed, so the gate was never taken and must not
            // be released -- but the reference taken above still has to be given back, or
            // the entry would never be removed and the leak would simply be slower.
            ReleaseArtifactLockReference(artifact.ArtifactId, entry);
            throw;
        }

        try
        {
            return await EnsureCoreAsync(artifact, cancellationToken);
        }
        finally
        {
            entry.Gate.Release();
            ReleaseArtifactLockReference(artifact.ArtifactId, entry);
        }
    }

    private ArtifactLock AcquireArtifactLock(int artifactId)
    {
        lock (_artifactLocksSync)
        {
            if (!_artifactLocks.TryGetValue(artifactId, out var entry))
            {
                entry = new ArtifactLock();
                _artifactLocks[artifactId] = entry;
            }

            entry.RefCount++;
            return entry;
        }
    }

    private void ReleaseArtifactLockReference(int artifactId, ArtifactLock entry)
    {
        lock (_artifactLocksSync)
        {
            if (--entry.RefCount > 0)
            {
                return;
            }

            // Reaching zero under the same lock that hands out references means nobody
            // else holds this entry and nobody can obtain it: a caller arriving now takes
            // the lock, fails the lookup and creates a fresh one. Disposing here is
            // therefore safe, and it is the point of the exercise -- leaving the entry
            // behind would keep the semaphore alive for the life of the process.
            _artifactLocks.Remove(artifactId);
            entry.Gate.Dispose();
        }
    }

    private async Task<ArtifactProvisioningResult> EnsureCoreAsync(
        ArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;

        var localPath = ResolveLocalPath(settings, artifact);
        var expectedHash = NormalizeHash(artifact.Sha256);

        // A missing expected hash means content integrity cannot be checked at all. The
        // code used to log this as an error under a comment claiming it was surfaced
        // "instead of silently accepting" -- and then accept it anyway, which is both at
        // once and told the operator the opposite of what happened (R3-D6).
        //
        // Which behaviour is right depends on the installation, so it is a setting.
        // Accepting is defensible: artifact identity still has to match, which is what
        // R8-P2-10 concluded. Refusing is defensible too. The default keeps the existing
        // behaviour so no running installation changes on upgrade.
        var observation = _hashGapTracker.Observe(
            artifact.ArtifactId,
            hasContentHash: expectedHash is not null,
            DateTimeOffset.UtcNow);
        ReportHashGapAudit(observation.Audit);

        if (expectedHash is null)
        {
            if (settings.RequireArtifactHash)
            {
                _logger.LogError(
                    "Artifact {ArtifactId} has no Sha256 in the catalog and HostAgent:RequireArtifactHash is set; refusing to provision it.",
                    artifact.ArtifactId);

                return ArtifactProvisioningResult.Failed(
                    ArtifactProvisioningState.Failed,
                    localPath,
                    $"Artifact {artifact.ArtifactId} has no Sha256 in the catalog and RequireArtifactHash is enabled.");
            }

            // Loud on the transition, quiet on the repeat: the recurring aggregate below is
            // what carries the standing state, so repeating this line every cycle only made
            // the gap harder to see, not easier (R12-F12).
            if (observation.IsNewlyMissingContentHash)
            {
                _logger.LogError(
                    "Artifact {ArtifactId} has no Sha256 in the catalog; content integrity is NOT verified and the local/downloaded content is accepted unchecked. Set HostAgent:RequireArtifactHash to refuse instead.",
                    artifact.ArtifactId);
            }
            else
            {
                _logger.LogDebug(
                    "Artifact {ArtifactId} still has no Sha256 in the catalog; content is accepted unverified. See the recurring artifact content hash audit for the total.",
                    artifact.ArtifactId);
            }
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

    private void ReportHashGapAudit(ArtifactContentHashGapAudit? audit)
    {
        if (audit is null)
        {
            return;
        }

        if (audit.MissingContentHashArtifactIds.Count == 0)
        {
            _logger.LogInformation(
                "Artifact content hash audit: all {ObservedCount} artifact(s) this host provisioned in the last {WindowHours} h carry a Sha256, so nothing here would be refused. HostAgent:RequireArtifactHash can be enabled on this host.",
                audit.ObservedArtifactCount,
                ArtifactContentHashGapTracker.RetentionWindow.TotalHours);
            return;
        }

        _logger.LogWarning(
            "Artifact content hash audit: {MissingCount} of {ObservedCount} artifact(s) this host provisioned carry no Sha256, so their content is accepted unverified and HostAgent:RequireArtifactHash cannot be enabled here until this reaches 0. ArtifactIds={ArtifactIds}",
            audit.MissingContentHashArtifactIds.Count,
            audit.ObservedArtifactCount,
            string.Join(", ", audit.MissingContentHashArtifactIds));
    }
}

/// <summary>
/// One aggregated report of the artifacts provisioned on this host that carry no content
/// hash.
/// </summary>
internal sealed record ArtifactContentHashGapAudit(
    int ObservedArtifactCount,
    IReadOnlyList<int> MissingContentHashArtifactIds);

/// <summary>The outcome of recording one artifact observation.</summary>
internal readonly record struct ArtifactContentHashObservation(
    bool IsNewlyMissingContentHash,
    ArtifactContentHashGapAudit? Audit);

/// <summary>
/// Turns the stream of per-artifact provisioning observations into a recurring aggregate:
/// how many distinct artifacts this host provisions without a catalog content hash
/// (R12-F12).
/// </summary>
/// <remarks>
/// State is per artifact id and holds the LATEST observation, not a running tally, so the
/// count actually falls when an operator fills a missing Sha256 in and the artifact is
/// provisioned again. Entries not observed within <see cref="RetentionWindow" /> are
/// evicted, so an artifact that stops being desired on this host stops holding the count
/// above zero -- without that, "drive it to zero" would be unreachable by construction and
/// the signal would be worthless (metod 4.2: the fix has to change what the operator can
/// observe, not merely add a line).
/// </remarks>
internal sealed class ArtifactContentHashGapTracker
{
    /// <summary>
    /// Matches ArtifactProvisioner.ReHashInterval: long enough that the aggregate is 24
    /// lines a day rather than a per-cycle stream, short enough that an operator who fixes
    /// a hash sees the number move within the hour.
    /// </summary>
    public static readonly TimeSpan AuditInterval = TimeSpan.FromHours(1);

    /// <summary>How long an unobserved artifact keeps counting before it is evicted.</summary>
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(2);

    private readonly object _sync = new();
    private readonly Dictionary<int, (bool HasContentHash, DateTimeOffset LastSeenUtc)> _observations = [];
    private DateTimeOffset? _lastAuditUtc;
    private int _lastReportedMissingCount = -1;

    public ArtifactContentHashObservation Observe(int artifactId, bool hasContentHash, DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            var known = _observations.TryGetValue(artifactId, out var previous);
            var isNewlyMissing = !hasContentHash && (!known || previous.HasContentHash);
            _observations[artifactId] = (hasContentHash, nowUtc);

            return new ArtifactContentHashObservation(isNewlyMissing, TryBuildAudit(nowUtc));
        }
    }

    private ArtifactContentHashGapAudit? TryBuildAudit(DateTimeOffset nowUtc)
    {
        var stale = nowUtc - RetentionWindow;
        foreach (var artifactId in _observations
            .Where(entry => entry.Value.LastSeenUtc < stale)
            .Select(static entry => entry.Key)
            .ToList())
        {
            _observations.Remove(artifactId);
        }

        var missing = _observations
            .Where(static entry => !entry.Value.HasContentHash)
            .Select(static entry => entry.Key)
            .OrderBy(static artifactId => artifactId)
            .ToList();

        // The very first observation after a restart is not an audit -- one artifact seen is
        // not a measurement of the host -- so it only starts the clock.
        if (_lastAuditUtc is not { } lastAuditUtc)
        {
            _lastAuditUtc = nowUtc;
            _lastReportedMissingCount = missing.Count;
            return null;
        }

        // Report on the interval so the state is standing and visible, and additionally the
        // moment the number changes, so both a new gap and progress towards zero are
        // confirmed immediately instead of up to an hour later.
        var intervalElapsed = nowUtc - lastAuditUtc >= AuditInterval;
        var countChanged = missing.Count != _lastReportedMissingCount;
        if (!intervalElapsed && !countChanged)
        {
            return null;
        }

        _lastAuditUtc = nowUtc;
        _lastReportedMissingCount = missing.Count;
        return new ArtifactContentHashGapAudit(_observations.Count, missing);
    }
}
