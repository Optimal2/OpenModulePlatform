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
                case HostAgentJobTypes.ArtifactCacheCleanup:
                    await ProcessArtifactCacheCleanupJobAsync(job, cancellationToken);
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

    private async Task ProcessArtifactCacheCleanupJobAsync(
        HostAgentJobWorkItem job,
        CancellationToken cancellationToken)
    {
        var payload = string.IsNullOrWhiteSpace(job.PayloadJson)
            ? new ArtifactCacheCleanupJobPayload()
            : JsonSerializer.Deserialize<ArtifactCacheCleanupJobPayload>(job.PayloadJson, JsonOptions)
                ?? new ArtifactCacheCleanupJobPayload();

        var result = new ArtifactCacheCleanupJobResult();
        foreach (var entry in payload.ArtifactCacheEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessArtifactCacheCleanupEntryAsync(job.HostId, entry, result, cancellationToken);
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
