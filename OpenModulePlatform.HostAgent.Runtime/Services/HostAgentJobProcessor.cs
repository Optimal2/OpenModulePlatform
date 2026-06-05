using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class HostAgentJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ILogger<HostAgentJobProcessor> _logger;

    public HostAgentJobProcessor(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        ILogger<HostAgentJobProcessor> logger)
    {
        _settings = settings;
        _repository = repository;
        _logger = logger;
    }

    public async Task ProcessPendingJobsAsync(
        string hostKey,
        string serviceName,
        int maxJobs,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        var leaseSeconds = Math.Max(300, settings.RefreshSeconds * 10);

        for (var index = 0; index < Math.Max(1, maxJobs); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = await _repository.TryClaimNextHostAgentJobAsync(
                hostKey,
                serviceName,
                leaseSeconds,
                cancellationToken);

            if (job is null)
            {
                return;
            }

            await ProcessJobAsync(job, cancellationToken);
        }
    }

    private async Task ProcessJobAsync(HostAgentJobWorkItem job, CancellationToken cancellationToken)
    {
        try
        {
            switch (job.JobType)
            {
                case HostAgentJobTypes.ArtifactRetentionCleanup:
                    await ProcessArtifactRetentionCleanupJobAsync(job, cancellationToken);
                    break;

                case HostAgentJobTypes.ArtifactCacheCleanup:
                    await ProcessArtifactCacheCleanupJobAsync(job, cancellationToken);
                    break;

                case HostAgentJobTypes.ArtifactStoreCleanup:
                    await ProcessArtifactStoreCleanupJobAsync(job, cancellationToken);
                    break;

                default:
                    await CompleteFailedAsync(
                        job,
                        $"Unsupported HostAgent job type '{job.JobType}'.",
                        cancellationToken);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "HostAgent job failed. HostAgentJobId={HostAgentJobId}, JobType={JobType}",
                job.HostAgentJobId,
                job.JobType);

            await CompleteFailedAsync(job, ex.Message, cancellationToken);
        }
    }

    private async Task ProcessArtifactRetentionCleanupJobAsync(
        HostAgentJobWorkItem job,
        CancellationToken cancellationToken)
    {
        if (job.HostId.HasValue)
        {
            await CompleteFailedAsync(
                job,
                "Artifact retention cleanup jobs must be global jobs without a specific host target.",
                cancellationToken);
            return;
        }

        var payload = string.IsNullOrWhiteSpace(job.PayloadJson)
            ? new ArtifactRetentionCleanupJobPayload()
            : JsonSerializer.Deserialize<ArtifactRetentionCleanupJobPayload>(job.PayloadJson, JsonOptions)
                ?? new ArtifactRetentionCleanupJobPayload();
        var maxVersionsToKeep = Math.Clamp(payload.MaxVersionsToKeep, 1, 100);

        var execution = await _repository.ExecuteArtifactRetentionCleanupAsync(
            maxVersionsToKeep,
            job.RequestedBy,
            cancellationToken);

        var storeCleanup = new ArtifactStoreCleanupJobResult();
        var seenPaths = new HashSet<string>(GetPathComparer());
        foreach (var entry in execution.ArtifactStoreEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessArtifactStoreCleanupEntryAsync(entry, storeCleanup, seenPaths, cancellationToken);
        }

        var result = new ArtifactRetentionCleanupJobResult
        {
            DeletedArtifactCount = execution.DeletedArtifacts.Count,
            ArtifactStoreEntryCount = execution.ArtifactStoreEntryCount,
            HostCacheEntryCount = execution.HostCacheEntryCount,
            CreatedHostAgentJobCount = execution.CreatedHostAgentJobCount,
            ArtifactStoreCleanup = storeCleanup,
            DeletedArtifacts = execution.DeletedArtifacts.ToList()
        };

        var status = storeCleanup.ErrorCount > 0
            ? HostAgentJobStatuses.Warning
            : HostAgentJobStatuses.Succeeded;
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        var message = storeCleanup.ErrorCount > 0
            ? "Artifact retention cleanup completed, but one or more artifact store entries could not be deleted."
            : null;

        await _repository.CompleteHostAgentJobAsync(
            job.HostAgentJobId,
            status,
            resultJson,
            message,
            cancellationToken);
    }

    private async Task ProcessArtifactCacheCleanupJobAsync(
        HostAgentJobWorkItem job,
        CancellationToken cancellationToken)
    {
        if (!job.HostId.HasValue)
        {
            await CompleteFailedAsync(
                job,
                "Artifact cache cleanup jobs must target a specific host.",
                cancellationToken);
            return;
        }

        var payload = string.IsNullOrWhiteSpace(job.PayloadJson)
            ? new ArtifactCacheCleanupJobPayload()
            : JsonSerializer.Deserialize<ArtifactCacheCleanupJobPayload>(job.PayloadJson, JsonOptions)
                ?? new ArtifactCacheCleanupJobPayload();

        var result = new ArtifactCacheCleanupJobResult();
        foreach (var entry in payload.ArtifactCacheEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessArtifactCacheCleanupEntryAsync(job.HostId.Value, entry, result, cancellationToken);
        }

        var status = result.ErrorCount > 0
            ? HostAgentJobStatuses.Warning
            : HostAgentJobStatuses.Succeeded;
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);

        await _repository.CompleteHostAgentJobAsync(
            job.HostAgentJobId,
            status,
            resultJson,
            result.ErrorCount > 0 ? "One or more artifact cache entries could not be deleted." : null,
            cancellationToken);
    }

    private async Task ProcessArtifactStoreCleanupJobAsync(
        HostAgentJobWorkItem job,
        CancellationToken cancellationToken)
    {
        var payload = string.IsNullOrWhiteSpace(job.PayloadJson)
            ? new ArtifactStoreCleanupJobPayload()
            : JsonSerializer.Deserialize<ArtifactStoreCleanupJobPayload>(job.PayloadJson, JsonOptions)
                ?? new ArtifactStoreCleanupJobPayload();

        var result = new ArtifactStoreCleanupJobResult();
        var seenPaths = new HashSet<string>(GetPathComparer());
        foreach (var entry in payload.ArtifactStoreEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessArtifactStoreCleanupEntryAsync(entry, result, seenPaths, cancellationToken);
        }

        var status = result.ErrorCount > 0
            ? HostAgentJobStatuses.Warning
            : HostAgentJobStatuses.Succeeded;
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);

        await _repository.CompleteHostAgentJobAsync(
            job.HostAgentJobId,
            status,
            resultJson,
            result.ErrorCount > 0 ? "One or more artifact store entries could not be deleted." : null,
            cancellationToken);
    }

    private async Task ProcessArtifactCacheCleanupEntryAsync(
        Guid hostId,
        ArtifactCacheCleanupJobEntry entry,
        ArtifactCacheCleanupJobResult result,
        CancellationToken cancellationToken)
    {
        if (!TryResolveArtifactCachePath(entry, out var localPath, out var validationError))
        {
            AddEntryResult(result, entry, localPath, "Skipped", validationError);
            return;
        }

        if (await _repository.IsHostArtifactCachePathInUseAsync(hostId, entry.ArtifactId, localPath, cancellationToken))
        {
            AddEntryResult(result, entry, localPath, "Skipped", "The artifact cache path is still referenced by host state.");
            return;
        }

        try
        {
            if (Directory.Exists(localPath))
            {
                Directory.Delete(localPath, recursive: true);
                AddEntryResult(result, entry, localPath, "Deleted", null);
            }
            else if (File.Exists(localPath))
            {
                File.Delete(localPath);
                AddEntryResult(result, entry, localPath, "Deleted", null);
            }
            else
            {
                AddEntryResult(result, entry, localPath, "Missing", null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddEntryResult(result, entry, localPath, "Error", ex.Message);
        }
    }

    private async Task ProcessArtifactStoreCleanupEntryAsync(
        ArtifactStoreCleanupJobEntry entry,
        ArtifactStoreCleanupJobResult result,
        HashSet<string> seenPaths,
        CancellationToken cancellationToken)
    {
        if (!TryResolveArtifactStorePath(entry, out var storePath, out var normalizedRelativePath, out var validationError))
        {
            AddStoreEntryResult(result, entry, normalizedRelativePath, storePath, "Error", validationError);
            return;
        }

        if (!seenPaths.Add(storePath))
        {
            AddStoreEntryResult(result, entry, normalizedRelativePath, storePath, "Skipped", "Duplicate artifact store path in cleanup payload.");
            return;
        }

        if (await _repository.IsArtifactStoreRelativePathReferencedAsync(normalizedRelativePath, cancellationToken))
        {
            AddStoreEntryResult(result, entry, normalizedRelativePath, storePath, "Skipped", "The artifact store path is still referenced by a registered artifact.");
            return;
        }

        try
        {
            if (Directory.Exists(storePath))
            {
                Directory.Delete(storePath, recursive: true);
                AddStoreEntryResult(result, entry, normalizedRelativePath, storePath, "Deleted", null);
            }
            else if (File.Exists(storePath))
            {
                File.Delete(storePath);
                AddStoreEntryResult(result, entry, normalizedRelativePath, storePath, "Deleted", null);
            }
            else
            {
                AddStoreEntryResult(result, entry, normalizedRelativePath, storePath, "Missing", null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddStoreEntryResult(result, entry, normalizedRelativePath, storePath, "Error", ex.Message);
        }
    }

    private bool TryResolveArtifactCachePath(
        ArtifactCacheCleanupJobEntry entry,
        out string localPath,
        out string? error)
    {
        localPath = string.Empty;
        error = null;

        var settings = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(settings.LocalArtifactCacheRoot))
        {
            error = "HostAgent:LocalArtifactCacheRoot is not configured.";
            return false;
        }

        string cacheRoot;
        try
        {
            cacheRoot = Path.GetFullPath(settings.LocalArtifactCacheRoot.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            error = $"HostAgent:LocalArtifactCacheRoot is not a valid path: {ex.Message}";
            return false;
        }

        if (!Directory.Exists(cacheRoot))
        {
            error = $"Local artifact cache root does not exist: {cacheRoot}.";
            return false;
        }

        string candidatePath;
        try
        {
            candidatePath = !string.IsNullOrWhiteSpace(entry.LocalPath)
                ? Path.GetFullPath(entry.LocalPath.Trim())
                : ResolveRelativeCachePath(cacheRoot, entry);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            error = $"Artifact cache path is not valid: {ex.Message}";
            return false;
        }

        if (!IsSameOrChildPath(cacheRoot, candidatePath))
        {
            error = $"Artifact cache path escapes the configured cache root: {candidatePath}.";
            return false;
        }

        if (string.Equals(cacheRoot, candidatePath, GetPathComparison()))
        {
            error = "Refusing to delete the artifact cache root.";
            return false;
        }

        var stagingRoot = Path.GetFullPath(Path.Join(cacheRoot, ".staging"));
        if (IsSameOrChildPath(stagingRoot, candidatePath))
        {
            error = "Refusing to delete HostAgent staging files from an artifact cleanup job.";
            return false;
        }

        localPath = candidatePath;
        return true;
    }

    private bool TryResolveArtifactStorePath(
        ArtifactStoreCleanupJobEntry entry,
        out string storePath,
        out string normalizedRelativePath,
        out string? error)
    {
        storePath = string.Empty;
        normalizedRelativePath = string.Empty;
        error = null;

        var settings = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(settings.CentralArtifactRoot))
        {
            error = "HostAgent:CentralArtifactRoot is not configured.";
            return false;
        }

        string storeRoot;
        try
        {
            storeRoot = Path.GetFullPath(settings.CentralArtifactRoot.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            error = $"HostAgent:CentralArtifactRoot is not a valid path: {ex.Message}";
            return false;
        }

        if (!Directory.Exists(storeRoot))
        {
            error = $"Central artifact store root does not exist: {storeRoot}.";
            return false;
        }

        if (!TryNormalizeRelativePath(entry.RelativePath, out normalizedRelativePath, out error))
        {
            return false;
        }

        string candidatePath;
        try
        {
            candidatePath = Path.GetFullPath(Path.Join(storeRoot, normalizedRelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            error = $"Artifact store path is not valid: {ex.Message}";
            return false;
        }

        if (!IsSameOrChildPath(storeRoot, candidatePath))
        {
            error = $"Artifact store path escapes the configured artifact store root: {candidatePath}.";
            return false;
        }

        if (string.Equals(storeRoot, candidatePath, GetPathComparison()))
        {
            error = "Refusing to delete the central artifact store root.";
            return false;
        }

        var availableRoot = Path.GetFullPath(Path.Join(storeRoot, "_available"));
        if (IsSameOrChildPath(availableRoot, candidatePath))
        {
            error = "Refusing to delete package-library files from an artifact retention cleanup job.";
            return false;
        }

        var stagingRoot = Path.GetFullPath(Path.Join(storeRoot, ".staging"));
        if (IsSameOrChildPath(stagingRoot, candidatePath))
        {
            error = "Refusing to delete HostAgent staging files from an artifact retention cleanup job.";
            return false;
        }

        storePath = candidatePath;
        return true;
    }

    private static bool TryNormalizeRelativePath(
        string? relativePath,
        out string normalizedRelativePath,
        out string? error)
    {
        normalizedRelativePath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "Artifact store relative path is empty.";
            return false;
        }

        var trimmed = relativePath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            error = $"Artifact store relative path is invalid: {relativePath}.";
            return false;
        }

        normalizedRelativePath = trimmed.Replace('\\', '/').Trim('/');
        if (normalizedRelativePath.Length == 0
            || normalizedRelativePath.Contains(':', StringComparison.Ordinal)
            || normalizedRelativePath.IndexOf('\0') >= 0)
        {
            error = $"Artifact store relative path is invalid: {relativePath}.";
            return false;
        }

        var segments = normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (segments.Length == 0
            || segments.Any(static segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            error = $"Artifact store relative path is invalid: {relativePath}.";
            return false;
        }

        return true;
    }

    private static string ResolveRelativeCachePath(string cacheRoot, ArtifactCacheCleanupJobEntry entry)
    {
        var relativePath = !string.IsNullOrWhiteSpace(entry.CacheRelativePath)
            ? entry.CacheRelativePath.Trim()
            : new ArtifactDescriptor
            {
                ArtifactId = entry.ArtifactId,
                PackageType = entry.PackageType,
                TargetName = entry.TargetName,
                Version = entry.Version
            }.GetCacheRelativePath();

        if (Path.IsPathRooted(relativePath))
        {
            return Path.GetFullPath(relativePath);
        }

        return Path.GetFullPath(Path.Join(cacheRoot, relativePath));
    }

    private static bool IsSameOrChildPath(string rootPath, string candidatePath)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var fullCandidate = Path.GetFullPath(candidatePath);
        var comparison = GetPathComparison();

        if (string.Equals(fullRoot, fullCandidate, comparison))
        {
            return true;
        }

        var normalizedRoot = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullCandidate.StartsWith(normalizedRoot, comparison);
    }

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static void AddEntryResult(
        ArtifactCacheCleanupJobResult result,
        ArtifactCacheCleanupJobEntry entry,
        string? localPath,
        string outcome,
        string? message)
    {
        result.Entries.Add(new ArtifactCacheCleanupEntryResult
        {
            ArtifactId = entry.ArtifactId,
            LocalPath = string.IsNullOrWhiteSpace(localPath) ? entry.LocalPath : localPath,
            Outcome = outcome,
            Message = message
        });

        switch (outcome)
        {
            case "Deleted":
                result.DeletedCount++;
                break;
            case "Missing":
                result.MissingCount++;
                break;
            case "Skipped":
                result.SkippedCount++;
                break;
            default:
                result.ErrorCount++;
                break;
        }
    }

    private static void AddStoreEntryResult(
        ArtifactStoreCleanupJobResult result,
        ArtifactStoreCleanupJobEntry entry,
        string? normalizedRelativePath,
        string? storePath,
        string outcome,
        string? message)
    {
        result.Entries.Add(new ArtifactStoreCleanupEntryResult
        {
            ArtifactId = entry.ArtifactId,
            RelativePath = string.IsNullOrWhiteSpace(normalizedRelativePath) ? entry.RelativePath : normalizedRelativePath,
            StorePath = storePath,
            Outcome = outcome,
            Message = message
        });

        switch (outcome)
        {
            case "Deleted":
                result.DeletedCount++;
                break;
            case "Missing":
                result.MissingCount++;
                break;
            case "Skipped":
                result.SkippedCount++;
                break;
            default:
                result.ErrorCount++;
                break;
        }
    }

    private async Task CompleteFailedAsync(
        HostAgentJobWorkItem job,
        string error,
        CancellationToken cancellationToken)
    {
        await _repository.CompleteHostAgentJobAsync(
            job.HostAgentJobId,
            HostAgentJobStatuses.Failed,
            null,
            error,
            cancellationToken);
    }
}
