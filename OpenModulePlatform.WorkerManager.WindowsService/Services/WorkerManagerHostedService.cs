// File: OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerManagerHostedService.cs
using System.ComponentModel;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.WorkerManager.WindowsService.Contracts;
using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Runtime;
using OpenModulePlatform.WorkerManager.WindowsService.Utilities;
using DbException = System.Data.Common.DbException;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

public sealed class WorkerManagerHostedService : BackgroundService
{
    private const string WorkerProcessHostExecutableName = "OpenModulePlatform.WorkerProcessHost.exe";
    private static readonly NamedWaitHandleOptions ShutdownEventOptions = new()
    {
        CurrentUserOnly = true
    };

    private readonly ILogger<WorkerManagerHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<WorkerManagerSettings> _settings;
    private readonly IWorkerInstanceCatalog _catalog;
    private readonly OmpWorkerRuntimeRepository _runtimeRepository;
    private readonly HostAgentRpcClient _hostAgentRpcClient;
    private readonly Dictionary<Guid, ManagedWorkerProcess> _managedWorkers = new();

    public WorkerManagerHostedService(
        ILogger<WorkerManagerHostedService> logger,
        IConfiguration configuration,
        IOptionsMonitor<WorkerManagerSettings> settings,
        IWorkerInstanceCatalog catalog,
        OmpWorkerRuntimeRepository runtimeRepository,
        HostAgentRpcClient hostAgentRpcClient)
    {
        _logger = logger;
        _configuration = configuration;
        _settings = settings;
        _catalog = catalog;
        _runtimeRepository = runtimeRepository;
        _hostAgentRpcClient = hostAgentRpcClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hostIdentity = _settings.CurrentValue.ResolveHostKey();
        var refreshInterval = TimeSpan.FromSeconds(Math.Max(1, _settings.CurrentValue.RefreshSeconds));

        _logger.LogInformation(
            "WorkerManager started. HostIdentity={HostIdentity}, RefreshSeconds={RefreshSeconds}",
            hostIdentity,
            refreshInterval.TotalSeconds);

        try
        {
            await CleanupOrphanedWorkerProcessesOnStartupAsync(stoppingToken);
            await RunReconcileCycleAsync(hostIdentity, stoppingToken);

            using var timer = new PeriodicTimer(refreshInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunReconcileCycleAsync(hostIdentity, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("WorkerManager cancellation requested. HostIdentity={HostIdentity}", hostIdentity);
        }
        finally
        {
            await StopAllWorkersAsync("manager shutdown", CancellationToken.None);
        }
    }

    private async Task RunReconcileCycleAsync(string hostIdentity, CancellationToken cancellationToken)
    {
        try
        {
            await TouchHostHeartbeatIfEnabledAsync(hostIdentity, cancellationToken);
            await ReconcileWorkersAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableWorkerManagerFailure(ex))
        {
            // A single reconcile failure must not stop the Windows service;
            // the next cycle gets a fresh catalog/runtime view and retries.
            _logger.LogError(
                ex,
                "WorkerManager reconcile cycle failed and will be retried during the next cycle. HostIdentity={HostIdentity}",
                hostIdentity);
        }
    }

    private async Task ReconcileWorkersAsync(CancellationToken cancellationToken)
    {
        var desiredWorkers = await ResolveDesiredWorkerArtifactsAsync(
            await _catalog.GetDesiredWorkersAsync(cancellationToken),
            cancellationToken);

        var desiredById = desiredWorkers.ToDictionary(worker => worker.WorkerInstanceId);
        var runtimeKind = GetRuntimeKindOrNull();

        var exitedWorkers = _managedWorkers.Values
            .Where(managed => managed.NeedsExitObservation())
            .ToList();

        foreach (var managed in exitedWorkers)
        {
            if (!managed.ObserveExitIfNeeded())
            {
                continue;
            }

            _logger.LogWarning(
                "Worker process exited. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, ExitCode={ExitCode}, StopRequested={StopRequested}",
                managed.Definition.AppInstanceId,
                managed.Definition.WorkerInstanceId,
                managed.LastExitCode,
                managed.StopRequested);

            await PublishExitObservationIfEnabledAsync(managed, runtimeKind, "worker process exited", cancellationToken);
        }

        var undesiredWorkers = _managedWorkers.Values
            .Where(worker => !desiredById.ContainsKey(worker.Definition.WorkerInstanceId))
            .ToList();

        foreach (var existing in undesiredWorkers)
        {
            try
            {
                await StopAndRemoveWorkerAsync(existing, runtimeKind, "worker no longer desired", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableWorkerManagerFailure(ex))
            {
                // Keep reconciling other workers; per-worker failures are published below.
                await HandleWorkerFailureAsync(existing, runtimeKind, "stop undesired worker", ex, cancellationToken);
            }
        }

        foreach (var desired in desiredWorkers)
        {
            if (!_managedWorkers.TryGetValue(desired.WorkerInstanceId, out var managed))
            {
                managed = new ManagedWorkerProcess(desired);
                _managedWorkers.Add(desired.WorkerInstanceId, managed);
            }

            try
            {
                if (!managed.HasEquivalentConfiguration(desired))
                {
                    _logger.LogInformation(
                        "Worker configuration changed. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, WorkerTypeKey={WorkerTypeKey}",
                        desired.AppInstanceId,
                        desired.WorkerInstanceId,
                        desired.WorkerTypeKey);

                    await StopWorkerAsync(managed, runtimeKind, "worker configuration changed", cancellationToken);
                    managed.UpdateDefinition(desired);
                }

                await EnsureWorkerRunningAsync(managed, runtimeKind, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableWorkerManagerFailure(ex))
            {
                // One worker can fail to stop, start, or publish status without blocking
                // reconciliation for the remaining workers in the same service cycle.
                await HandleWorkerFailureAsync(managed, runtimeKind, "reconcile desired worker", ex, cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<DesiredWorkerInstance>> ResolveDesiredWorkerArtifactsAsync(
        IReadOnlyList<DesiredWorkerInstance> desiredWorkers,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        var resolvedWorkers = new List<DesiredWorkerInstance>(desiredWorkers.Count);

        foreach (var desired in desiredWorkers)
        {
            var resolved = desired;
            var shouldAskHostAgent = ShouldRequestArtifactFromHostAgent(settings, desired);

            if (shouldAskHostAgent)
            {
                var response = await _hostAgentRpcClient.EnsureArtifactAsync(
                    desired.ArtifactId!.Value,
                    null,
                    cancellationToken);

                if (response?.Success == true && !string.IsNullOrWhiteSpace(response.LocalPath))
                {
                    resolved = desired.WithInstallRootPath(response.LocalPath);
                }
                else
                {
                    _logger.LogWarning(
                        "HostAgent could not provision worker artifact. WorkerInstanceId={WorkerInstanceId}, ArtifactId={ArtifactId}, Error={Error}",
                        desired.WorkerInstanceId,
                        desired.ArtifactId,
                        response?.ErrorMessage ?? "no response");
                }
            }

            if (string.IsNullOrWhiteSpace(resolved.PluginAssemblyPath))
            {
                _logger.LogWarning(
                    "Skipping desired worker with unresolved plugin path. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, ArtifactId={ArtifactId}",
                    resolved.AppInstanceId,
                    resolved.WorkerInstanceId,
                    resolved.ArtifactId);
                continue;
            }

            resolvedWorkers.Add(resolved);
        }

        return resolvedWorkers;
    }

    private async Task EnsureWorkerRunningAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        CancellationToken cancellationToken)
    {
        if (managed.IsRunning())
        {
            await PublishRunningObservationIfEnabledAsync(managed, runtimeKind, cancellationToken);
            return;
        }

        var settings = _settings.CurrentValue;

        var nowUtc = DateTimeOffset.UtcNow;
        var restartWindow = TimeSpan.FromSeconds(settings.RestartWindowSeconds);
        var nextAllowedStartUtc = managed.GetNextEligibleStartUtc(nowUtc, restartWindow, settings.MaxRestartsPerWindow);

        if (nextAllowedStartUtc.HasValue && nextAllowedStartUtc.Value > nowUtc)
        {
            _logger.LogWarning(
                "Worker restart delayed by restart policy. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, NextAllowedStartUtc={NextAllowedStartUtc:O}",
                managed.Definition.AppInstanceId,
                managed.Definition.WorkerInstanceId,
                nextAllowedStartUtc.Value);
            return;
        }

        if (managed.LastExitUtc.HasValue)
        {
            var restartDelay = TimeSpan.FromSeconds(settings.RestartDelaySeconds);
            var earliestRestartUtc = managed.LastExitUtc.Value.Add(restartDelay);
            if (earliestRestartUtc > nowUtc)
            {
                return;
            }
        }

        var workerProcessPath = await ResolveWorkerProcessPathAsync(settings, cancellationToken);
        ValidateReadableStartupFile(workerProcessPath, "Resolved WorkerProcessHost executable");
        ValidateReadableStartupFile(managed.Definition.PluginAssemblyPath, "Worker plugin assembly");

        using var startupResources = new WorkerStartupResources(CreateShutdownEvent(managed.Definition));

        var ompConnectionString = _configuration.GetConnectionString("OmpDb");
        if (string.IsNullOrWhiteSpace(ompConnectionString))
        {
            throw new InvalidOperationException("Missing connection string: ConnectionStrings:OmpDb");
        }

        var process = CreateWorkerProcess(workerProcessPath, managed.Definition, ompConnectionString);
        startupResources.AttachProcess(process);
        managed.RecordStartAttempt(nowUtc, restartWindow);

        StartWorkerProcess(process, managed.Definition.WorkerInstanceId, workerProcessPath);

        managed.AttachProcess(process, startupResources.ShutdownEvent, nowUtc);
        startupResources.ReleaseOwnership();

        await PublishStartingObservationIfEnabledAsync(managed, runtimeKind, cancellationToken);

        _logger.LogInformation(
            "Started worker process. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, WorkerTypeKey={WorkerTypeKey}, ProcessId={ProcessId}, WorkerProcessPath={WorkerProcessPath}, PluginAssemblyPath={PluginAssemblyPath}",
            managed.Definition.AppInstanceId,
            managed.Definition.WorkerInstanceId,
            managed.Definition.WorkerTypeKey,
            managed.ProcessId,
            workerProcessPath,
            managed.Definition.PluginAssemblyPath);
    }

    private async Task StopAndRemoveWorkerAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        string reason,
        CancellationToken cancellationToken)
    {
        await StopWorkerAsync(managed, runtimeKind, reason, cancellationToken);
        _managedWorkers.Remove(managed.Definition.WorkerInstanceId);
    }

    private static bool ShouldRequestArtifactFromHostAgent(
        WorkerManagerSettings settings,
        DesiredWorkerInstance desired)
    {
        return settings.HostAgentRpc.Enabled
            && desired.ArtifactId.HasValue
            && !string.IsNullOrWhiteSpace(desired.PluginRelativePath)
            && (!desired.IsProvisionedFromHostArtifactCache
                || string.IsNullOrWhiteSpace(desired.PluginAssemblyPath)
                || !File.Exists(desired.PluginAssemblyPath));
    }

    private async Task StopAllWorkersAsync(string reason, CancellationToken cancellationToken)
    {
        var runtimeKind = GetRuntimeKindOrNull();

        foreach (var managed in _managedWorkers.Values.ToList())
        {
            try
            {
                await StopWorkerAsync(managed, runtimeKind, reason, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableWorkerManagerFailure(ex))
            {
                // Shutdown cleanup is best-effort because the service is already stopping.
                _logger.LogWarning(
                    ex,
                    "WorkerManager could not stop a managed worker during shutdown cleanup. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, Reason={Reason}",
                    managed.Definition.AppInstanceId,
                    managed.Definition.WorkerInstanceId,
                    reason);
            }
        }

        _managedWorkers.Clear();
    }

    private async Task StopWorkerAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!managed.IsRunning() && managed.Process is null)
        {
            return;
        }

        var settings = _settings.CurrentValue;
        var stopTimeout = TimeSpan.FromSeconds(settings.StopTimeoutSeconds);

        _logger.LogInformation(
            "Stopping worker process. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, Reason={Reason}, ProcessId={ProcessId}",
            managed.Definition.AppInstanceId,
            managed.Definition.WorkerInstanceId,
            reason,
            managed.ProcessId);

        await PublishStoppingObservationIfEnabledAsync(managed, runtimeKind, reason, cancellationToken);

        var stoppedGracefully = await managed.RequestStopAsync(stopTimeout, cancellationToken);
        if (!stoppedGracefully)
        {
            _logger.LogWarning(
                "Worker process did not stop within timeout and will be killed. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, StopTimeoutSeconds={StopTimeoutSeconds}, ProcessId={ProcessId}",
                managed.Definition.AppInstanceId,
                managed.Definition.WorkerInstanceId,
                settings.StopTimeoutSeconds,
                managed.ProcessId);

            var killed = await managed.KillAsync(stopTimeout, cancellationToken);
            if (!killed)
            {
                throw new TimeoutException(
                    $"Worker process '{managed.Definition.WorkerInstanceId}' did not exit within {settings.StopTimeoutSeconds} seconds after kill.");
            }
        }

        await PublishExitObservationIfEnabledAsync(managed, runtimeKind, reason, cancellationToken);

        _logger.LogInformation(
            "Worker process stopped. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, ExitCode={ExitCode}",
            managed.Definition.AppInstanceId,
            managed.Definition.WorkerInstanceId,
            managed.LastExitCode);
    }

    private async Task HandleWorkerFailureAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        string phase,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(
            exception,
            "WorkerManager failed to {Phase}. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, WorkerTypeKey={WorkerTypeKey}",
            phase,
            managed.Definition.AppInstanceId,
            managed.Definition.WorkerInstanceId,
            managed.Definition.WorkerTypeKey);

        if (string.IsNullOrWhiteSpace(runtimeKind))
        {
            return;
        }

        var observation = CreateObservation(
            managed,
            runtimeKind,
            WorkerObservedStates.Failed,
            managed.LastStartUtc,
            DateTimeOffset.UtcNow,
            managed.LastExitUtc,
            $"WorkerManager failed to {phase}: {exception.Message}");

        await TryPublishObservationAsync(observation, touchAppInstanceHeartbeat: false, cancellationToken);
    }

    private async Task TouchHostHeartbeatIfEnabledAsync(string hostIdentity, CancellationToken cancellationToken)
    {
        if (!ShouldPublishRuntimeToOmp())
        {
            return;
        }

        try
        {
            await _runtimeRepository.TouchHostHeartbeatAsync(hostIdentity, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to publish manager heartbeat. HostIdentity={HostIdentity}", hostIdentity);
        }
    }

    private async Task PublishStartingObservationIfEnabledAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtimeKind))
        {
            return;
        }

        var observation = CreateObservation(
            managed,
            runtimeKind,
            WorkerObservedStates.Starting,
            managed.LastStartUtc,
            null,
            null,
            "worker process started");

        await TryPublishObservationAsync(observation, touchAppInstanceHeartbeat: true, cancellationToken);
    }

    private async Task PublishRunningObservationIfEnabledAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtimeKind))
        {
            return;
        }

