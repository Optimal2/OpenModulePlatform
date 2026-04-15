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
    private readonly Dictionary<Guid, ManagedWorkerProcess> _managedWorkers = new();

    public WorkerManagerHostedService(
        ILogger<WorkerManagerHostedService> logger,
        IOptionsMonitor<WorkerManagerSettings> settings,
        IWorkerInstanceCatalog catalog)
    {
        _logger = logger;
        _settings = settings;
        _catalog = catalog;
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
            await ReconcileWorkersAsync(stoppingToken);

            using var timer = new PeriodicTimer(refreshInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
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

        foreach (var managed in _managedWorkers.Values)
        {
            if (managed.ObserveExitIfNeeded())
            {
                _logger.LogWarning(
                    "Worker process exited. AppInstanceId={AppInstanceId}, ExitCode={ExitCode}, StopRequested={StopRequested}",
                    managed.Definition.AppInstanceId,
                    managed.LastExitCode,
                    managed.StopRequested);
            }
        }

        foreach (var existing in _managedWorkers.Values.Where(worker => !desiredById.ContainsKey(worker.Definition.AppInstanceId)).ToList())
        {
            await StopAndRemoveWorkerAsync(existing, "worker no longer desired", cancellationToken);
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

                await StopWorkerAsync(managed, "worker configuration changed", cancellationToken);
                managed.UpdateDefinition(desired);
            }

            await EnsureWorkerRunningAsync(managed, cancellationToken);
        }
    }

    private async Task EnsureWorkerRunningAsync(ManagedWorkerProcess managed, CancellationToken cancellationToken)
    {
        if (managed.IsRunning())
        {
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
        string reason,
        CancellationToken cancellationToken)
    {
        await StopWorkerAsync(managed, reason, cancellationToken);
        _managedWorkers.Remove(managed.Definition.AppInstanceId);
    }

    private async Task StopAllWorkersAsync(string reason, CancellationToken cancellationToken)
    {
        foreach (var managed in _managedWorkers.Values.ToList())
        {
            await StopWorkerAsync(managed, reason, cancellationToken);
        }

        _managedWorkers.Clear();
    }

    private async Task StopWorkerAsync(
        ManagedWorkerProcess managed,
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

        _logger.LogInformation(
            "Worker process stopped. AppInstanceId={AppInstanceId}, ExitCode={ExitCode}",
            managed.Definition.AppInstanceId,
            managed.LastExitCode);
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
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
