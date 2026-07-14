using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Background service that periodically enqueues a detect-only <see cref="HostAgentJobTypes.MaintenanceScan"/> job.
/// This scheduler NEVER enqueues <see cref="HostAgentJobTypes.MaintenanceCleanup"/>; cleanup remains human-gated.
/// </summary>
public sealed class MaintenanceScanScheduler : BackgroundService
{
    internal const string DefaultRequestedBy = "system:scheduled-maintenance-scan";

    private readonly IOmpHostArtifactRepository _repository;
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly HostAgentProcessContext _process;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MaintenanceScanScheduler> _logger;

    public MaintenanceScanScheduler(
        IOmpHostArtifactRepository repository,
        IOptionsMonitor<HostAgentSettings> settings,
        HostAgentProcessContext process,
        TimeProvider timeProvider,
        ILogger<MaintenanceScanScheduler> logger)
    {
        _repository = repository;
        _settings = settings;
        _process = process;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = _settings.CurrentValue.MaintenanceScanIntervalMinutes;

        if (intervalMinutes <= 0)
        {
            _logger.LogInformation(
                "Scheduled maintenance scan is disabled because MaintenanceScanIntervalMinutes is {IntervalMinutes}. ServiceName={ServiceName}",
                intervalMinutes,
                _process.ServiceName);
            return;
        }

        var interval = TimeSpan.FromMinutes(intervalMinutes);
        var hostKey = _settings.CurrentValue.ResolveHostKey();

        _logger.LogInformation(
            "Scheduled maintenance scan started. Interval={IntervalMinutes}m, HostKey={HostKey}, ServiceName={ServiceName}",
            intervalMinutes,
            hostKey,
            _process.ServiceName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var enqueued = await _repository.EnqueueMaintenanceScanJobAsync(
                    hostKey,
                    DefaultRequestedBy,
                    stoppingToken);

                if (enqueued)
                {
                    _logger.LogInformation(
                        "Enqueued scheduled maintenance scan. HostKey={HostKey}, ServiceName={ServiceName}",
                        hostKey,
                        _process.ServiceName);
                }
                else
                {
                    _logger.LogWarning(
                        "Scheduled maintenance scan was not enqueued because the host is not registered or enabled. HostKey={HostKey}, ServiceName={ServiceName}",
                        hostKey,
                        _process.ServiceName);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to enqueue scheduled maintenance scan. HostKey={HostKey}, ServiceName={ServiceName}",
                    hostKey,
                    _process.ServiceName);
            }
        }
    }
}
