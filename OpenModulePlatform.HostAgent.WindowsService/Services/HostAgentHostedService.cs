using System.Data.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.WindowsService.Services;

public sealed class HostAgentHostedService : BackgroundService
{
    private readonly HostAgentEngine _engine;
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly ILogger<HostAgentHostedService> _logger;

    public HostAgentHostedService(
        HostAgentEngine engine,
        IOptionsMonitor<HostAgentSettings> settings,
        ILogger<HostAgentHostedService> logger)
    {
        _engine = engine;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hostKey = _settings.CurrentValue.ResolveHostKey();

        _logger.LogInformation("HostAgent started. HostKey={HostKey}", hostKey);

        try
        {
            await RunCycleSafelyAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var refreshSeconds = Math.Max(1, _settings.CurrentValue.RefreshSeconds);
                await Task.Delay(TimeSpan.FromSeconds(refreshSeconds), stoppingToken);
                await RunCycleSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("HostAgent cancellation requested. HostKey={HostKey}", hostKey);
        }
    }

    private async Task RunCycleSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _engine.RunOnceAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            LogCycleFailure(ex);
        }
        catch (IOException ex)
        {
            LogCycleFailure(ex);
        }
        catch (DbException ex)
        {
            LogCycleFailure(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogCycleFailure(ex);
        }
        catch (TimeoutException ex)
        {
            LogCycleFailure(ex);
        }
    }

    private void LogCycleFailure(Exception exception)
    {
        _logger.LogError(exception, "HostAgent cycle failed.");
    }
}
