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
        finally
        {
            await ReleaseLeaseAndRuntimeStateAsync();
        }
    }

    private async Task RunCycleSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _engine.RunOnceAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One convergence cycle must never take down the whole Windows service.
            // The previous curated allowlist (InvalidOperationException, IOException,
            // DbException, ...) missed realistic types thrown deep in the cycle —
            // InvalidDataException from a corrupt self-upgrade zip, ManagementException
            // from a WMI blip during service-identity repair, Win32Exception from
            // Process.Start on the web-app path — and any uncaught type escaped
            // ExecuteAsync, where .NET's default BackgroundServiceExceptionBehavior
            // .StopHost stopped the service and left it crash-looping every cycle
            // instead of logging one failed cycle and continuing (R5-D1). Catch every
            // non-cancellation fault at this boundary; OperationCanceledException still
            // propagates to the shutdown handler.
            LogCycleFailure(ex);
        }
    }

    private static bool IsExpectedShutdownCancellation(OperationCanceledException ex, CancellationToken stoppingToken)
    {
        return stoppingToken.IsCancellationRequested
            && (ex.CancellationToken == stoppingToken || !ex.CancellationToken.CanBeCanceled);
    }

    private void LogCycleFailure(Exception exception)
    {
        _logger.LogError(exception, "HostAgent cycle failed.");
    }

    private async Task ReleaseLeaseAndRuntimeStateAsync()
    {
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await _engine.ShutdownAsync(shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("HostAgent shutdown cleanup timed out.");
        }
        catch (Exception ex) when (IsRecoverableShutdownFailure(ex))
        {
            // Shutdown cleanup is best-effort because the service is already stopping.
            _logger.LogWarning(ex, "HostAgent shutdown cleanup failed.");
        }
    }

    private static bool IsRecoverableShutdownFailure(Exception exception)
        => IsRecoverableCycleFailure(exception)
            || exception is OperationCanceledException;

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
}
