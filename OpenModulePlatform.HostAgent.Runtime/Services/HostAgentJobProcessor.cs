using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class HostAgentJobProcessor
{
    private const int DirectoryDeleteMaxAttempts = 20;
    private const int MaxServiceAppDeploymentsForOrphanScan = 10000;
    private const int MaxOrphanHostCandidates = 10000;
    private const string OrphanServiceAppFindingCategory = "OrphanServiceApp";
    private static readonly TimeSpan DirectoryDeleteRetryDelay = TimeSpan.FromMilliseconds(500);
    // Keep legacy branded prefixes so upgrade and cleanup logic can recognize
    // older installs without exposing any customer-specific configuration.
    private static readonly string[] KnownHostAgentServiceNamePrefixes =
    [
        "EMP.HostAgent",
        "OMP.HostAgent",
        "OpenModulePlatform.HostAgent"
    ];
    private static readonly string[] KnownWorkerManagerServiceNamePrefixes =
    [
        "EMP.WorkerManager",
        "OMP.WorkerManager",
        "OpenModulePlatform.WorkerManager"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly WebAppHealthMonitor _webAppHealthMonitor;
    private readonly ILogger<HostAgentJobProcessor> _logger;

    public HostAgentJobProcessor(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        WebAppHealthMonitor webAppHealthMonitor,
        ILogger<HostAgentJobProcessor> logger)
    {
        _settings = settings;
        _repository = repository;
        _webAppHealthMonitor = webAppHealthMonitor;
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

            await ProcessJobAsync(job, hostKey, serviceName, leaseSeconds, cancellationToken);
        }
    }

    private async Task ProcessJobAsync(
        HostAgentJobWorkItem job,
        string hostKey,
        string serviceName,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseRenewal = RenewJobLeaseUntilProcessingCompletesAsync(
            job,
            leaseSeconds,
            processingCancellation,
            cancellationToken);

        try
        {
            switch (job.JobType)
            {
                case HostAgentJobTypes.ArtifactRetentionCleanup:
                    await ProcessArtifactRetentionCleanupJobAsync(job, processingCancellation.Token);
                    break;

                case HostAgentJobTypes.ArtifactCacheCleanup:
                    await ProcessArtifactCacheCleanupJobAsync(job, processingCancellation.Token);
                    break;

                case HostAgentJobTypes.ArtifactStoreCleanup:
                    await ProcessArtifactStoreCleanupJobAsync(job, processingCancellation.Token);
                    break;

                case HostAgentJobTypes.MaintenanceScan:
                    await ProcessMaintenanceScanJobAsync(job, hostKey, serviceName, processingCancellation.Token);
                    break;

                case HostAgentJobTypes.MaintenanceCleanup:
                    await ProcessMaintenanceCleanupJobAsync(job, hostKey, processingCancellation.Token);
                    break;

                case HostAgentJobTypes.WebAppHealthProbe:
                    await ProcessWebAppHealthProbeJobAsync(job, processingCancellation.Token);
                    break;

                case HostAgentJobTypes.RecycleWebAppAppPool:
                    await ProcessRecycleWebAppAppPoolJobAsync(job, processingCancellation.Token);
                    break;

                case HostAgentJobTypes.CollectWebAppLogs:
                    await ProcessCollectWebAppLogsJobAsync(job, processingCancellation.Token);
                    break;

                default:
                    await CompleteFailedAsync(
                        job,
                        $"Unsupported HostAgent job type '{job.JobType}'.",
                        processingCancellation.Token);
                    break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && processingCancellation.IsCancellationRequested)
        {
            _logger.LogWarning(
                "HostAgent job processing stopped because the job lease is no longer owned by this process. HostAgentJobId={HostAgentJobId}, JobType={JobType}",
                job.HostAgentJobId,
                job.JobType);

            await TryMarkJobFailedAfterCancellationAsync(
                job,
                "HostAgent job lease was lost during processing.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "HostAgent job processing stopped because HostAgent is shutting down. HostAgentJobId={HostAgentJobId}, JobType={JobType}",
                job.HostAgentJobId,
                job.JobType);

            await TryMarkJobFailedAfterCancellationAsync(
                job,
                "HostAgent stopped before the job completed.");

            throw;
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
        finally
        {
            await StopJobLeaseRenewalAsync(processingCancellation, leaseRenewal);
        }
    }

    private async Task RenewJobLeaseUntilProcessingCompletesAsync(
        HostAgentJobWorkItem job,
        int leaseSeconds,
        CancellationTokenSource processingCancellation,
        CancellationToken hostAgentCancellationToken)
    {
        var renewalInterval = TimeSpan.FromSeconds(Math.Clamp(leaseSeconds / 3, 30, 120));
        while (!processingCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(renewalInterval, processingCancellation.Token);
                var renewed = await _repository.RenewHostAgentJobLeaseAsync(
                    job.HostAgentJobId,
                    job.LeaseToken,
                    leaseSeconds,
                    processingCancellation.Token);

                if (!renewed)
                {
                    _logger.LogWarning(
                        "HostAgent job lease renewal did not update a running job row. Cancelling local processing. HostAgentJobId={HostAgentJobId}, JobType={JobType}",
                        job.HostAgentJobId,
                        job.JobType);
                    await processingCancellation.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (hostAgentCancellationToken.IsCancellationRequested || processingCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsExpectedLeaseRenewalFailure(ex))
            {
                // Lease renewal is best-effort: log transient repository/SQL failures
                // and retry on the next renewal interval while the job still runs.
                _logger.LogWarning(
                    ex,
                    "HostAgent job lease renewal failed. The next renewal attempt will retry while the job is still running. HostAgentJobId={HostAgentJobId}, JobType={JobType}",
                    job.HostAgentJobId,
                    job.JobType);
            }
        }
    }

    private static bool IsExpectedLeaseRenewalFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or DbException
            or UnauthorizedAccessException
            or TimeoutException;

    private static async Task StopJobLeaseRenewalAsync(
        CancellationTokenSource processingCancellation,
        Task leaseRenewal)
    {
        await processingCancellation.CancelAsync();
        try
        {
            await leaseRenewal;
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }

    private async Task ProcessWebAppHealthProbeJobAsync(
        HostAgentJobWorkItem job,
        CancellationToken cancellationToken)
    {
        if (!job.HostId.HasValue)
        {
            await CompleteFailedAsync(
                job,
                "Web app health probe jobs must target a specific host.",
                cancellationToken);
            return;
        }

        var payload = string.IsNullOrWhiteSpace(job.PayloadJson)
            ? new WebAppHealthProbeJobPayload()
            : JsonSerializer.Deserialize<WebAppHealthProbeJobPayload>(job.PayloadJson, JsonOptions)
                ?? new WebAppHealthProbeJobPayload();

        var probe = await _webAppHealthMonitor.ProbePortalAsync(
            job.HostId.Value,
            payload.RecycleIfUnhealthy,
            cancellationToken);
        if (probe is null)
        {
            await CompleteFailedAsync(
                job,
                "Portal health monitoring is disabled for this HostAgent.",
                cancellationToken);
            return;
        }

        var result = new WebAppHealthProbeJobResult
        {
            HealthKey = probe.HealthKey,
            ProbeUrl = probe.ProbeUrl,
            Status = probe.Status,
            HttpStatusCode = probe.HttpStatusCode,
            ConsecutiveFailures = probe.ConsecutiveFailures,
            Message = probe.IsHealthy ? probe.ResponseSummary : probe.Error,
            RecycledAppPool = payload.RecycleIfUnhealthy && !probe.IsHealthy
        };

        await _repository.CompleteHostAgentJobAsync(
            job.HostAgentJobId,
            job.LeaseToken,
            probe.IsHealthy ? HostAgentJobStatuses.Succeeded : HostAgentJobStatuses.Warning,
            JsonSerializer.Serialize(result, JsonOptions),
            probe.IsHealthy ? null : probe.Error,
            cancellationToken);
    }

    private async Task ProcessRecycleWebAppAppPoolJobAsync(
        HostAgentJobWorkItem job,
        CancellationToken cancellationToken)
    {
        if (!job.HostId.HasValue)
        {
            await CompleteFailedAsync(
                job,
                "Web app application-pool recycle jobs must target a specific host.",
                cancellationToken);
            return;
        }

        var payload = string.IsNullOrWhiteSpace(job.PayloadJson)
            ? new RecycleWebAppAppPoolJobPayload()
            : JsonSerializer.Deserialize<RecycleWebAppAppPoolJobPayload>(job.PayloadJson, JsonOptions)
                ?? new RecycleWebAppAppPoolJobPayload();

        var result = await _webAppHealthMonitor.RecyclePortalAppPoolAsync(
            job.HostId.Value,
            payload.HealthKey,
            payload.AppPoolName,
            cancellationToken);

        await _repository.CompleteHostAgentJobAsync(
            job.HostAgentJobId,
            job.LeaseToken,
            HostAgentJobStatuses.Succeeded,
            JsonSerializer.Serialize(result, JsonOptions),
            null,
            cancellationToken);
    }

    private async Task ProcessCollectWebAppLogsJobAsync(
        HostAgentJobWorkItem job,
        CancellationToken cancellationToken)
    {
        if (!job.HostId.HasValue)
        {
            await CompleteFailedAsync(
                job,
                "Web app log collection jobs must target a specific host.",
                cancellationToken);
            return;
        }

        var payload = string.IsNullOrWhiteSpace(job.PayloadJson)
            ? new CollectWebAppLogsJobPayload()
            : JsonSerializer.Deserialize<CollectWebAppLogsJobPayload>(job.PayloadJson, JsonOptions)
                ?? new CollectWebAppLogsJobPayload();
        var result = _webAppHealthMonitor.CollectPortalLogTail(payload.HealthKey, payload.MaxLines);
        var status = string.IsNullOrWhiteSpace(result.Content)
            ? HostAgentJobStatuses.Warning
            : HostAgentJobStatuses.Succeeded;

        await _repository.CompleteHostAgentJobAsync(
            job.HostAgentJobId,
            job.LeaseToken,
            status,
            JsonSerializer.Serialize(result, JsonOptions),
            status == HostAgentJobStatuses.Succeeded ? null : result.Message,
            cancellationToken);
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
                cancellationToken).ToList();
            findings.AddRange(BuildWorkerManagerLeftoverServiceFindings(
                job.HostId.Value,
                hostKey,
                cancellationToken));
            findings.AddRange(await BuildOrphanServiceAppFindings(
                job.HostId.Value,
                hostKey,
                _settings.CurrentValue,
                cancellationToken));
            findings.AddRange(await BuildOrphanHostFindings(
                job.HostId.Value,
                hostKey,
                cancellationToken));

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
            job.LeaseToken,
            HostAgentJobStatuses.Succeeded,
            JsonSerializer.Serialize(result, JsonOptions),
            null,
            cancellationToken);
    }

    private async Task ProcessMaintenanceCleanupJobAsync(
        HostAgentJobWorkItem job,
        string hostKey,
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
            var entryResult = await ProcessMaintenanceCleanupEntryAsync(entry, job.HostAgentJobId, hostKey, cancellationToken);
            AddMaintenanceCleanupResult(result, entryResult);
        }

        var status = result.ErrorCount > 0
            ? HostAgentJobStatuses.Warning
            : HostAgentJobStatuses.Succeeded;

        await _repository.CompleteHostAgentJobAsync(
            job.HostAgentJobId,
            job.LeaseToken,
            status,
            JsonSerializer.Serialize(result, JsonOptions),
            result.ErrorCount > 0 ? "One or more maintenance findings could not be cleaned." : null,
            cancellationToken);
    }

    private async Task<MaintenanceCleanupEntryResult> ProcessMaintenanceCleanupEntryAsync(
        MaintenanceFindingCleanupEntry entry,
        long hostAgentJobId,
        string hostKey,
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
                result = await CleanupWindowsServiceFindingAsync(entry, action, hostKey, cancellationToken);
                break;
            case MaintenanceTargetKinds.Directory:
                result = string.Equals(entry.Category, OrphanServiceAppFindingCategory, StringComparison.OrdinalIgnoreCase)
                    ? await CleanupOrphanServiceAppDirectoryFindingAsync(entry, action, hostKey, cancellationToken)
                    : CleanupDirectoryFinding(entry, action, cancellationToken);
                break;
            case MaintenanceTargetKinds.DatabaseRow:
                result = await CleanupDatabaseRowFindingAsync(entry, action, cancellationToken);
                break;
            case MaintenanceTargetKinds.OrphanHost:
                result = await CleanupOrphanHostFindingAsync(entry, action, cancellationToken);
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
            job.LeaseToken,
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
            job.LeaseToken,
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
            job.LeaseToken,
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
                DeleteDirectoryWithRetry(localPath, cancellationToken);
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
                DeleteDirectoryWithRetry(storePath, cancellationToken);
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

    private async Task<IReadOnlyList<MaintenanceFindingUpsert>> BuildOrphanServiceAppFindings(
        Guid hostId,
        string hostKey,
        HostAgentSettings settings,
        CancellationToken cancellationToken)
    {
        var deployments = await _repository.GetDesiredServiceAppDeploymentsAsync(
            hostKey,
            MaxServiceAppDeploymentsForOrphanScan,
            cancellationToken);

        return BuildOrphanServiceAppFindingsCore(
            hostId,
            hostKey,
            settings,
            deployments,
            serviceLookup: null,
            EnumerateServiceAppServices(settings),
            cancellationToken);
    }

    internal static IReadOnlyList<MaintenanceFindingUpsert> BuildOrphanServiceAppFindingsCore(
        Guid hostId,
        string hostKey,
        HostAgentSettings settings,
        IReadOnlyList<ServiceAppDeploymentDescriptor> activeDeployments,
        Func<string, (string? State, string? ExecutablePath)?>? serviceLookup,
        CancellationToken cancellationToken)
        => BuildOrphanServiceAppFindingsCore(
            hostId,
            hostKey,
            settings,
            activeDeployments,
            serviceLookup,
            serviceAppServiceCandidates: null,
            cancellationToken);

    internal static IReadOnlyList<MaintenanceFindingUpsert> BuildOrphanServiceAppFindingsCore(
        Guid hostId,
        string hostKey,
        HostAgentSettings settings,
        IReadOnlyList<ServiceAppDeploymentDescriptor> activeDeployments,
        Func<string, (string? State, string? ExecutablePath)?>? serviceLookup,
        IReadOnlyList<ServiceAppServiceCandidate>? serviceAppServiceCandidates,
        CancellationToken cancellationToken)
    {
        var findings = new List<MaintenanceFindingUpsert>();

        if (string.IsNullOrWhiteSpace(settings.ServicesRoot))
        {
            return findings;
        }

        var servicesRoot = Path.GetFullPath(settings.ServicesRoot.Trim());
        var hostAgentInstallRoot = Path.GetFullPath(ResolveHostAgentInstallRoot(settings))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var expectedTargetPaths = new HashSet<string>(GetPathComparer());
        var expectedServiceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var deployment in activeDeployments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var serviceName = ResolveExpectedServiceAppServiceName(deployment);
            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                expectedServiceNames.Add(serviceName);
            }

            var targetPath = ResolveExpectedServiceAppTargetPath(settings, deployment, serviceName);
            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                expectedTargetPaths.Add(
                    Path.GetFullPath(targetPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }

        if (!Directory.Exists(servicesRoot))
        {
            return findings;
        }

        foreach (var directory in Directory.EnumerateDirectories(servicesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullDirectory, hostAgentInstallRoot, GetPathComparison()))
            {
                continue;
            }

            if (expectedTargetPaths.Contains(fullDirectory))
            {
                continue;
            }

            var folderName = Path.GetFileName(fullDirectory);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                continue;
            }

            // Extra safety: never flag WorkerManager services or directories, and never flag
            // a directory that shares the active HostAgent service name.
            if (IsServiceNameWithKnownPrefix(folderName, KnownWorkerManagerServiceNamePrefixes)
                || string.Equals(folderName, settings.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (serviceState, serviceExecutablePath) = LookupServiceCandidate(
                folderName,
                serviceLookup);

            // A running service under this directory is a strong signal the folder is still
            // owned by an active deployment that was not captured above. Skip it entirely.
            if (!string.IsNullOrWhiteSpace(serviceState)
                && string.Equals(serviceState, "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasStoppedService = false;
            if (!string.IsNullOrWhiteSpace(serviceState)
                && !string.IsNullOrWhiteSpace(serviceExecutablePath))
            {
                var fullExecutablePath = Path.GetFullPath(serviceExecutablePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (IsSameOrChildPath(fullDirectory, fullExecutablePath))
                {
                    hasStoppedService = true;
                }
            }

            var directoryDetail =
                $"Host '{hostKey}' has a service-app directory '{fullDirectory}' that is not owned by any active enabled AppInstance.";
            var directoryAction = JsonSerializer.Serialize(new MaintenanceFindingAction
            {
                TargetKind = MaintenanceTargetKinds.Directory,
                HostId = hostId,
                Path = fullDirectory,
                InstallRoot = servicesRoot
            }, JsonOptions);

            findings.Add(new MaintenanceFindingUpsert
            {
                FindingKey = $"orphan-serviceapp-directory:{hostId:D}:{fullDirectory}",
                Scope = MaintenanceScanScopes.Host,
                HostId = hostId,
                Category = "OrphanServiceApp",
                TargetKind = MaintenanceTargetKinds.Directory,
                TargetIdentifier = fullDirectory,
                Title = "Orphan service-app directory",
                Detail = directoryDetail,
                RecommendedAction = "Remove the orphan service-app directory after confirming it is no longer needed.",
                SafetyNotes = "The directory is not owned by any active enabled AppInstance on this host, is not the HostAgent install directory, and is not a WorkerManager directory.",
                ActionJson = directoryAction,
                Severity = 2,
                Confidence = hasStoppedService ? (byte)90 : (byte)80
            });
        }

        if (serviceAppServiceCandidates is not null)
        {
            findings.AddRange(BuildOrphanServiceAppServiceFindings(
                hostId,
                hostKey,
                settings,
                activeDeployments,
                expectedServiceNames,
                expectedTargetPaths,
                serviceAppServiceCandidates,
                cancellationToken));
        }

        return findings;
    }

    private async Task<IReadOnlyList<MaintenanceFindingUpsert>> BuildOrphanHostFindings(
        Guid hostId,
        string hostKey,
        CancellationToken cancellationToken)
    {
        var candidates = await _repository.GetOrphanHostCandidatesAsync(
            hostId,
            MaxOrphanHostCandidates,
            cancellationToken);

        var findings = new List<MaintenanceFindingUpsert>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var action = JsonSerializer.Serialize(new MaintenanceFindingAction
            {
                SchemaVersion = 1,
                TargetKind = MaintenanceTargetKinds.OrphanHost,
                HostId = candidate.HostId
            }, JsonOptions);

            findings.Add(new MaintenanceFindingUpsert
            {
                FindingKey = $"orphan-host:{candidate.HostId:D}",
                Scope = MaintenanceScanScopes.Host,
                HostId = candidate.HostId,
                Category = "OrphanHost",
                TargetKind = MaintenanceTargetKinds.OrphanHost,
                TargetIdentifier = candidate.HostKey,
                Title = "Orphan host row",
                Detail = $"Host '{candidate.HostKey}' appears to be an orphan. Its Environment is null, its InstanceId '{candidate.InstanceId:D}' does not belong to an active installation, and the host has no desired apps or runtime deployment states. Leftover artifact requirements or states may be present and will be removed by the human-gated cleanup.",
                RecommendedAction = $"Review host '{candidate.HostKey}'; if confirmed orphan, delete via maintenance cleanup.",
                SafetyNotes = "This finding is detect-only and never triggers automatic deletion. Verify the host is truly unused before removing it via the separate human-gated MaintenanceCleanup job.",
                ActionJson = action,
                Severity = 2,
                Confidence = 85
            });
        }

        return findings;
    }

    private static string? ResolveExpectedServiceAppServiceName(ServiceAppDeploymentDescriptor deployment)
    {
        var runtimeName = ServiceAppDeploymentNaming.Clean(deployment.DeployedRuntimeName);
        if (!string.IsNullOrWhiteSpace(runtimeName))
        {
            return runtimeName;
        }

        var installationName = ServiceAppDeploymentNaming.Clean(deployment.InstallationName);
        if (!string.IsNullOrWhiteSpace(installationName)
            && !ServiceAppDeploymentNaming.IsGenericInstallationName(installationName))
        {
            return installationName;
        }

        return null;
    }

    private static string? ResolveExpectedServiceAppTargetPath(
        HostAgentSettings settings,
        ServiceAppDeploymentDescriptor deployment,
        string? serviceName)
    {
        var deployedTargetPath = ServiceAppDeploymentNaming.Clean(deployment.DeployedTargetPath);
        if (!string.IsNullOrWhiteSpace(deployedTargetPath))
        {
            return deployedTargetPath;
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }

        return ServiceAppDeploymentNaming.ResolveTargetPath(settings, deployment, serviceName);
    }

    private static (string? State, string? ExecutablePath) LookupServiceCandidate(
        string serviceName,
        Func<string, (string? State, string? ExecutablePath)?>? serviceLookup)
    {
        if (serviceLookup is not null)
        {
            return serviceLookup(serviceName) ?? (null, null);
        }

        if (IsServiceNameWithKnownPrefix(serviceName, KnownWorkerManagerServiceNamePrefixes)
            || IsServiceNameWithKnownPrefix(serviceName, KnownHostAgentServiceNamePrefixes))
        {
            return (null, null);
        }

        var state = GetServiceState(serviceName);
        if (state is null)
        {
            return (null, null);
        }

        var executablePath = TryGetServiceExecutablePath(serviceName);
        return (state, executablePath);
    }

    private static IReadOnlyList<MaintenanceFindingUpsert> BuildWorkerManagerLeftoverServiceFindings(
        Guid hostId,
        string hostKey,
        CancellationToken cancellationToken)
    {
        var findings = new List<MaintenanceFindingUpsert>();
        var services = EnumerateWorkerManagerServices();
        var runningServices = services
            .Where(static service =>
                string.Equals(service.State, "RUNNING", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(service.ExecutablePath))
            .ToArray();

        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(service.State, "RUNNING", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(service.ExecutablePath))
            {
                continue;
            }

            var executablePath = Path.GetFullPath(service.ExecutablePath);
            var activeService = runningServices.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.ExecutablePath)
                && string.Equals(
                    Path.GetFullPath(candidate.ExecutablePath),
                    executablePath,
                    GetPathComparison()));
            if (activeService is null)
            {
                continue;
            }

            var detail =
                $"Host '{hostKey}' has a stopped WorkerManager service '{service.Name}' in state '{service.State ?? "unknown"}'. It points to the same executable as running WorkerManager service '{activeService.Name}': {executablePath}.";
            var action = JsonSerializer.Serialize(new MaintenanceFindingAction
            {
                TargetKind = MaintenanceTargetKinds.WindowsService,
                HostId = hostId,
                ServiceName = service.Name
            }, JsonOptions);

            findings.Add(new MaintenanceFindingUpsert
            {
                FindingKey = $"workermanager-service:{hostId:D}:{service.Name}",
                Scope = MaintenanceScanScopes.Host,
                HostId = hostId,
                Category = "WorkerManagerLeftover",
                TargetKind = MaintenanceTargetKinds.WindowsService,
                TargetIdentifier = service.Name,
                Title = "Stopped old WorkerManager service",
                Detail = detail,
                RecommendedAction = "Delete the stopped old WorkerManager Windows service.",
                SafetyNotes = "The service name matches a known WorkerManager prefix, is not running, and another WorkerManager service is running from the same executable path.",
                ActionJson = action,
                Severity = 2,
                Confidence = 90
            });
        }

        return findings;
    }

    private async Task<MaintenanceCleanupEntryResult> CleanupWindowsServiceFindingAsync(
        MaintenanceFindingCleanupEntry entry,
        MaintenanceFindingAction? action,
        string hostKey,
        CancellationToken cancellationToken)
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

        var canonicalServiceName = ServiceAppDeploymentNaming.Clean(action?.CanonicalServiceName);
        if (!string.IsNullOrWhiteSpace(canonicalServiceName))
        {
            return await CleanupOrphanDuplicateServiceFindingAsync(
                entry,
                hostKey,
                serviceName,
                state,
                canonicalServiceName,
                cancellationToken);
        }

        if (string.Equals(state, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Skipped", "Refusing to delete a running Windows service.");
        }

        var result = RunSc("delete", serviceName);
        if (result.ExitCode == 0 || result.IsServiceNotFound())
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Cleaned", $"Deleted Windows service '{serviceName}'.");
        }

        if (result.IsServiceMarkedForDeletion())
        {
            return CreateMaintenanceCleanupEntryResult(
                entry,
                "Cleaned",
                $"Windows service '{serviceName}' is already marked for deletion. Windows will remove it after all service handles are released, or after the next reboot.");
        }

        return CreateMaintenanceCleanupEntryResult(
            entry,
            "Error",
            $"sc.exe delete failed with exit code {result.ExitCode}: {result.CombinedOutput.Trim()}");
    }

    /// <summary>
    /// Deletes a confirmed orphan-duplicate ("legacy twin") service. Unlike the generic
    /// cleanup path, a running duplicate may be stopped and deleted here, but only when
    /// every guardrail in <see cref="TryCleanupOrphanDuplicateService"/> passes.
    /// </summary>
    private async Task<MaintenanceCleanupEntryResult> CleanupOrphanDuplicateServiceFindingAsync(
        MaintenanceFindingCleanupEntry entry,
        string hostKey,
        string serviceName,
        string? serviceState,
        string canonicalServiceName,
        CancellationToken cancellationToken)
    {
        var deployments = await _repository.GetDesiredServiceAppDeploymentsAsync(
            hostKey,
            MaxServiceAppDeploymentsForOrphanScan,
            cancellationToken);

        var claimedServiceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var deployment in deployments)
        {
            var resolvedName = ResolveExpectedServiceAppServiceName(deployment);
            if (!string.IsNullOrWhiteSpace(resolvedName))
            {
                claimedServiceNames.Add(resolvedName);
            }
        }

        var canonicalState = GetServiceState(canonicalServiceName);
        var serviceExecutablePath = TryGetServiceExecutablePath(serviceName);
        var canonicalExecutablePath = TryGetServiceExecutablePath(canonicalServiceName);

        bool deleted;
        string? refusalReason;
        try
        {
            deleted = TryCleanupOrphanDuplicateService(
                WindowsServiceControl.Instance,
                _settings.CurrentValue,
                serviceName,
                serviceState,
                serviceExecutablePath,
                canonicalServiceName,
                canonicalState,
                canonicalExecutablePath,
                claimedServiceNames,
                out refusalReason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateMaintenanceCleanupEntryResult(
                entry,
                "Error",
                $"Failed to stop and delete duplicate Windows service '{serviceName}': {ex.Message}");
        }

        if (!deleted)
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Skipped", refusalReason);
        }

        return CreateMaintenanceCleanupEntryResult(
            entry,
            "Cleaned",
            $"Stopped and deleted duplicate Windows service '{serviceName}'. The claimed canonical service '{canonicalServiceName}' remains in place.");
    }

    /// <summary>
    /// Guardrailed stop-and-delete of an orphan-duplicate service. The duplicate is
    /// only deleted when (a) the claimed canonical service for the same app remains
    /// running, (b) the duplicate is unambiguously orphan (not claimed by any active
    /// enabled AppInstance and pointing to the same executable file name as the
    /// canonical service), and (c) neither the duplicate nor the canonical service is
    /// the active HostAgent or a WorkerManager service. Returns true when the service
    /// was stopped and deleted via <paramref name="serviceControl"/>.
    /// </summary>
    internal static bool TryCleanupOrphanDuplicateService(
        IWindowsServiceControl serviceControl,
        HostAgentSettings settings,
        string serviceName,
        string? serviceState,
        string? serviceExecutablePath,
        string canonicalServiceName,
        string? canonicalState,
        string? canonicalExecutablePath,
        IReadOnlySet<string> claimedServiceNames,
        out string? refusalReason)
    {
        refusalReason = EvaluateOrphanDuplicateCleanup(
            settings,
            serviceName,
            serviceState,
            serviceExecutablePath,
            canonicalServiceName,
            canonicalState,
            canonicalExecutablePath,
            claimedServiceNames);
        if (refusalReason is not null)
        {
            return false;
        }

        // DeleteService stops the service first when it is running, then deletes it.
        serviceControl.DeleteService(serviceName);
        return true;
    }

    private static string? EvaluateOrphanDuplicateCleanup(
        HostAgentSettings settings,
        string serviceName,
        string? serviceState,
        string? serviceExecutablePath,
        string canonicalServiceName,
        string? canonicalState,
        string? canonicalExecutablePath,
        IReadOnlySet<string> claimedServiceNames)
    {
        if (string.Equals(serviceName, settings.ServiceName, StringComparison.OrdinalIgnoreCase)
            || IsServiceNameWithKnownPrefix(serviceName, KnownHostAgentServiceNamePrefixes))
        {
            return $"Refusing to delete '{serviceName}': it is a HostAgent service.";
        }

        if (IsServiceNameWithKnownPrefix(serviceName, KnownWorkerManagerServiceNamePrefixes))
        {
            return $"Refusing to delete '{serviceName}': it is a WorkerManager service.";
        }

        if (string.Equals(canonicalServiceName, serviceName, StringComparison.OrdinalIgnoreCase))
        {
            return $"Refusing to delete '{serviceName}': the canonical service name matches the duplicate target; the finding action is inconsistent.";
        }

        if (string.Equals(canonicalServiceName, settings.ServiceName, StringComparison.OrdinalIgnoreCase)
            || IsServiceNameWithKnownPrefix(canonicalServiceName, KnownHostAgentServiceNamePrefixes)
            || IsServiceNameWithKnownPrefix(canonicalServiceName, KnownWorkerManagerServiceNamePrefixes))
        {
            return $"Refusing to delete '{serviceName}': the canonical service '{canonicalServiceName}' is a HostAgent or WorkerManager service; the finding action is inconsistent.";
        }

        if (claimedServiceNames.Contains(serviceName))
        {
            return $"Refusing to delete '{serviceName}': it is still claimed by an active enabled AppInstance. Disable or remove that AppInstance first.";
        }

        if (!claimedServiceNames.Contains(canonicalServiceName))
        {
            return $"Refusing to delete '{serviceName}': the canonical service '{canonicalServiceName}' is not claimed by any active enabled AppInstance.";
        }

        if (canonicalState is null)
        {
            return $"Refusing to delete '{serviceName}': the canonical service '{canonicalServiceName}' is missing.";
        }

        if (!string.Equals(canonicalState, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return $"Refusing to delete '{serviceName}': the canonical service '{canonicalServiceName}' is not running (state '{canonicalState}').";
        }

        if (string.IsNullOrWhiteSpace(serviceExecutablePath) || string.IsNullOrWhiteSpace(canonicalExecutablePath))
        {
            return $"Refusing to delete '{serviceName}': the executable identity of the duplicate pair could not be confirmed.";
        }

        var serviceExeFileName = Path.GetFileName(serviceExecutablePath);
        var canonicalExeFileName = Path.GetFileName(canonicalExecutablePath);
        if (string.IsNullOrWhiteSpace(serviceExeFileName)
            || !string.Equals(serviceExeFileName, canonicalExeFileName, StringComparison.OrdinalIgnoreCase))
        {
            return $"Refusing to delete '{serviceName}': its executable does not match the canonical service executable.";
        }

        if (!ServiceAppDeploymentNaming.IsLegacyTwinServiceName(serviceName, canonicalServiceName, canonicalExecutablePath))
        {
            return $"Refusing to delete '{serviceName}': it is not an unambiguous legacy twin of '{canonicalServiceName}'.";
        }

        return null;
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

        return DeleteMaintenanceDirectoryWithGuards(entry, directory, cancellationToken);
    }

    private async Task<MaintenanceCleanupEntryResult> CleanupOrphanServiceAppDirectoryFindingAsync(
        MaintenanceFindingCleanupEntry entry,
        MaintenanceFindingAction? action,
        string hostKey,
        CancellationToken cancellationToken)
    {
        if (!TryResolveMaintenanceDirectoryPath(action, out var directory, out var error))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Error", error);
        }

        if (!Directory.Exists(directory))
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Missing", "The directory was already missing.");
        }

        // Re-verify the finding-time safety conditions at cleanup time: the directory
        // must still be an unowned orphan below the configured services root.
        var settings = _settings.CurrentValue;
        var deployments = await _repository.GetDesiredServiceAppDeploymentsAsync(
            hostKey,
            MaxServiceAppDeploymentsForOrphanScan,
            cancellationToken);
        var serviceCandidates = EnumerateServiceAppServices(settings);

        var refusal = ValidateOrphanServiceAppDirectoryCleanup(settings, deployments, serviceCandidates, directory);
        if (refusal is not null)
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Skipped", refusal);
        }

        return DeleteMaintenanceDirectoryWithGuards(entry, directory, cancellationToken);
    }

    private MaintenanceCleanupEntryResult DeleteMaintenanceDirectoryWithGuards(
        MaintenanceFindingCleanupEntry entry,
        string directory,
        CancellationToken cancellationToken)
    {
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

    private async Task<MaintenanceCleanupEntryResult> CleanupOrphanHostFindingAsync(
        MaintenanceFindingCleanupEntry entry,
        MaintenanceFindingAction? action,
        CancellationToken cancellationToken)
    {
        if (action?.HostId is not { } hostId)
        {
            return CreateMaintenanceCleanupEntryResult(entry, "Error", "The cleanup action does not contain a host identity.");
        }

        var deletedRows = await _repository.DeleteOrphanHostAsync(
            hostId,
            cancellationToken);

        return deletedRows > 0
            ? CreateMaintenanceCleanupEntryResult(entry, "Cleaned", "Deleted orphan host and its dependent rows.")
            : CreateMaintenanceCleanupEntryResult(entry, "Missing", "The orphan host row was already missing.");
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
        if (!TryResolveMaintenanceDirectoryPath(action, out directory, out error))
        {
            return false;
        }

        var folderName = Path.GetFileName(directory);
        if (!string.Equals(folderName, "HostAgent", StringComparison.OrdinalIgnoreCase)
            && !folderName.StartsWith("HostAgent-", StringComparison.OrdinalIgnoreCase))
        {
            error = "Refusing to delete a directory that does not look like a HostAgent install directory.";
            directory = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryResolveMaintenanceDirectoryPath(
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

        var normalizedInstallRoot = installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedInstallRoot, candidate, GetPathComparison()))
        {
            error = "Refusing to delete the configured install root.";
            return false;
        }

        directory = candidate;
        return true;
    }

    /// <summary>
    /// Re-validates, at cleanup time, that an orphan service-app directory is still safe to
    /// delete. Returns <see langword="null"/> when deletion is allowed, otherwise a refusal
    /// message. Every finding-time safety condition from
    /// <see cref="BuildOrphanServiceAppFindingsCore(Guid, string, HostAgentSettings, IReadOnlyList{ServiceAppDeploymentDescriptor}, Func{string, (string? State, string? ExecutablePath)?}?, IReadOnlyList{ServiceAppServiceCandidate}?, CancellationToken)"/>
    /// is re-checked here against current state.
    /// </summary>
    internal static string? ValidateOrphanServiceAppDirectoryCleanup(
        HostAgentSettings settings,
        IReadOnlyList<ServiceAppDeploymentDescriptor> activeDeployments,
        IReadOnlyList<ServiceAppServiceCandidate> serviceAppServiceCandidates,
        string directory)
    {
        if (string.IsNullOrWhiteSpace(settings.ServicesRoot))
        {
            return "Refusing to delete the orphan service-app directory: no services root is configured.";
        }

        var servicesRoot = Path.GetFullPath(settings.ServicesRoot.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate;
        try
        {
            candidate = Path.GetFullPath(directory.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return $"Refusing to delete the orphan service-app directory: the path is invalid ({ex.Message}).";
        }

        if (!IsSameOrChildPath(servicesRoot, candidate)
            || string.Equals(servicesRoot, candidate, GetPathComparison()))
        {
            return $"Refusing to delete '{candidate}': it is not a child of the configured services root.";
        }

        var hostAgentInstallRoot = Path.GetFullPath(ResolveHostAgentInstallRoot(settings))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(hostAgentInstallRoot, candidate, GetPathComparison()))
        {
            return $"Refusing to delete '{candidate}': it is the HostAgent install directory.";
        }

        var folderName = Path.GetFileName(candidate);
        if (string.IsNullOrWhiteSpace(folderName)
            || IsServiceNameWithKnownPrefix(folderName, KnownWorkerManagerServiceNamePrefixes)
            || string.Equals(folderName, settings.ServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return $"Refusing to delete '{candidate}': the folder name matches a protected platform service.";
        }

        foreach (var deployment in activeDeployments)
        {
            var serviceName = ResolveExpectedServiceAppServiceName(deployment);
            var targetPath = ResolveExpectedServiceAppTargetPath(settings, deployment, serviceName);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                continue;
            }

            var expectedPath = Path.GetFullPath(targetPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(expectedPath, candidate, GetPathComparison()))
            {
                return $"Refusing to delete '{candidate}': it is now owned by active enabled AppInstance '{deployment.AppInstanceKey}'.";
            }
        }

        foreach (var service in serviceAppServiceCandidates)
        {
            if (string.IsNullOrWhiteSpace(service.ExecutablePath))
            {
                continue;
            }

            var executablePath = Path.GetFullPath(service.ExecutablePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsSameOrChildPath(candidate, executablePath))
            {
                return $"Refusing to delete '{candidate}' while Windows service '{service.Name}' still references it.";
            }
        }

        return null;
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
        var prefixes = GetKnownHostAgentServiceNamePrefixes(serviceNamePrefix);
        return EnumerateWindowsServicesByPrefix(prefixes);
    }

    private static IReadOnlyList<HostAgentServiceCandidate> EnumerateWorkerManagerServices()
        => EnumerateWindowsServicesByPrefix(KnownWorkerManagerServiceNamePrefixes);

    private static IReadOnlyList<HostAgentServiceCandidate> EnumerateWindowsServicesByPrefix(IEnumerable<string> serviceNamePrefixes)
    {
        var result = RunSc("queryex", "type=", "service", "state=", "all");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe failed with exit code {result.ExitCode}: {result.CombinedOutput.Trim()}");
        }

        var prefixes = serviceNamePrefixes
            .Select(static prefix => prefix.Trim().TrimEnd('.'))
            .Where(static prefix => !string.IsNullOrWhiteSpace(prefix))
            .ToArray();
        var serviceNames = result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["SERVICE_NAME:".Length..].Trim())
            .Where(name => IsServiceNameWithKnownPrefix(name, prefixes))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return serviceNames
            .Select(name => new HostAgentServiceCandidate(name, GetServiceState(name), TryGetServiceExecutablePath(name)))
            .ToArray();
    }

    private static IReadOnlyList<ServiceAppServiceCandidate> EnumerateServiceAppServices(HostAgentSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ServicesRoot))
        {
            return [];
        }

        var servicesRoot = Path.GetFullPath(settings.ServicesRoot.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hostAgentInstallRoot = Path.GetFullPath(ResolveHostAgentInstallRoot(settings))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var result = RunSc("queryex", "type=", "service", "state=", "all");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe failed with exit code {result.ExitCode}: {result.CombinedOutput.Trim()}");
        }

        var serviceNames = result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(line => line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["SERVICE_NAME:".Length..].Trim())
            .Where(name =>
                !IsServiceNameWithKnownPrefix(name, KnownHostAgentServiceNamePrefixes)
                && !IsServiceNameWithKnownPrefix(name, KnownWorkerManagerServiceNamePrefixes)
                && !string.Equals(name, settings.ServiceName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return serviceNames
            .Select(name => new ServiceAppServiceCandidate(
                name,
                GetServiceState(name),
                TryGetServiceExecutablePath(name),
                TryGetServiceDisplayName(name)))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ExecutablePath))
            .Select(candidate =>
            {
                var fullExecutablePath = Path.GetFullPath(candidate.ExecutablePath!)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Services outside the services root are kept as candidates so that a
                // legacy/unprefixed duplicate of an active OMP service can be detected
                // even when it was self-installed elsewhere. The general orphan sweep
                // still only considers services under the services root.
                var isUnderServicesRoot =
                    IsSameOrChildPath(servicesRoot, fullExecutablePath)
                    && !IsSameOrChildPath(hostAgentInstallRoot, fullExecutablePath);

                return candidate with { IsUnderServicesRoot = isUnderServicesRoot };
            })
            .ToArray();
    }

    private static string? TryGetServiceDisplayName(string serviceName)
    {
        var result = RunSc("qc", serviceName);
        if (result.ExitCode != 0)
        {
            return null;
        }

        foreach (var line in result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var displayNameIndex = line.IndexOf("DISPLAY_NAME", StringComparison.OrdinalIgnoreCase);
            if (displayNameIndex < 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':', displayNameIndex);
            if (separatorIndex < 0)
            {
                continue;
            }

            var displayName = line[(separatorIndex + 1)..].Trim();
            return string.IsNullOrWhiteSpace(displayName) ? null : displayName;
        }

        return null;
    }

    private static IReadOnlyList<MaintenanceFindingUpsert> BuildOrphanServiceAppServiceFindings(
        Guid hostId,
        string hostKey,
        HostAgentSettings settings,
        IReadOnlyList<ServiceAppDeploymentDescriptor> activeDeployments,
        IReadOnlySet<string> claimedServiceNames,
        IReadOnlySet<string> expectedTargetPaths,
        IReadOnlyList<ServiceAppServiceCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var findings = new List<MaintenanceFindingUpsert>();

        // Detect legacy/unprefixed duplicate ("twin") services of active claimed
        // services first, so the general orphan sweep does not double-report them.
        var duplicateTwinMatches = FindLegacyDuplicateTwinServices(
            settings,
            activeDeployments,
            claimedServiceNames,
            candidates);
        var duplicateTwinNames = new HashSet<string>(
            duplicateTwinMatches.Select(static match => match.Twin.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                continue;
            }

            if (IsServiceNameWithKnownPrefix(candidate.Name, KnownHostAgentServiceNamePrefixes)
                || IsServiceNameWithKnownPrefix(candidate.Name, KnownWorkerManagerServiceNamePrefixes)
                || string.Equals(candidate.Name, settings.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The general orphan sweep only considers services whose executable lives
            // under the services root. Outside-root services are only reported through
            // the duplicate-twin detection below.
            if (!candidate.IsUnderServicesRoot)
            {
                continue;
            }

            if (duplicateTwinNames.Contains(candidate.Name))
            {
                continue;
            }

            if (claimedServiceNames.Contains(candidate.Name))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(candidate.ExecutablePath))
            {
                var executableDirectory = Path.GetDirectoryName(
                    Path.GetFullPath(candidate.ExecutablePath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (!string.IsNullOrWhiteSpace(executableDirectory)
                    && expectedTargetPaths.Contains(executableDirectory))
                {
                    continue;
                }
            }

            var isRunning = string.Equals(candidate.State, "RUNNING", StringComparison.OrdinalIgnoreCase);
            var detail =
                $"Host '{hostKey}' has a Windows service '{candidate.Name}' in state '{candidate.State ?? "unknown"}' that is not owned by any active enabled AppInstance.";
            var action = JsonSerializer.Serialize(new MaintenanceFindingAction
            {
                TargetKind = MaintenanceTargetKinds.WindowsService,
                HostId = hostId,
                ServiceName = candidate.Name
            }, JsonOptions);

            findings.Add(new MaintenanceFindingUpsert
            {
                FindingKey = $"orphan-serviceapp-service:{hostId:D}:{candidate.Name}",
                Scope = MaintenanceScanScopes.Host,
                HostId = hostId,
                Category = "OrphanServiceApp",
                TargetKind = MaintenanceTargetKinds.WindowsService,
                TargetIdentifier = candidate.Name,
                Title = "Orphan service-app Windows service",
                Detail = detail,
                RecommendedAction = $"Review and delete the orphan Windows service '{candidate.Name}' after confirming it is no longer needed.",
                SafetyNotes = "The service executable is located under the configured service-apps root, and the service is not owned by any active enabled AppInstance, HostAgent, or WorkerManager. Cleanup is human-gated.",
                ActionJson = action,
                Severity = 2,
                Confidence = isRunning ? (byte)95 : (byte)90
            });
        }

        var duplicateDisplayNameGroups = candidates
            .Where(candidate => candidate.IsUnderServicesRoot && !string.IsNullOrWhiteSpace(candidate.DisplayName))
            .GroupBy(candidate => candidate.DisplayName!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();

        foreach (var group in duplicateDisplayNameGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var unclaimedCandidates = group
                .Where(candidate =>
                    !claimedServiceNames.Contains(candidate.Name)
                    && !IsServiceNameWithKnownPrefix(candidate.Name, KnownHostAgentServiceNamePrefixes)
                    && !IsServiceNameWithKnownPrefix(candidate.Name, KnownWorkerManagerServiceNamePrefixes)
                    && !string.Equals(candidate.Name, settings.ServiceName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (unclaimedCandidates.Length == 0)
            {
                continue;
            }

            var names = string.Join(", ", unclaimedCandidates.Select(candidate => $"'{candidate.Name}'"));

            foreach (var candidate in unclaimedCandidates)
            {
                var action = JsonSerializer.Serialize(new MaintenanceFindingAction
                {
                    TargetKind = MaintenanceTargetKinds.WindowsService,
                    HostId = hostId,
                    ServiceName = candidate.Name
                }, JsonOptions);

                findings.Add(new MaintenanceFindingUpsert
                {
                    FindingKey = $"orphan-serviceapp-duplicate-displayname:{hostId:D}:{candidate.Name}",
                    Scope = MaintenanceScanScopes.Host,
                    HostId = hostId,
                    Category = "OrphanServiceApp",
                    TargetKind = MaintenanceTargetKinds.WindowsService,
                    TargetIdentifier = candidate.Name,
                    Title = "Duplicate service-app display name",
                    Detail =
                        $"Host '{hostKey}' has multiple service-app services sharing the display name '{group.Key}': {names}. " +
                        $"At least one of them ('{candidate.Name}') is not owned by an active enabled AppInstance.",
                    RecommendedAction = $"Review the duplicate services and delete the orphan service '{candidate.Name}' after confirming it is no longer needed.",
                    SafetyNotes = "Duplicate display names usually indicate a rename or cross-instance upgrade that left an old service behind. Cleanup is human-gated.",
                    ActionJson = action,
                    Severity = 2,
                    Confidence = 95
                });
            }
        }

        foreach (var match in duplicateTwinMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var twin = match.Twin;
            var canonical = match.Canonical;
            var executableFileName = Path.GetFileName(canonical.ExecutablePath);
            var location = twin.IsUnderServicesRoot ? "under" : "outside";

            string detail;
            string recommendedAction;
            byte confidence;
            if (match.ClaimingDeployment is not null)
            {
                detail =
                    $"Host '{hostKey}' has a Windows service '{twin.Name}' in state '{twin.State ?? "unknown"}' ({location} the services root) that is a legacy/unprefixed duplicate of the claimed canonical service '{canonical.Name}' for the same app (same executable '{executableFileName}'). " +
                    $"The duplicate name is still claimed by enabled AppInstance '{match.ClaimingDeployment.AppInstanceKey}', so the app is deployed twice on this host.";
                recommendedAction =
                    $"Disable or remove the duplicate AppInstance '{match.ClaimingDeployment.AppInstanceKey}' in the Portal, then let the human-gated maintenance cleanup delete service '{twin.Name}'.";
                confidence = 80;
            }
            else
            {
                detail =
                    $"Host '{hostKey}' has a Windows service '{twin.Name}' in state '{twin.State ?? "unknown"}' ({location} the services root) that is a legacy/unprefixed duplicate of the claimed canonical service '{canonical.Name}' for the same app (same executable '{executableFileName}'). " +
                    "The duplicate is not owned by any active enabled AppInstance; it is likely left behind by a rename of AppInstances.InstallationName.";
                recommendedAction =
                    $"Confirm that the canonical service '{canonical.Name}' is running, then delete the duplicate service '{twin.Name}' via the human-gated maintenance cleanup.";
                confidence = 95;
            }

            var twinAction = JsonSerializer.Serialize(new MaintenanceFindingAction
            {
                TargetKind = MaintenanceTargetKinds.WindowsService,
                HostId = hostId,
                ServiceName = twin.Name,
                CanonicalServiceName = canonical.Name
            }, JsonOptions);

            findings.Add(new MaintenanceFindingUpsert
            {
                FindingKey = $"orphan-serviceapp-duplicate-twin:{hostId:D}:{twin.Name}",
                Scope = MaintenanceScanScopes.Host,
                HostId = hostId,
                Category = "OrphanServiceApp",
                TargetKind = MaintenanceTargetKinds.WindowsService,
                TargetIdentifier = twin.Name,
                Title = "Duplicate service-app service (legacy twin)",
                Detail = detail,
                RecommendedAction = recommendedAction,
                SafetyNotes =
                    "Cleanup is human-gated and only proceeds when the claimed canonical service for the same app remains running, the duplicate is no longer claimed by any active enabled AppInstance, both services point to the same executable file name, and the target is not the HostAgent or WorkerManager.",
                ActionJson = twinAction,
                Severity = 2,
                Confidence = confidence
            });
        }

        return findings;
    }

    /// <summary>
    /// Finds legacy/unprefixed "twin" services of active claimed service-app services:
    /// a different Windows service that points to the same executable file name as a
    /// claimed canonical service and whose name matches the legacy naming (the
    /// executable name, or the canonical name without its first prefix segment).
    /// Twins are matched regardless of whether their executable is under the services
    /// root, so self-installed legacy services outside the root are detected too.
    /// </summary>
    private static IReadOnlyList<LegacyDuplicateTwinMatch> FindLegacyDuplicateTwinServices(
        HostAgentSettings settings,
        IReadOnlyList<ServiceAppDeploymentDescriptor> activeDeployments,
        IReadOnlySet<string> claimedServiceNames,
        IReadOnlyList<ServiceAppServiceCandidate> candidates)
    {
        var deploymentsByServiceName = new Dictionary<string, ServiceAppDeploymentDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var deployment in activeDeployments)
        {
            var resolvedName = ResolveExpectedServiceAppServiceName(deployment);
            if (!string.IsNullOrWhiteSpace(resolvedName)
                && !deploymentsByServiceName.ContainsKey(resolvedName))
            {
                deploymentsByServiceName[resolvedName] = deployment;
            }
        }

        var canonicalCandidates = candidates
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Name)
                && !string.IsNullOrWhiteSpace(candidate.ExecutablePath)
                && claimedServiceNames.Contains(candidate.Name))
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var matches = new List<LegacyDuplicateTwinMatch>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Name)
                || string.IsNullOrWhiteSpace(candidate.ExecutablePath))
            {
                continue;
            }

            if (IsServiceNameWithKnownPrefix(candidate.Name, KnownHostAgentServiceNamePrefixes)
                || IsServiceNameWithKnownPrefix(candidate.Name, KnownWorkerManagerServiceNamePrefixes)
                || string.Equals(candidate.Name, settings.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidateExeFileName = Path.GetFileName(candidate.ExecutablePath);
            if (string.IsNullOrWhiteSpace(candidateExeFileName))
            {
                continue;
            }

            foreach (var canonical in canonicalCandidates)
            {
                if (string.Equals(canonical.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var canonicalExeFileName = Path.GetFileName(canonical.ExecutablePath);
                if (string.IsNullOrWhiteSpace(canonicalExeFileName)
                    || !string.Equals(candidateExeFileName, canonicalExeFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!ServiceAppDeploymentNaming.IsLegacyTwinServiceName(
                        candidate.Name,
                        canonical.Name,
                        canonical.ExecutablePath))
                {
                    continue;
                }

                // A twin still claimed by an active AppInstance is only a duplicate when
                // the claiming instance deploys the same app (same artifact). When a
                // different app legitimately owns the name, flagging it would be unsafe.
                deploymentsByServiceName.TryGetValue(candidate.Name, out var claimingDeployment);
                deploymentsByServiceName.TryGetValue(canonical.Name, out var canonicalDeployment);
                if (claimingDeployment is not null
                    && canonicalDeployment is not null
                    && claimingDeployment.ArtifactId != canonicalDeployment.ArtifactId)
                {
                    continue;
                }

                matches.Add(new LegacyDuplicateTwinMatch(candidate, canonical, claimingDeployment));
                break;
            }
        }

        return matches;
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

    private static bool IsServiceNameWithKnownPrefix(string serviceName, IEnumerable<string> serviceNamePrefixes)
        => serviceNamePrefixes.Any(prefix =>
            string.Equals(serviceName, prefix, StringComparison.OrdinalIgnoreCase)
            || serviceName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    private static string? GetServiceState(string serviceName)
    {
        var result = RunSc("query", serviceName);
        if (result.ExitCode != 0)
        {
            return result.IsServiceNotFound() ? null : throw new InvalidOperationException(result.CombinedOutput.Trim());
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

    private static ScCommandResult RunSc(params string[] arguments)
    {
        var result = HostAgentProcessRunner.Run(
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "sc.exe"),
            arguments);
        return new ScCommandResult(result.ExitCode, result.StdOut, result.StdErr);
    }

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
            job.LeaseToken,
            HostAgentJobStatuses.Failed,
            null,
            error,
            cancellationToken);
    }

    private async Task TryMarkJobFailedAfterCancellationAsync(
        HostAgentJobWorkItem job,
        string error)
    {
        using var completionCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await CompleteFailedAsync(job, error, completionCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "HostAgent could not mark a canceled job as failed. HostAgentJobId={HostAgentJobId}, JobType={JobType}",
                job.HostAgentJobId,
                job.JobType);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "HostAgent timed out while marking a canceled job as failed. HostAgentJobId={HostAgentJobId}, JobType={JobType}",
                job.HostAgentJobId,
                job.JobType);
        }
    }

    private sealed record HostAgentServiceCandidate(
        string Name,
        string? State,
        string? ExecutablePath);

    internal sealed record ServiceAppServiceCandidate(
        string Name,
        string? State,
        string? ExecutablePath,
        string? DisplayName,
        bool IsUnderServicesRoot = true);

    private sealed record LegacyDuplicateTwinMatch(
        ServiceAppServiceCandidate Twin,
        ServiceAppServiceCandidate Canonical,
        ServiceAppDeploymentDescriptor? ClaimingDeployment);

}
