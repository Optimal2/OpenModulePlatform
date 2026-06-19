// File: OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerManagerHostedService.cs
using System.ComponentModel;
using System.Diagnostics;
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

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

public sealed class WorkerManagerHostedService : BackgroundService
{
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
        catch (Exception ex)
        {
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

        foreach (var existing in _managedWorkers.Values.Where(worker => !desiredById.ContainsKey(worker.Definition.WorkerInstanceId)).ToList())
        {
            try
            {
                await StopAndRemoveWorkerAsync(existing, runtimeKind, "worker no longer desired", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
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
            catch (Exception ex)
            {
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
            var shouldAskHostAgent = settings.HostAgentRpc.Enabled
                && desired.ArtifactId.HasValue
                && !string.IsNullOrWhiteSpace(desired.PluginRelativePath)
                && (!desired.IsProvisionedFromHostArtifactCache || string.IsNullOrWhiteSpace(desired.PluginAssemblyPath) || !File.Exists(desired.PluginAssemblyPath));

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
        settings.Validate();

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
        if (!File.Exists(workerProcessPath))
        {
            throw new InvalidOperationException(
                $"Resolved WorkerProcessHost executable path '{workerProcessPath}' does not exist.");
        }

        if (!File.Exists(managed.Definition.PluginAssemblyPath))
        {
            throw new InvalidOperationException(
                $"Worker plugin assembly path does not exist: '{managed.Definition.PluginAssemblyPath}'.");
        }

        var shutdownEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: managed.Definition.ShutdownEventName);

        var process = CreateWorkerProcess(
            workerProcessPath,
            managed.Definition,
            _configuration.GetConnectionString("OmpDb"));
        managed.RecordStartAttempt(nowUtc, restartWindow);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Failed to start worker process for WorkerInstanceId '{managed.Definition.WorkerInstanceId}'.");
            }
        }
        catch
        {
            process.Dispose();
            shutdownEvent.Dispose();
            throw;
        }

        managed.AttachProcess(process, shutdownEvent, nowUtc);
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
            catch (Exception ex)
            {
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

        return _settings.CurrentValue.OmpDatabase.RuntimeKind.Trim();
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

    private static Process CreateWorkerProcess(
        string workerProcessPath,
        DesiredWorkerInstance desired,
        string? ompConnectionString)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = workerProcessPath,
            WorkingDirectory = Path.GetDirectoryName(workerProcessPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        startInfo.ArgumentList.Add($"--WorkerProcess:AppInstanceId={desired.AppInstanceId:D}");
        startInfo.ArgumentList.Add($"--WorkerProcess:WorkerInstanceId={desired.WorkerInstanceId:D}");
        startInfo.ArgumentList.Add($"--WorkerProcess:WorkerInstanceKey={desired.WorkerInstanceKey}");
        if (!string.IsNullOrWhiteSpace(desired.ConfigurationJson))
        {
            startInfo.ArgumentList.Add($"--WorkerProcess:ConfigurationJson={desired.ConfigurationJson}");
        }
        startInfo.ArgumentList.Add($"--WorkerProcess:WorkerTypeKey={desired.WorkerTypeKey}");
        startInfo.ArgumentList.Add($"--WorkerProcess:PluginAssemblyPath={desired.PluginAssemblyPath}");
        startInfo.ArgumentList.Add($"--WorkerProcess:ShutdownEventName={desired.ShutdownEventName}");
        if (!string.IsNullOrWhiteSpace(ompConnectionString))
        {
            // Worker modules run in a separate process; pass the OMP database connection through
            // process-local configuration without exposing it as a command-line argument.
            startInfo.Environment["ConnectionStrings__OmpDb"] = ompConnectionString.Trim();
        }

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = false
        };
    }

    private async Task CleanupOrphanedWorkerProcessesOnStartupAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        settings.Validate();

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

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IReadOnlyList<OrphanedWorkerProcess> orphanedProcesses;
        try
        {
            orphanedProcesses = FindOrphanedWorkerProcesses(
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
                    "WorkerManager is stopping an orphaned worker host process discovered during startup. ProcessId={ProcessId}, ParentProcessId={ParentProcessId}, WorkerProcessPath={WorkerProcessPath}",
                    orphan.ProcessId,
                    orphan.ParentProcessId,
                    workerProcessPath);

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(stopTimeout, cancellationToken);
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
        string workerProcessPath,
        string managerProcessPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var normalizedWorkerProcessPath = Path.GetFullPath(workerProcessPath);
        var result = new List<OrphanedWorkerProcess>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, ExecutablePath FROM Win32_Process WHERE Name = 'OpenModulePlatform.WorkerProcessHost.exe'");
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

                var parentProcessId = ReadManagementUInt32(process, "ParentProcessId");
                if (IsLiveWorkerManagerParent(parentProcessId, managerProcessPath))
                {
                    continue;
                }

                result.Add(new OrphanedWorkerProcess(processId, parentProcessId));
            }
        }

        return result;
    }

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

    private sealed record OrphanedWorkerProcess(int ProcessId, int ParentProcessId);
}
