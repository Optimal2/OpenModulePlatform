// File: OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerManagerHostedService.cs
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.WorkerManager.WindowsService.Contracts;
using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Runtime;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

public sealed class WorkerManagerHostedService : BackgroundService
{
    private readonly ILogger<WorkerManagerHostedService> _logger;
    private readonly IOptionsMonitor<WorkerManagerSettings> _settings;
    private readonly IWorkerInstanceCatalog _catalog;
    private readonly OmpWorkerRuntimeRepository _runtimeRepository;
    private readonly Dictionary<Guid, ManagedWorkerProcess> _managedWorkers = new();

    public WorkerManagerHostedService(
        ILogger<WorkerManagerHostedService> logger,
        IOptionsMonitor<WorkerManagerSettings> settings,
        IWorkerInstanceCatalog catalog,
        OmpWorkerRuntimeRepository runtimeRepository)
    {
        _logger = logger;
        _settings = settings;
        _catalog = catalog;
        _runtimeRepository = runtimeRepository;
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
            await TouchHostHeartbeatIfEnabledAsync(hostIdentity, stoppingToken);
            await ReconcileWorkersAsync(stoppingToken);

            using var timer = new PeriodicTimer(refreshInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TouchHostHeartbeatIfEnabledAsync(hostIdentity, stoppingToken);
                await ReconcileWorkersAsync(stoppingToken);
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

    private async Task ReconcileWorkersAsync(CancellationToken cancellationToken)
    {
        var desiredWorkers = await _catalog.GetDesiredWorkersAsync(cancellationToken);
        var desiredById = desiredWorkers.ToDictionary(worker => worker.AppInstanceId);
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
                "Worker process exited. AppInstanceId={AppInstanceId}, ExitCode={ExitCode}, StopRequested={StopRequested}",
                managed.Definition.AppInstanceId,
                managed.LastExitCode,
                managed.StopRequested);

            await PublishExitObservationIfEnabledAsync(managed, runtimeKind, "worker process exited", cancellationToken);
        }

        foreach (var existing in _managedWorkers.Values.Where(worker => !desiredById.ContainsKey(worker.Definition.AppInstanceId)).ToList())
        {
            await StopAndRemoveWorkerAsync(existing, runtimeKind, "worker no longer desired", cancellationToken);
        }

        foreach (var desired in desiredWorkers)
        {
            if (!_managedWorkers.TryGetValue(desired.AppInstanceId, out var managed))
            {
                managed = new ManagedWorkerProcess(desired);
                _managedWorkers.Add(desired.AppInstanceId, managed);
            }

            if (!managed.HasEquivalentConfiguration(desired))
            {
                _logger.LogInformation(
                    "Worker configuration changed. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}",
                    desired.AppInstanceId,
                    desired.WorkerTypeKey);

                await StopWorkerAsync(managed, runtimeKind, "worker configuration changed", cancellationToken);
                managed.UpdateDefinition(desired);
            }

            await EnsureWorkerRunningAsync(managed, runtimeKind, cancellationToken);
        }
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
                "Worker restart delayed by restart policy. AppInstanceId={AppInstanceId}, NextAllowedStartUtc={NextAllowedStartUtc:O}",
                managed.Definition.AppInstanceId,
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

        var workerProcessPath = ResolvePath(settings.WorkerProcessPath);
        if (!File.Exists(workerProcessPath))
        {
            throw new InvalidOperationException(
                $"Configured WorkerManager:WorkerProcessPath '{workerProcessPath}' does not exist.");
        }

        var shutdownEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: managed.Definition.ShutdownEventName);

        shutdownEvent.Reset();

        var process = CreateWorkerProcess(workerProcessPath, managed.Definition);
        managed.RecordStartAttempt(nowUtc, restartWindow);

        if (!process.Start())
        {
            shutdownEvent.Dispose();

            throw new InvalidOperationException(
                $"Failed to start worker process for AppInstanceId '{managed.Definition.AppInstanceId}'.");
        }

        managed.AttachProcess(process, shutdownEvent, nowUtc);
        await PublishStartingObservationIfEnabledAsync(managed, runtimeKind, cancellationToken);

        _logger.LogInformation(
            "Started worker process. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}, ProcessId={ProcessId}, WorkerProcessPath={WorkerProcessPath}, PluginAssemblyPath={PluginAssemblyPath}",
            managed.Definition.AppInstanceId,
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
        _managedWorkers.Remove(managed.Definition.AppInstanceId);
    }

    private async Task StopAllWorkersAsync(string reason, CancellationToken cancellationToken)
    {
        var runtimeKind = GetRuntimeKindOrNull();

        foreach (var managed in _managedWorkers.Values.ToList())
        {
            await StopWorkerAsync(managed, runtimeKind, reason, cancellationToken);
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
            "Stopping worker process. AppInstanceId={AppInstanceId}, Reason={Reason}, ProcessId={ProcessId}",
            managed.Definition.AppInstanceId,
            reason,
            managed.ProcessId);

        await PublishStoppingObservationIfEnabledAsync(managed, runtimeKind, reason, cancellationToken);

        var stoppedGracefully = await managed.RequestStopAsync(stopTimeout, cancellationToken);
        if (!stoppedGracefully)
        {
            _logger.LogWarning(
                "Worker process did not stop within timeout and will be killed. AppInstanceId={AppInstanceId}, StopTimeoutSeconds={StopTimeoutSeconds}, ProcessId={ProcessId}",
                managed.Definition.AppInstanceId,
                settings.StopTimeoutSeconds,
                managed.ProcessId);

            managed.Kill();
        }

        await PublishExitObservationIfEnabledAsync(managed, runtimeKind, reason, cancellationToken);

        _logger.LogInformation(
            "Worker process stopped. AppInstanceId={AppInstanceId}, ExitCode={ExitCode}",
            managed.Definition.AppInstanceId,
            managed.LastExitCode);
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
                "Failed to publish worker runtime observation. AppInstanceId={AppInstanceId}, ObservedState={ObservedState}",
                observation.AppInstanceId,
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
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.IsNullOrWhiteSpace(relativePath)
            ? Path.GetFullPath(baseDirectory)
            : Path.GetFullPath($"{baseDirectory}{Path.DirectorySeparatorChar}{relativePath}");
    }

    private static Process CreateWorkerProcess(string workerProcessPath, DesiredWorkerInstance desired)
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
        startInfo.ArgumentList.Add($"--WorkerProcess:WorkerTypeKey={desired.WorkerTypeKey}");
        startInfo.ArgumentList.Add($"--WorkerProcess:PluginAssemblyPath={desired.PluginAssemblyPath}");
        startInfo.ArgumentList.Add($"--WorkerProcess:ShutdownEventName={desired.ShutdownEventName}");

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = false
        };
    }
}
