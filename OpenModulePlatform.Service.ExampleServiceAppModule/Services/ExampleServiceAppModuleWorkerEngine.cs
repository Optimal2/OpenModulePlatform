// File: OpenModulePlatform.Service.ExampleServiceAppModule/Services/ExampleServiceAppModuleWorkerEngine.cs
using OpenModulePlatform.Service.ExampleServiceAppModule.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Service.ExampleServiceAppModule.Services;

public sealed class ExampleServiceAppModuleWorkerEngine
{
    private readonly ILogger<ExampleServiceAppModuleWorkerEngine> _log;
    private readonly IOptionsMonitor<WorkerSettings> _workerSettings;
    private readonly HostInstallationRepository _hostInstallations;
    private readonly ExampleServiceAppModuleConfigService _configService;
    private readonly ExampleServiceAppModuleJobRepository _jobs;
    private readonly ExampleServiceAppModuleJobProcessor _processor;

    private DateTime _nextHeartbeatUtc = DateTime.MinValue;
    private DateTime _nextConfigRefreshUtc = DateTime.MinValue;
    private ExampleServiceAppModuleOptions? _config;
    private HostInstallationRepository.HostInstallationRuntime? _runtime;

    public ExampleServiceAppModuleWorkerEngine(
        ILogger<ExampleServiceAppModuleWorkerEngine> log,
        IOptionsMonitor<WorkerSettings> workerSettings,
        HostInstallationRepository hostInstallations,
        ExampleServiceAppModuleConfigService configService,
        ExampleServiceAppModuleJobRepository jobs,
        ExampleServiceAppModuleJobProcessor processor)
    {
        _log = log;
        _workerSettings = workerSettings;
        _hostInstallations = hostInstallations;
        _configService = configService;
        _jobs = jobs;
        _processor = processor;
    }

    public async Task ExecuteLoopAsync(CancellationToken stoppingToken)
    {
        var settings = _workerSettings.CurrentValue;
        var hostInstallationId = settings.HostInstallationId;
        _log.LogInformation("Started. HostInstallationId={HostInstallationId}", hostInstallationId);

        HostInstallationRepository.ObservedIdentity? observed = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            settings = _workerSettings.CurrentValue;
            try
            {
                var now = DateTime.UtcNow;

                if (now >= _nextHeartbeatUtc)
                {
                    observed = await _hostInstallations.HeartbeatAsync(hostInstallationId, stoppingToken);
                    _nextHeartbeatUtc = now.AddSeconds(Math.Max(1, settings.HeartbeatSeconds));
                }

                if (now >= _nextConfigRefreshUtc)
                {
                    var refresh = await _configService.RefreshAsync(hostInstallationId, stoppingToken);
                    _runtime = refresh.Runtime;
                    _config = refresh.Config;
                    _nextConfigRefreshUtc = now.AddSeconds(Math.Max(1, settings.ConfigRefreshSeconds));

                    if (_runtime is null || _config is null)
                        _log.LogInformation("Service app inactive for current cycle. Reason={Reason}", refresh.StateReason);
                }

                if (_runtime is null || _config is null || observed is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.PollSeconds)), stoppingToken);
                    continue;
                }

                if (!IsExpectedIdentityMatch(_runtime, observed, out var mismatchReason))
                {
                    _log.LogError(
                        "Host installation identity mismatch. HostInstallationId={HostInstallationId} ExpectedLogin={ExpectedLogin} ObservedLogin={ObservedLogin} Reason={Reason}",
                        hostInstallationId,
                        _runtime.ExpectedLogin,
                        observed.Login,
                        mismatchReason);

                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.PollSeconds)), stoppingToken);
                    continue;
                }

                var processedAny = false;
                var batchSize = Math.Max(1, _config.ScanBatchSize);

                for (var i = 0; i < batchSize && !stoppingToken.IsCancellationRequested; i++)
                {
                    var job = await _jobs.TryClaimNextAsync(hostInstallationId, stoppingToken);
                    if (job is null)
                        break;

                    processedAny = true;
                    await _processor.ProcessOneAsync(hostInstallationId, _config, job, stoppingToken);
                }

                if (!processedAny)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.PollSeconds)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SqlException ex)
            {
                _log.LogError(ex, "Loop error caused by a database operation.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (InvalidOperationException ex)
            {
                _log.LogError(ex, "Loop error caused by an invalid worker state.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (IOException ex)
            {
                _log.LogError(ex, "Loop error caused by an I/O operation.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private static bool IsExpectedIdentityMatch(HostInstallationRepository.HostInstallationRuntime runtime, HostInstallationRepository.ObservedIdentity observed, out string reason)
    {
        reason = string.Empty;

        if (!string.IsNullOrWhiteSpace(runtime.ExpectedLogin)
            && !string.Equals(runtime.ExpectedLogin, observed.Login, StringComparison.OrdinalIgnoreCase))
        {
            reason = "login";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(runtime.ExpectedHostName)
            && !string.Equals(runtime.ExpectedHostName, observed.HostName, StringComparison.OrdinalIgnoreCase))
        {
            reason = "host_name";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(runtime.ExpectedClientIp)
            && !string.Equals(runtime.ExpectedClientIp, observed.ClientIp, StringComparison.OrdinalIgnoreCase))
        {
            reason = "client_ip";
            return false;
        }

        return true;
    }
}