        var observation = CreateObservation(
            managed,
            runtimeKind,
            WorkerObservedStates.Running,
            managed.LastStartUtc,
            DateTimeOffset.UtcNow,
            null,
            "worker process running");

        await TryPublishObservationAsync(observation, touchAppInstanceHeartbeat: true, cancellationToken);
    }

    private async Task PublishStoppingObservationIfEnabledAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtimeKind))
        {
            return;
        }

        var observation = CreateObservation(
            managed,
            runtimeKind,
            WorkerObservedStates.Stopping,
            managed.LastStartUtc,
            null,
            null,
            reason);

        await TryPublishObservationAsync(observation, touchAppInstanceHeartbeat: false, cancellationToken);
    }

    private async Task PublishExitObservationIfEnabledAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtimeKind))
        {
            return;
        }

        var observedState = managed.LastExitCode.GetValueOrDefault() == 0
            ? WorkerObservedStates.Stopped
            : WorkerObservedStates.Failed;

        var observation = CreateObservation(
            managed,
            runtimeKind,
            observedState,
            managed.LastStartUtc,
            null,
            managed.LastExitUtc,
            BuildExitMessage(managed, reason));

        await TryPublishObservationAsync(observation, touchAppInstanceHeartbeat: false, cancellationToken);
    }

    private async Task TryPublishObservationAsync(
        WorkerRuntimeObservation observation,
        bool touchAppInstanceHeartbeat,
        CancellationToken cancellationToken)
    {
        try
        {
            await _runtimeRepository.PublishObservationAsync(observation, touchAppInstanceHeartbeat, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish worker runtime observation. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, ObservedState={ObservedState}",
                observation.AppInstanceId,
                observation.WorkerInstanceId,
                observation.ObservedState);
        }
    }

    private bool ShouldPublishRuntimeToOmp()
    {
        return string.Equals(
            _settings.CurrentValue.GetCatalogMode(),
            WorkerCatalogModes.OmpDatabase,
            StringComparison.OrdinalIgnoreCase);
    }

    private string? GetRuntimeKindOrNull()
    {
        if (!ShouldPublishRuntimeToOmp())
        {
            return null;
        }

        var ompDatabase = _settings.CurrentValue.OmpDatabase;
        if (ompDatabase is null || string.IsNullOrWhiteSpace(ompDatabase.RuntimeKind))
        {
            return null;
        }

        return ompDatabase.RuntimeKind.Trim();
    }

    private static WorkerRuntimeObservation CreateObservation(
        ManagedWorkerProcess managed,
        string runtimeKind,
        byte observedState,
        DateTimeOffset? startedUtc,
        DateTimeOffset? lastSeenUtc,
        DateTimeOffset? lastExitUtc,
        string statusMessage)
    {
        return new WorkerRuntimeObservation
        {
            AppInstanceId = managed.Definition.AppInstanceId,
            WorkerInstanceId = managed.Definition.WorkerInstanceId,
            WorkerInstanceKey = managed.Definition.WorkerInstanceKey,
            RuntimeKind = runtimeKind,
            WorkerTypeKey = managed.Definition.WorkerTypeKey,
            ObservedState = observedState,
            ProcessId = managed.IsRunning() ? managed.ProcessId : null,
            StartedUtc = startedUtc,
            LastSeenUtc = lastSeenUtc,
            LastExitUtc = lastExitUtc,
            LastExitCode = managed.LastExitCode,
            StatusMessage = statusMessage
        };
    }

    private static string BuildExitMessage(ManagedWorkerProcess managed, string reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "worker process exited"
            : reason.Trim();

        return managed.LastExitCode.GetValueOrDefault() == 0
            ? normalizedReason
            : $"{normalizedReason}; exit code {managed.LastExitCode}";
    }

    private static string ResolvePath(string path)
    {
        return PathResolutionUtility.ResolvePath(path);
    }

    private async Task<string> ResolveWorkerProcessPathAsync(
        WorkerManagerSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.WorkerProcessPath))
        {
            return ResolvePath(settings.WorkerProcessPath);
        }

        if (!string.Equals(settings.GetCatalogMode(), WorkerCatalogModes.OmpDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "WorkerManager:WorkerProcessPath must be configured unless WorkerManager:CatalogMode is 'OmpDatabase'.");
        }

        var hostKey = settings.ResolveHostKey();
        var workerProcessPath = await _runtimeRepository.ResolveWorkerProcessHostPathAsync(hostKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(workerProcessPath))
        {
            throw new InvalidOperationException(
                $"Could not resolve a provisioned OMP Worker Process Host artifact for HostKey '{hostKey}'.");
        }

        return workerProcessPath;
    }

    private static void ValidateReadableStartupFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{description} path is not configured.");
        }

        try
        {
            // This is a diagnostic preflight check only. Process.Start and the worker host still
            // handle the authoritative file-system state because paths can change after validation.
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is ArgumentException
            or DirectoryNotFoundException
            or FileNotFoundException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{description} is not readable: '{path}'.", ex);
        }
    }

    private static void StartWorkerProcess(Process process, Guid workerInstanceId, string workerProcessPath)
    {
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Failed to start worker process for WorkerInstanceId '{workerInstanceId}'.");
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException
            or FileNotFoundException
            or UnauthorizedAccessException
            or Win32Exception)
        {
            throw new InvalidOperationException(
                $"Failed to start WorkerProcessHost for WorkerInstanceId '{workerInstanceId}'. Path='{workerProcessPath}'.",
                ex);
        }
    }

    private static Process CreateWorkerProcess(
        string workerProcessPath,
        DesiredWorkerInstance desired,
        string ompConnectionString)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = workerProcessPath,
            WorkingDirectory = Path.GetDirectoryName(workerProcessPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Worker plugins use their own logging providers. Redirecting these streams here would
            // require an always-drained pipe and can block noisy workers if the manager falls behind.
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        startInfo.ArgumentList.Add($"--WorkerProcess:AppInstanceId={desired.AppInstanceId:D}");
        startInfo.ArgumentList.Add($"--WorkerProcess:WorkerInstanceId={desired.WorkerInstanceId:D}");
        startInfo.ArgumentList.Add($"--WorkerProcess:WorkerInstanceKey={desired.WorkerInstanceKey}");
        startInfo.ArgumentList.Add($"--WorkerProcess:WorkerTypeKey={desired.WorkerTypeKey}");
        startInfo.ArgumentList.Add($"--WorkerProcess:PluginAssemblyPath={desired.PluginAssemblyPath}");
        startInfo.ArgumentList.Add($"--WorkerProcess:ShutdownEventName={desired.ShutdownEventName}");
        var workerConfigurationJson = desired.ConfigurationJson;
        if (!string.IsNullOrWhiteSpace(workerConfigurationJson))
        {
            // Worker configuration may contain module-specific values. Keep it out of
            // process command lines and let WorkerProcessHost read it from environment config.
            startInfo.Environment["WorkerProcess__ConfigurationJson"] = workerConfigurationJson;
        }

        var normalizedOmpConnectionString = ompConnectionString.Trim();
        // Worker host and plugins are OMP-provisioned code running in the same Windows
        // service trust boundary. Environment variables can still be read by the same
        // service account or local administrators, but they avoid command-line exposure;
        // service account isolation and filesystem ACLs are the intended protection boundary.
        startInfo.Environment["ConnectionStrings__OmpDb"] = normalizedOmpConnectionString;

        return new Process
        {
            StartInfo = startInfo,
            // Exit detection is intentionally polling-based through ManagedWorkerProcess so that
            // reconciliation observes all workers in one place instead of mixing event callbacks.
            EnableRaisingEvents = false
        };
    }

    private async Task CleanupOrphanedWorkerProcessesOnStartupAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var settings = _settings.CurrentValue;

        string workerProcessPath;
        try
        {
            workerProcessPath = Path.GetFullPath(await ResolveWorkerProcessPathAsync(settings, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "WorkerManager skipped startup orphan scan because the worker host executable path could not be resolved.");
            return;
        }

        var managerProcessPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(managerProcessPath))
        {
            _logger.LogWarning("WorkerManager skipped startup orphan scan because the manager executable path could not be resolved.");
            return;
        }

        IReadOnlyList<OrphanedWorkerProcess> orphanedProcesses;
        try
        {
            orphanedProcesses = FindOrphanedWorkerProcesses(
                _logger,
                workerProcessPath,
                Path.GetFullPath(managerProcessPath));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or Win32Exception or NotSupportedException)
        {
            _logger.LogWarning(
                ex,
                "WorkerManager skipped startup orphan scan because running worker process metadata could not be enumerated.");
            return;
        }

        if (orphanedProcesses.Count == 0)
        {
            return;
        }

        var stopTimeout = TimeSpan.FromSeconds(settings.StopTimeoutSeconds);
        var cleanedCount = 0;

        foreach (var orphan in orphanedProcesses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var process = Process.GetProcessById(orphan.ProcessId);
                if (process.HasExited)
                {
                    cleanedCount++;
                    continue;
                }

                _logger.LogWarning(
                    "WorkerManager is stopping an orphaned worker host process discovered during startup. ProcessId={ProcessId}, ParentProcessId={ParentProcessId}, WorkerProcessPath={WorkerProcessPath}, CommandLine={CommandLine}",
                    orphan.ProcessId,
                    orphan.ParentProcessId,
                    workerProcessPath,
                    orphan.CommandLine);

                // WorkerProcessHost owns the plugin lifetime by default. The setting
                // allows hosts with intentionally independent plugin children to opt out.
                process.Kill(entireProcessTree: settings.CleanupOrphansKillProcessTree);
                if (!await WaitForProcessExitAsync(process, stopTimeout, cancellationToken))
                {
                    throw new TimeoutException(
                        $"Orphaned worker host process '{orphan.ProcessId}' did not exit within {settings.StopTimeoutSeconds} seconds.");
                }

                cleanedCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ArgumentException)
            {
                cleanedCount++;
            }
            catch (InvalidOperationException)
            {
                cleanedCount++;
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(
                    ex,
                    "WorkerManager timed out while stopping an orphaned worker host process during startup. ProcessId={ProcessId}",
                    orphan.ProcessId);
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
            {
                _logger.LogWarning(
                    ex,
                    "WorkerManager could not stop an orphaned worker host process during startup. ProcessId={ProcessId}",
                    orphan.ProcessId);
            }
        }

        if (cleanedCount > 0)
        {
            _logger.LogWarning(
                "WorkerManager cleaned orphaned worker host processes during startup. Count={Count}, WorkerProcessPath={WorkerProcessPath}",
                cleanedCount,
                workerProcessPath);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<OrphanedWorkerProcess> FindOrphanedWorkerProcesses(
        ILogger<WorkerManagerHostedService> logger,
        string workerProcessPath,
        string managerProcessPath)
    {
        var normalizedWorkerProcessPath = Path.GetFullPath(workerProcessPath);
        var result = new List<OrphanedWorkerProcess>();
        using var searcher = new ManagementObjectSearcher(CreateWorkerProcessHostQuery());
        using var processes = searcher.Get();

        foreach (ManagementObject process in processes)
        {
            using (process)
            {
                var processId = ReadManagementUInt32(process, "ProcessId");
                if (processId <= 0 || processId == Environment.ProcessId)
                {
                    continue;
                }

                var executablePath = process["ExecutablePath"] as string;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    continue;
                }

                if (!string.Equals(
                        Path.GetFullPath(executablePath),
                        normalizedWorkerProcessPath,
                        GetPathComparison()))
                {
                    continue;
                }

                var commandLine = process["CommandLine"] as string;
                if (!HasWorkerHostOwnershipMarkers(commandLine))
                {
                    logger.LogDebug(
                        "WorkerManager skipped a process with the worker host executable name because its command line did not match expected worker ownership markers. ProcessId={ProcessId}, ExecutablePath={ExecutablePath}",
                        processId,
                        executablePath);
                    continue;
                }

                var parentProcessId = ReadManagementUInt32(process, "ParentProcessId");
                if (IsLiveWorkerManagerParent(parentProcessId, managerProcessPath))
                {
                    continue;
                }

                result.Add(new OrphanedWorkerProcess(
                    processId,
                    parentProcessId,
                    executablePath,
                    FormatOrphanCommandLine(commandLine)));
            }
        }

        return result;
    }

    private static bool HasWorkerHostOwnershipMarkers(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        return commandLine.Contains("--WorkerProcess:AppInstanceId=", StringComparison.OrdinalIgnoreCase)
            && commandLine.Contains("--WorkerProcess:WorkerInstanceId=", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FormatOrphanCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var normalized = commandLine.Trim();
        const int maxLength = 500;
        return normalized.Length > maxLength
            ? normalized[..maxLength] + "..."
            : normalized;
    }

    private static string CreateWorkerProcessHostQuery()
    {
        var executableNameLiteral = CreateSafeWqlStringLiteral(WorkerProcessHostExecutableName);
        return "SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = "
            + executableNameLiteral;
    }

    private static string CreateSafeWqlStringLiteral(string value)
    {
        var unsupportedCharacter = value
            .Where(ch => !IsSafeWqlStringLiteralCharacter(ch))
            .Select(ch => (char?)ch)
            .FirstOrDefault();

        if (unsupportedCharacter.HasValue)
        {
            throw new InvalidOperationException(
                $"WorkerProcessHost executable name contains an unsupported WMI query character: '{value}'.");
        }

        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static bool IsSafeWqlStringLiteralCharacter(char ch)
        => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-';

    [SupportedOSPlatform("windows")]
    private static bool IsLiveWorkerManagerParent(int parentProcessId, string managerProcessPath)
    {
        if (parentProcessId <= 0)
        {
            return false;
        }

        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            if (parent.HasExited)
            {
                return false;
            }

            var executablePath = parent.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(executablePath)
                && string.Equals(
                    Path.GetFullPath(executablePath),
                    managerProcessPath,
                    GetPathComparison());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int ReadManagementUInt32(ManagementBaseObject process, string propertyName)
    {
        return process[propertyName] switch
        {
            uint value => checked((int)value),
            int value => value,
            _ => 0
        };
    }

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static async Task<bool> WaitForProcessExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(waitCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool IsRecoverableWorkerManagerFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or DbException
            or UnauthorizedAccessException
            or TimeoutException
            or ArgumentException
            or ManagementException
            or Win32Exception
            or NotSupportedException;

    private static EventWaitHandle CreateShutdownEvent(DesiredWorkerInstance definition)
    {
        // The named event is a cooperative same-host shutdown signal between WorkerManager and
        // the OMP-provisioned WorkerProcessHost. CurrentUserOnly scopes the object to the service
        // identity, and createdNew protects against stale or pre-created handles with the same
        // deterministic worker-instance name.
        var shutdownEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: definition.ShutdownEventName,
            options: ShutdownEventOptions,
            createdNew: out var createdNew);

        if (createdNew)
        {
            return shutdownEvent;
        }

        shutdownEvent.Dispose();
        throw new InvalidOperationException(
            $"Worker shutdown event already exists for WorkerInstanceId '{definition.WorkerInstanceId}'. Refusing to reuse named event '{definition.ShutdownEventName}'.");
    }

    private sealed class WorkerStartupResources : IDisposable
    {
        private bool _ownsResources = true;

        public WorkerStartupResources(EventWaitHandle shutdownEvent)
        {
            ShutdownEvent = shutdownEvent;
        }

        public EventWaitHandle ShutdownEvent { get; }

        public Process? Process { get; private set; }

        public void AttachProcess(Process process)
        {
            Process = process;
        }

        public void ReleaseOwnership()
        {
            _ownsResources = false;
        }

        public void Dispose()
        {
            if (!_ownsResources)
            {
                return;
            }

            Process?.Dispose();
            ShutdownEvent.Dispose();
        }
    }

    private sealed record OrphanedWorkerProcess(
        int ProcessId,
        int ParentProcessId,
        string ExecutablePath,
        string? CommandLine);
}
