// File: OpenModulePlatform.WorkerProcessHost/Services/WorkerProcessHostedService.cs
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
        RegisteredWaitHandle? shutdownRegistration = null;

        try
        {
            _settings.Validate();
            using var shutdownEvent = TryOpenShutdownEvent();
            shutdownRegistration = RegisterExternalShutdownSignal(shutdownEvent);

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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Environment.ExitCode = 0;

            _logger.LogInformation(
                "Worker process cancellation requested. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}",
                _settings.AppInstanceId,
                _settings.WorkerTypeKey);
        }
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
            shutdownRegistration?.Unregister(null);
            _applicationLifetime.StopApplication();
        }
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
    }

    private RegisteredWaitHandle? RegisterExternalShutdownSignal(EventWaitHandle? shutdownEvent)
    {
        if (shutdownEvent is null)
        {
            return null;
        }

        try
        {
            return ThreadPool.RegisterWaitForSingleObject(
                shutdownEvent,
                static (state, _) =>
                {
                    var service = (WorkerProcessHostedService)state!;
                    service._logger.LogInformation(
                        "External shutdown requested. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}",
                        service._settings.AppInstanceId,
                        service._settings.WorkerTypeKey);
                    service._applicationLifetime.StopApplication();
                },
                this,
                Timeout.Infinite,
                executeOnlyOnce: true);
        }
        catch
        {
            shutdownEvent.Dispose();
            throw;
        }
    }
}
