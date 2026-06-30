// File: OpenModulePlatform.WorkerProcessHost/Services/WorkerProcessHostedService.cs
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.WorkerProcessHost.Models;
using OpenModulePlatform.WorkerProcessHost.Plugins;
using OpenModulePlatform.WorkerProcessHost.Runtime;

namespace OpenModulePlatform.WorkerProcessHost.Services;

public sealed class WorkerProcessHostedService : BackgroundService
{
    private const long BytesPerMegabyte = 1024L * 1024L;
    private const int MaxGcGeneration = 2;

    private readonly ILogger<WorkerProcessHostedService> _logger;
    private readonly WorkerProcessSettings _settings;
    private readonly WorkerModuleLoader _loader;
    private readonly WorkerRuntimeContextFactory _contextFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _applicationLifetime;

    public WorkerProcessHostedService(
        ILogger<WorkerProcessHostedService> logger,
        IOptions<WorkerProcessSettings> settings,
        WorkerModuleLoader loader,
        WorkerRuntimeContextFactory contextFactory,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime applicationLifetime)
    {
        _logger = logger;
        _settings = settings.Value;
        _loader = loader;
        _contextFactory = contextFactory;
        _scopeFactory = scopeFactory;
        _applicationLifetime = applicationLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _settings.Validate();
            using var memoryGuardCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var memoryGuardTask = RunMemoryGuardAsync(memoryGuardCts.Token);

            try
            {
                await RunWorkerModuleAsync(stoppingToken);
            }
            finally
            {
                await StopMemoryGuardAsync(memoryGuardCts, memoryGuardTask);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Environment.ExitCode = 0;

            _logger.LogInformation(
                "Worker process cancellation requested. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}",
                _settings.AppInstanceId,
                _settings.WorkerTypeKey);
        }
        // This is the worker process boundary: unexpected module startup or runtime
        // failures must be logged and converted to a non-zero process exit code.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Environment.ExitCode = 1;

            _logger.LogCritical(
                ex,
                "Worker process failed to start or execute. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}, PluginAssemblyPath={PluginAssemblyPath}",
                _settings.AppInstanceId,
                _settings.WorkerTypeKey,
                _settings.PluginAssemblyPath);
        }
        finally
        {
            _applicationLifetime.StopApplication();
        }
    }

    private async Task RunWorkerModuleAsync(CancellationToken stoppingToken)
    {
        using var shutdownEvent = TryOpenShutdownEvent();
        var shutdownRegistration = RegisterExternalShutdownSignal(shutdownEvent);

        try
        {
            var factory = _loader.LoadFactory(_settings.PluginAssemblyPath, _settings.WorkerTypeKey);
            using var scope = _scopeFactory.CreateScope();

            var module = factory.Create(scope.ServiceProvider)
                ?? throw new InvalidOperationException(
                    $"Worker factory '{factory.GetType().FullName}' returned null from Create().");

            var context = _contextFactory.Create(_settings);

            _logger.LogInformation(
                "Starting worker module. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}, PluginAssemblyPath={PluginAssemblyPath}, FactoryType={FactoryType}",
                context.AppInstanceId,
                context.WorkerTypeKey,
                context.PluginAssemblyPath,
                factory.GetType().FullName);

            await module.RunAsync(context, stoppingToken);

            Environment.ExitCode = 0;

            _logger.LogInformation(
                "Worker module completed normally. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}",
                context.AppInstanceId,
                context.WorkerTypeKey);
        }
        finally
        {
            shutdownRegistration?.Unregister(null);
        }
    }

    private async Task RunMemoryGuardAsync(CancellationToken cancellationToken)
    {
        if (_settings.MaxPrivateMemoryMegabytes <= 0)
        {
            return;
        }

        var thresholdBytes = _settings.MaxPrivateMemoryMegabytes * BytesPerMegabyte;
        var consecutiveOverageCount = 0;
        var compactedCurrentOverage = false;

        using var process = Process.GetCurrentProcess();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.MemoryCheckIntervalSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var privateBytes = GetCurrentPrivateMemoryBytes(process);
                if (privateBytes < thresholdBytes)
                {
                    consecutiveOverageCount = 0;
                    compactedCurrentOverage = false;
                    continue;
                }

                consecutiveOverageCount++;
                if (!compactedCurrentOverage)
                {
                    _logger.LogWarning(
                        "Worker process private memory exceeded the configured threshold; compacting managed heap before deciding whether to recycle. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}, PrivateMemoryMegabytes={PrivateMemoryMegabytes}, MaxPrivateMemoryMegabytes={MaxPrivateMemoryMegabytes}",
                        _settings.AppInstanceId,
                        _settings.WorkerTypeKey,
                        ToMegabytes(privateBytes),
                        _settings.MaxPrivateMemoryMegabytes);

                    CompactManagedHeap();
                    compactedCurrentOverage = true;
                    privateBytes = GetCurrentPrivateMemoryBytes(process);

                    if (privateBytes < thresholdBytes)
                    {
                        _logger.LogInformation(
                            "Worker process private memory dropped below the configured threshold after managed heap compaction. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}, PrivateMemoryMegabytes={PrivateMemoryMegabytes}, MaxPrivateMemoryMegabytes={MaxPrivateMemoryMegabytes}",
                            _settings.AppInstanceId,
                            _settings.WorkerTypeKey,
                            ToMegabytes(privateBytes),
                            _settings.MaxPrivateMemoryMegabytes);

                        consecutiveOverageCount = 0;
                        compactedCurrentOverage = false;
                        continue;
                    }
                }

                if (consecutiveOverageCount < _settings.MemoryLimitConsecutiveSamples)
                {
                    _logger.LogWarning(
                        "Worker process private memory remains above the configured threshold. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}, PrivateMemoryMegabytes={PrivateMemoryMegabytes}, MaxPrivateMemoryMegabytes={MaxPrivateMemoryMegabytes}, ConsecutiveSamples={ConsecutiveSamples}, RequiredSamples={RequiredSamples}",
                        _settings.AppInstanceId,
                        _settings.WorkerTypeKey,
                        ToMegabytes(privateBytes),
                        _settings.MaxPrivateMemoryMegabytes,
                        consecutiveOverageCount,
                        _settings.MemoryLimitConsecutiveSamples);
                    continue;
                }

                _logger.LogWarning(
                    "Worker process private memory stayed above the configured threshold; requesting a controlled process recycle. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}, PrivateMemoryMegabytes={PrivateMemoryMegabytes}, MaxPrivateMemoryMegabytes={MaxPrivateMemoryMegabytes}, ConsecutiveSamples={ConsecutiveSamples}",
                    _settings.AppInstanceId,
                    _settings.WorkerTypeKey,
                    ToMegabytes(privateBytes),
                    _settings.MaxPrivateMemoryMegabytes,
                    consecutiveOverageCount);

                _applicationLifetime.StopApplication();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                LogMemoryGuardSamplingFailure(ex);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                LogMemoryGuardSamplingFailure(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogMemoryGuardSamplingFailure(ex);
            }
        }
    }

    private void LogMemoryGuardSamplingFailure(Exception ex)
    {
        // The guard must never crash the worker host. Sampling failures are
        // logged and the next interval gets another chance to enforce limits.
        _logger.LogWarning(
            ex,
            "Worker process memory guard failed during a sampling pass. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}",
            _settings.AppInstanceId,
            _settings.WorkerTypeKey);
    }

    private async Task StopMemoryGuardAsync(CancellationTokenSource memoryGuardCts, Task memoryGuardTask)
    {
        await memoryGuardCts.CancelAsync();

        await memoryGuardTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (memoryGuardTask.IsFaulted && memoryGuardTask.Exception is { } exception)
        {
            foreach (var innerException in exception.Flatten().InnerExceptions)
            {
                _logger.LogWarning(
                    innerException,
                    "Worker process memory guard stopped with an unexpected failure during cleanup. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}",
                    _settings.AppInstanceId,
                    _settings.WorkerTypeKey);
            }
        }
    }

    private static long GetCurrentPrivateMemoryBytes(Process process)
    {
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private static long ToMegabytes(long bytes) => bytes / BytesPerMegabyte;

    private static void CompactManagedHeap()
    {
        // The memory guard only reaches this path after a threshold breach. The first forced
        // collection requests LOH compaction and runs finalizers; the second collection reclaims
        // objects made collectible by those finalizers before deciding whether to recycle.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(MaxGcGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(MaxGcGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private EventWaitHandle? TryOpenShutdownEvent()
    {
        if (string.IsNullOrWhiteSpace(_settings.ShutdownEventName))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning(
                "Configured shutdown events are only supported on Windows. ShutdownEventName={ShutdownEventName}",
                _settings.ShutdownEventName);
            return null;
        }

        try
        {
            return EventWaitHandle.OpenExisting(_settings.ShutdownEventName);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            _logger.LogWarning(
                "Configured shutdown event was not found. ShutdownEventName={ShutdownEventName}",
                _settings.ShutdownEventName);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "Configured shutdown event could not be opened because access was denied. ShutdownEventName={ShutdownEventName}",
                _settings.ShutdownEventName);
            return null;
        }
        catch (System.IO.IOException ex)
        {
            _logger.LogWarning(
                ex,
                "Configured shutdown event could not be opened due to an operating system I/O error. ShutdownEventName={ShutdownEventName}",
                _settings.ShutdownEventName);
            return null;
        }
    }

    private RegisteredWaitHandle? RegisterExternalShutdownSignal(EventWaitHandle? shutdownEvent)
    {
        if (shutdownEvent is null)
        {
            return null;
        }

        var signalState = new ShutdownSignalState(
            _logger,
            _applicationLifetime,
            _settings.AppInstanceId,
            _settings.WorkerTypeKey);

        return ThreadPool.RegisterWaitForSingleObject(
            shutdownEvent,
            static (state, _) =>
            {
                if (state is not ShutdownSignalState signalState)
                {
                    return;
                }

                signalState.Logger.LogInformation(
                    "External shutdown requested. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}",
                    signalState.AppInstanceId,
                    signalState.WorkerTypeKey);
                signalState.ApplicationLifetime.StopApplication();
            },
            signalState,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    private sealed record ShutdownSignalState(
        ILogger<WorkerProcessHostedService> Logger,
        IHostApplicationLifetime ApplicationLifetime,
        Guid AppInstanceId,
        string WorkerTypeKey);
}
