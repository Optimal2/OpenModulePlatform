using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class HostAgentJobProcessor
{
    private const int ScServiceNotFoundExitCode = 1060;
    private const int DirectoryDeleteMaxAttempts = 20;
    private static readonly TimeSpan DirectoryDeleteRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly string[] KnownHostAgentServiceNamePrefixes =
    [
        "EMP.HostAgent",
        "OMP.HostAgent",
        "OpenModulePlatform.HostAgent"
    ];

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

            await ProcessJobAsync(job, hostKey, serviceName, cancellationToken);
        }
    }

    private async Task ProcessJobAsync(
        HostAgentJobWorkItem job,
        string hostKey,
        string serviceName,
        CancellationToken cancellationToken)
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

                case HostAgentJobTypes.MaintenanceScan:
                    await ProcessMaintenanceScanJobAsync(job, hostKey, serviceName, cancellationToken);
                    break;

                case HostAgentJobTypes.MaintenanceCleanup:
                    await ProcessMaintenanceCleanupJobAsync(job, cancellationToken);
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

    private async Task ProcessMaintenanceScanJobAsync(
        HostAgentJobWorkItem job,
        string hostKey,
        string serviceName,
        CancellationToken cancellationToken)
    {
        var payload = string.IsNullOrWhiteSpace(job.PayloadJson)
            ? new MaintenanceScanJobPayload()
            : JsonSerializer.Deserialize<MaintenanceScanJobPayload>(job.PayloadJson, JsonOptions)
                ?? new MaintenanceScanJobPayload();

        var scope = string.IsNullOrWhiteSpace(payload.Scope)
            ? MaintenanceScanScopes.Host
            : payload.Scope.Trim();

        List<string> findingKeys;
        if (string.Equals(scope, MaintenanceScanScopes.Global, StringComparison.OrdinalIgnoreCase))
        {
            if (job.HostId.HasValue)
            {
                await CompleteFailedAsync(
                    job,
                    "Global maintenance scan jobs must not target a specific host.",
                    cancellationToken);
                return;
            }

            findingKeys = (await _repository.UpsertStaleHostAgentRuntimeStateFindingsAsync(
                job.HostAgentJobId,
                cancellationToken)).ToList();
        }
        else
        {
            if (!job.HostId.HasValue)
            {
                await CompleteFailedAsync(
                    job,
                    "Host maintenance scan jobs must target a specific host.",
                    cancellationToken);
                return;
            }

            var findings = BuildHostAgentLeftoverFindings(
                job.HostId.Value,
                hostKey,
                serviceName,
                _settings.CurrentValue,
                cancellationToken);

            await _repository.UpsertMaintenanceFindingsAsync(
                findings,
                job.HostAgentJobId,
                cancellationToken);

            findingKeys = findings.Select(static finding => finding.FindingKey).ToList();
        }

        var result = new MaintenanceScanJobResult
        {
            Scope = scope,
            FindingCount = findingKeys.Count,
            FindingKeys = findingKeys
        };

        await _repository.CompleteHostAgentJobAsync(
            job.HostAgentJobId,
            HostAgentJobStatuses.Succeeded,
            JsonSerializer.Serialize(result, JsonOptions),
            null,
            cancellationToken);
    }

    private async Task ProcessMaintenanceCleanupJobAsync(
        HostAgentJobWorkItem job,
        CancellationToken cancellationToken)
    {
        var payload = string.IsNullOrWhiteSpace(job.PayloadJson)
            ? new MaintenanceCleanupJobPayload()
            : JsonSerializer.Deserialize<MaintenanceCleanupJobPayload>(job.PayloadJson, JsonOptions)
                ?? new MaintenanceCleanupJobPayload();

        var entries = await _repository.GetMaintenanceCleanupEntriesAsync(
            job.HostId,
            payload.FindingIds,
            cancellationToken);

        var result = new MaintenanceCleanupJobResult();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryResult = await ProcessMaintenanceCleanupEntryAsync(entry, job.HostAgentJobId, cancellationToken);
            AddMaintenanceCleanupResult(result, entryResult);
        }

        var status = result.ErrorCount > 0
            ? HostAgentJobStatuses.Warning
            : HostAgentJobStatuses.Succeeded;

        await _repository.CompleteHostAgentJobAsync(
            job.HostAgentJobId,
            status,
            JsonSerializer.Serialize(result, JsonOptions),
            result.ErrorCount > 0 ? "One or more maintenance findings could not be cleaned." : null,
            cancellationToken);
    }

    private async Task<MaintenanceCleanupEntryResult> ProcessMaintenanceCleanupEntryAsync(
        MaintenanceFindingCleanupEntry entry,
        long hostAgentJobId,
        CancellationToken cancellationToken)
    {
        MaintenanceFindingAction? action = null;
        if (!string.IsNullOrWhiteSpace(entry.ActionJson))
        {
            action = JsonSerializer.Deserialize<MaintenanceFindingAction>(entry.ActionJson, JsonOptions);
        }

        var targetKind = string.IsNullOrWhiteSpace(action?.TargetKind)
            ? entry.TargetKind
            : action.TargetKind.Trim();

        MaintenanceCleanupEntryResult result;
        switch (targetKind)
        {
            case MaintenanceTargetKinds.WindowsService:
                result = CleanupWindowsServiceFinding(entry, action);
                break;
            case MaintenanceTargetKinds.Directory:
                result = CleanupDirectoryFinding(entry, action, cancellationToken);
                break;
            case MaintenanceTargetKinds.DatabaseRow:
                result = await CleanupDatabaseRowFindingAsync(entry, action, cancellationToken);
                break;
            default:
                result = CreateMaintenanceCleanupEntryResult(
                    entry,
                    "Skipped",
                    $"Maintenance target kind '{targetKind}' is not implemented by this HostAgent version.");
                break;
        }

        var status = result.Outcome switch
        {
            "Cleaned" or "Missing" => MaintenanceFindingStatuses.Cleaned,
            "Skipped" => MaintenanceFindingStatuses.Skipped,
            _ => MaintenanceFindingStatuses.Failed
        };

        await _repository.UpdateMaintenanceFindingResultAsync(
            entry.MaintenanceFindingId,
            status,
            result.Message,
            hostAgentJobId,
            cancellationToken);

        return result;
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

    private IReadOnlyList<MaintenanceFindingUpsert> BuildHostAgentLeftoverFindings(
        Guid hostId,
        string hostKey,
        string serviceName,
        HostAgentSettings settings,
        CancellationToken cancellationToken)
    {
        var findings = new List<MaintenanceFindingUpsert>();
        var serviceNamePrefix = ResolveServiceNamePrefixForMaintenance(settings, serviceName);
        var installRoot = ResolveHostAgentInstallRoot(settings);
        var currentInstallDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var credentialStoreProtectedDirectories = GetCredentialStoreProtectedDirectories(settings)
            .Select(Path.GetFullPath)
            .ToHashSet(GetPathComparer());

        var services = EnumerateHostAgentServices(serviceNamePrefix);
        foreach (var service in services)
        {
            if (string.Equals(service.Name, serviceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service.State, "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var detail =
                $"Host '{hostKey}' has a stopped HostAgent service '{service.Name}' in state '{service.State ?? "unknown"}'. Executable path: {service.ExecutablePath ?? "unknown"}.";
            var action = JsonSerializer.Serialize(new MaintenanceFindingAction
            {
                TargetKind = MaintenanceTargetKinds.WindowsService,
                HostId = hostId,
                ServiceName = service.Name
            }, JsonOptions);

            findings.Add(new MaintenanceFindingUpsert
            {
                FindingKey = $"hostagent-service:{hostId:D}:{service.Name}",
                Scope = MaintenanceScanScopes.Host,
                HostId = hostId,
                Category = "HostAgentLeftover",
                TargetKind = MaintenanceTargetKinds.WindowsService,
                TargetIdentifier = service.Name,
                Title = "Stopped old HostAgent service",
                Detail = detail,
                RecommendedAction = "Delete the stopped old HostAgent Windows service.",
                SafetyNotes = "The service name matches a known HostAgent prefix, is not the active HostAgent service, and is not currently running.",
                ActionJson = action,
                Severity = 2,
                Confidence = string.IsNullOrWhiteSpace(service.ExecutablePath) ? (byte)75 : (byte)90
            });
        }

        if (!Directory.Exists(installRoot))
        {
            return findings;
        }

        var serviceDirectories = services
            .Select(static service => service.ExecutablePath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetDirectoryName(path!))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .ToHashSet(GetPathComparer());

        foreach (var directory in EnumerateHostAgentInstallDirectories(installRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullDirectory, currentInstallDirectory, GetPathComparison())
                || credentialStoreProtectedDirectories.Contains(fullDirectory))
            {
                continue;
            }

            var isReferencedByService = serviceDirectories.Contains(fullDirectory);
            var detail = isReferencedByService
                ? $"Host '{hostKey}' has a HostAgent directory that is still referenced by a stopped service: {fullDirectory}."
                : $"Host '{hostKey}' has a HostAgent directory that is not the active HostAgent install directory: {fullDirectory}.";

            var action = JsonSerializer.Serialize(new MaintenanceFindingAction
            {
                TargetKind = MaintenanceTargetKinds.Directory,
                HostId = hostId,
                Path = fullDirectory,
                InstallRoot = installRoot
            }, JsonOptions);

            findings.Add(new MaintenanceFindingUpsert
            {
                FindingKey = $"hostagent-directory:{hostId:D}:{fullDirectory}",
                Scope = MaintenanceScanScopes.Host,
                HostId = hostId,
                Category = "HostAgentLeftover",
                TargetKind = MaintenanceTargetKinds.Directory,
                TargetIdentifier = fullDirectory,
                Title = "Old HostAgent directory",
                Detail = detail,
                RecommendedAction = "Delete the old HostAgent directory after any stopped service that references it has been removed.",
                SafetyNotes = "The directory is below the configured HostAgent install root and is not the active HostAgent process directory. Cleanup rechecks service references before deleting.",
                ActionJson = action,
                Severity = isReferencedByService ? (byte)1 : (byte)2,
                Confidence = isReferencedByService ? (byte)75 : (byte)90
            });
        }

        return findings;
    }

    private MaintenanceCleanupEntryResult CleanupWindowsServiceFinding(
        MaintenanceFindingCleanupEntry entry,
        MaintenanceFindingAction? action)
    {
        var serviceName = action?.ServiceName?.Trim();
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Error", "The cleanup action does not contain a Windows service name.");
        }

        var currentServiceName = _settings.CurrentValue.ServiceName;
        if (string.Equals(serviceName, currentServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Skipped", "Refusing to delete the active HostAgent service.");
        }

        var state = GetServiceState(serviceName);
        if (state is null)
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Missing", "The Windows service was already missing.");
        }

        if (string.Equals(state, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Skipped", "Refusing to delete a running Windows service.");
        }

        var result = RunSc("delete", serviceName);
        if (result.ExitCode == 0 || IsServiceNotFound(result))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Cleaned", $"Deleted Windows service '{serviceName}'.");
        }

        return CreateMaintenanceCleanupEntryResult(
            entry,
            "Error",
            $"sc.exe delete failed with exit code {result.ExitCode}: {result.CombinedOutput.Trim()}");
    }

    private MaintenanceCleanupEntryResult CleanupDirectoryFinding(
        MaintenanceFindingCleanupEntry entry,
        MaintenanceFindingAction? action,
        CancellationToken cancellationToken)
    {
        if (!TryResolveMaintenanceDirectory(action, out var directory, out var error))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Error", error);
        }

        if (!Directory.Exists(directory))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Missing", "The directory was already missing.");
        }

        var settings = _settings.CurrentValue;
        var currentInstallDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(directory, currentInstallDirectory, GetPathComparison()))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Skipped", "Refusing to delete the active HostAgent process directory.");
        }

        if (GetCredentialStoreProtectedDirectories(settings).Any(path => string.Equals(Path.GetFullPath(path), directory, GetPathComparison())))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Skipped", "Refusing to delete a directory that contains the configured HostAgent credential store.");
        }

        var serviceNamePrefix = ResolveServiceNamePrefixForMaintenance(settings, settings.ServiceName);
        var referencingService = EnumerateHostAgentServices(serviceNamePrefix)
            .FirstOrDefault(service =>
            {
                if (string.IsNullOrWhiteSpace(service.ExecutablePath))
                {
                    return false;
                }

                var serviceDirectory = Path.GetDirectoryName(service.ExecutablePath);
                return !string.IsNullOrWhiteSpace(serviceDirectory)
                    && string.Equals(Path.GetFullPath(serviceDirectory), directory, GetPathComparison());
            });

        if (referencingService is not null)
        {
            return CreateMaintenanceCleanupEntryResult(
                entry,
                "Skipped",
                $"Refusing to delete the directory while Windows service '{referencingService.Name}' still references it.");
        }

        try
        {
            DeleteDirectoryWithRetry(directory, cancellationToken);
            return CreateMaintenanceCleanupEntryResult(entry, "Cleaned", $"Deleted directory '{directory}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Error", ex.Message);
        }
    }

    private async Task<MaintenanceCleanupEntryResult> CleanupDatabaseRowFindingAsync(
        MaintenanceFindingCleanupEntry entry,
        MaintenanceFindingAction? action,
        CancellationToken cancellationToken)
    {
        if (action?.HostId is not { } hostId || string.IsNullOrWhiteSpace(action.ServiceName))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Error", "The cleanup action does not contain a HostAgent runtime row identity.");
        }

        var deletedRows = await _repository.DeleteStaleHostAgentRuntimeStateAsync(
            hostId,
            action.ServiceName.Trim(),
            cancellationToken);

        return deletedRows > 0
            ? CreateMaintenanceCleanupEntryResult(entry, "Cleaned", "Deleted stale HostAgent runtime-state row.")
            : CreateMaintenanceCleanupEntryResult(entry, "Missing", "The runtime-state row was already missing or is now protected by active desired/runtime state.");
    }

    private static MaintenanceCleanupEntryResult CreateMaintenanceCleanupEntryResult(
        MaintenanceFindingCleanupEntry entry,
        string outcome,
        string? message)
        => new()
        {
            MaintenanceFindingId = entry.MaintenanceFindingId,
            TargetKind = entry.TargetKind,
            TargetIdentifier = entry.TargetIdentifier,
            Outcome = outcome,
            Message = message
        };

    private static void AddMaintenanceCleanupResult(
        MaintenanceCleanupJobResult result,
        MaintenanceCleanupEntryResult entry)
    {
        result.Entries.Add(entry);
        switch (entry.Outcome)
        {
            case "Cleaned":
                result.CleanedCount++;
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

    private static IEnumerable<string> EnumerateHostAgentInstallDirectories(string installRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(installRoot, "HostAgent", SearchOption.TopDirectoryOnly))
        {
            yield return directory;
        }

        foreach (var directory in Directory.EnumerateDirectories(installRoot, "HostAgent-*", SearchOption.TopDirectoryOnly))
        {
            yield return directory;
        }
    }

    private static bool TryResolveMaintenanceDirectory(
        MaintenanceFindingAction? action,
        out string directory,
        out string error)
    {
        directory = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(action?.Path) || string.IsNullOrWhiteSpace(action.InstallRoot))
        {
            error = "The cleanup action does not contain both a directory path and an install root.";
            return false;
        }

        string installRoot;
        string candidate;
        try
        {
            installRoot = Path.GetFullPath(action.InstallRoot.Trim());
            candidate = Path.GetFullPath(action.Path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            error = $"The cleanup directory path is invalid: {ex.Message}";
            return false;
        }

        if (!IsSameOrChildPath(installRoot, candidate))
        {
            error = $"Refusing to delete a directory outside the configured install root: {candidate}.";
            return false;
        }

        if (string.Equals(Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), candidate, GetPathComparison()))
        {
            error = "Refusing to delete the configured install root.";
            return false;
        }

        var folderName = Path.GetFileName(candidate);
        if (!string.Equals(folderName, "HostAgent", StringComparison.OrdinalIgnoreCase)
            && !folderName.StartsWith("HostAgent-", StringComparison.OrdinalIgnoreCase))
        {
            error = "Refusing to delete a directory that does not look like a HostAgent install directory.";
            return false;
        }

        directory = candidate;
        return true;
    }

    private static string ResolveHostAgentInstallRoot(HostAgentSettings settings)
    {
        var root = FirstNonEmpty(
            settings.SelfUpgrade.InstallRoot,
            settings.ServicesRoot,
            Path.GetDirectoryName(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            AppContext.BaseDirectory);

        return Path.GetFullPath(root);
    }

    private static string ResolveServiceNamePrefixForMaintenance(
        HostAgentSettings settings,
        string serviceName)
    {
        var prefix = FirstNonEmpty(settings.SelfUpgrade.ServiceNamePrefix, TrimTrailingVersion(serviceName), serviceName);
        return prefix.Trim().TrimEnd('.');
    }

    private static string TrimTrailingVersion(string serviceName)
    {
        var trimmed = serviceName.Trim();
        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return trimmed;
        }

        var suffixStart = parts.Length;
        while (suffixStart > 0 && parts[suffixStart - 1].All(char.IsDigit))
        {
            suffixStart--;
        }

        return suffixStart == parts.Length
            ? trimmed
            : string.Join('.', parts.Take(Math.Max(1, suffixStart)));
    }

    private static IReadOnlyList<HostAgentServiceCandidate> EnumerateHostAgentServices(string serviceNamePrefix)
    {
        var result = RunSc("queryex", "type=", "service", "state=", "all");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe failed with exit code {result.ExitCode}: {result.CombinedOutput.Trim()}");
        }

        var prefixes = GetKnownHostAgentServiceNamePrefixes(serviceNamePrefix);
        var serviceNames = result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["SERVICE_NAME:".Length..].Trim())
            .Where(name => IsHostAgentServiceName(name, prefixes))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return serviceNames
            .Select(name => new HostAgentServiceCandidate(name, GetServiceState(name), TryGetServiceExecutablePath(name)))
            .ToArray();
    }

    private static IReadOnlySet<string> GetKnownHostAgentServiceNamePrefixes(string serviceNamePrefix)
    {
        var prefixes = new HashSet<string>(KnownHostAgentServiceNamePrefixes, StringComparer.OrdinalIgnoreCase);
        var prefix = serviceNamePrefix.Trim().TrimEnd('.');
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            prefixes.Add(prefix);
        }

        return prefixes;
    }

    private static bool IsHostAgentServiceName(string serviceName, IEnumerable<string> serviceNamePrefixes)
        => serviceNamePrefixes.Any(prefix =>
            string.Equals(serviceName, prefix, StringComparison.OrdinalIgnoreCase)
            || serviceName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    private static string? GetServiceState(string serviceName)
    {
        var result = RunSc("query", serviceName);
        if (result.ExitCode != 0)
        {
            return IsServiceNotFound(result) ? null : throw new InvalidOperationException(result.CombinedOutput.Trim());
        }

        foreach (var line in result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var stateIndex = line.IndexOf("STATE", StringComparison.OrdinalIgnoreCase);
            if (stateIndex < 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':', stateIndex);
            if (separatorIndex < 0)
            {
                continue;
            }

            var parts = line[(separatorIndex + 1)..].Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0)
            {
                return parts[^1];
            }
        }

        return null;
    }

    private static string? TryGetServiceExecutablePath(string serviceName)
    {
        var result = RunSc("qc", serviceName);
        if (result.ExitCode != 0)
        {
            return null;
        }

        foreach (var line in result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var binaryPathIndex = line.IndexOf("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase);
            if (binaryPathIndex < 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':', binaryPathIndex);
            if (separatorIndex < 0)
            {
                continue;
            }

            return TryExtractExecutablePath(line[(separatorIndex + 1)..].Trim());
        }

        return null;
    }

    private static string? TryExtractExecutablePath(string binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return null;
        }

        var trimmed = binaryPath.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }

        var executableEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd < 0 ? null : trimmed[..(executableEnd + ".exe".Length)].Trim();
    }

    private static void DeleteDirectoryWithRetry(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < DirectoryDeleteMaxAttempts)
            {
                WaitBeforeDirectoryDeleteRetry(cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < DirectoryDeleteMaxAttempts)
            {
                WaitBeforeDirectoryDeleteRetry(cancellationToken);
            }
        }
    }

    private static void WaitBeforeDirectoryDeleteRetry(CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(DirectoryDeleteRetryDelay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static IEnumerable<string> GetCredentialStoreProtectedDirectories(HostAgentSettings settings)
    {
        var credentialStorePath = settings.CredentialStore.ResolveFilePath();
        if (string.IsNullOrWhiteSpace(credentialStorePath))
        {
            yield break;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(credentialStorePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            yield return directory;
        }
    }

    private static ScResult RunSc(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "sc.exe"),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start sc.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ScResult(process.ExitCode, output, error);
    }

    private static bool IsServiceNotFound(ScResult result)
        => result.ExitCode == ScServiceNotFoundExitCode
            || result.CombinedOutput.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase)
            || result.CombinedOutput.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
            ?? throw new InvalidOperationException("A required HostAgent maintenance value was not configured.");

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

    private sealed record HostAgentServiceCandidate(
        string Name,
        string? State,
        string? ExecutablePath);

    private sealed record ScResult(
        int ExitCode,
        string Output,
        string Error)
    {
        public string CombinedOutput => string.Concat(Output, Error);
    }
}
