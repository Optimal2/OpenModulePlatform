using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
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
    private readonly HostAgentProcessContext _process;
    private readonly ILogger<HostAgentHostedService> _logger;

    public HostAgentHostedService(
        HostAgentEngine engine,
        IOptionsMonitor<HostAgentSettings> settings,
        HostAgentProcessContext process,
        ILogger<HostAgentHostedService> logger)
    {
        _engine = engine;
        _settings = settings;
        _process = process;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hostKey = _settings.CurrentValue.ResolveHostKey();

        _logger.LogInformation(
            "HostAgent started. HostKey={HostKey}, ServiceName={ServiceName}, Version={Version}, RuntimeMode={RuntimeMode}",
            hostKey,
            _process.ServiceName,
            _process.Version,
            _process.RuntimeMode);

        try
        {
            await RunCycleSafelyAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_process.IsQuiesceRequested)
                {
                    _logger.LogInformation(
                        "HostAgent quiesce requested. ServiceName={ServiceName}",
                        _process.ServiceName);
                    break;
                }

                var refreshSeconds = Math.Max(1, _settings.CurrentValue.RefreshSeconds);
                await Task.Delay(TimeSpan.FromSeconds(refreshSeconds), stoppingToken);
                await RunCycleSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException ex) when (IsExpectedShutdownCancellation(ex, stoppingToken))
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
        catch (Exception ex) when (IsRecoverableCycleFailure(ex))
        {
            LogCycleFailure(ex);
        }
    }

    private static bool IsExpectedShutdownCancellation(OperationCanceledException ex, CancellationToken stoppingToken)
    {
        return stoppingToken.IsCancellationRequested
            && (ex.CancellationToken == stoppingToken || !ex.CancellationToken.CanBeCanceled);
    }

    private static bool IsRecoverableCycleFailure(Exception exception)
    {
        return exception is InvalidOperationException
            or IOException
            or DbException
            or UnauthorizedAccessException
            or TimeoutException
            or CryptographicException
            or JsonException;
    }

    private void LogCycleFailure(Exception exception)
    {
        _logger.LogError(exception, "HostAgent cycle failed.");
    }
}
