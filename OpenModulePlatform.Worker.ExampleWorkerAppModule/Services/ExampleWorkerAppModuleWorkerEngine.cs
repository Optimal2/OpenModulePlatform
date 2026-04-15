// File: OpenModulePlatform.Worker.ExampleWorkerAppModule/Services/ExampleWorkerAppModuleWorkerEngine.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Worker.Abstractions.Models;
using OpenModulePlatform.Worker.ExampleWorkerAppModule.Models;
using Microsoft.Extensions.Logging;

namespace OpenModulePlatform.Worker.ExampleWorkerAppModule.Services;

/// <summary>
/// Main orchestration loop for the example manager-driven worker module.
/// </summary>
public sealed class ExampleWorkerAppModuleWorkerEngine
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConfigRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly ILogger<ExampleWorkerAppModuleWorkerEngine> _log;
    private readonly ExampleWorkerAppModuleConfigService _configService;
    private readonly ExampleWorkerAppModuleJobRepository _jobs;
    private readonly ExampleWorkerAppModuleJobProcessor _processor;

    private DateTime _nextConfigRefreshUtc = DateTime.MinValue;
    private ExampleWorkerAppModuleOptions? _config;
    private AppInstanceRepository.AppInstanceRuntime? _runtime;

    public ExampleWorkerAppModuleWorkerEngine(
        ILogger<ExampleWorkerAppModuleWorkerEngine> log,
        ExampleWorkerAppModuleConfigService configService,
        ExampleWorkerAppModuleJobRepository jobs,
        ExampleWorkerAppModuleJobProcessor processor)
    {
        _log = log;
        _configService = configService;
        _jobs = jobs;
        _processor = processor;
    }

    public async Task ExecuteLoopAsync(WorkerExecutionContext context, CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "Started example worker plugin. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}",
            context.AppInstanceId,
            context.WorkerTypeKey);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;

                if (now >= _nextConfigRefreshUtc)
                {
                    var refresh = await _configService.RefreshAsync(context.AppInstanceId, stoppingToken);
                    _runtime = refresh.Runtime;
                    _config = refresh.Config;
                    _nextConfigRefreshUtc = now.Add(ConfigRefreshInterval);

                    if (_runtime is null || _config is null)
                    {
                        _log.LogInformation(
                            "Worker plugin inactive for current cycle. AppInstanceId={AppInstanceId}, Reason={Reason}",
                            context.AppInstanceId,
                            refresh.StateReason);
                    }
                }

                if (_runtime is null || _config is null)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                var processedAny = false;
                var batchSize = Math.Max(1, _config.ScanBatchSize);

                for (var i = 0; i < batchSize && !stoppingToken.IsCancellationRequested; i++)
                {
                    var job = await _jobs.TryClaimNextAsync(context.AppInstanceId, stoppingToken);
                    if (job is null)
                    {
                        break;
                    }

                    processedAny = true;
                    await _processor.ProcessOneAsync(
                        context.AppInstanceId,
                        _config,
                        job,
                        stoppingToken);
                }

                if (!processedAny)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SqlException ex)
            {
                _log.LogError(ex, "Worker loop error caused by a database operation.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (InvalidOperationException ex)
            {
                _log.LogError(ex, "Worker loop error caused by an invalid worker state.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (IOException ex)
            {
                _log.LogError(ex, "Worker loop error caused by an I/O operation.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
