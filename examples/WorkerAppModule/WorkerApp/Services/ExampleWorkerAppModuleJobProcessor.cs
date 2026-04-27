// File: OpenModulePlatform.Worker.ExampleWorkerAppModule/Services/ExampleWorkerAppModuleJobProcessor.cs
using OpenModulePlatform.Worker.ExampleWorkerAppModule.Models;
using Microsoft.Data.SqlClient;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenModulePlatform.Worker.ExampleWorkerAppModule.Services;

/// <summary>
/// Processes one claimed job for the example worker-manager-backed module.
/// </summary>
public sealed class ExampleWorkerAppModuleJobProcessor
{
    private readonly ILogger<ExampleWorkerAppModuleJobProcessor> _log;
    private readonly ExampleWorkerAppModuleJobRepository _jobs;

    public ExampleWorkerAppModuleJobProcessor(
        ILogger<ExampleWorkerAppModuleJobProcessor> log,
        ExampleWorkerAppModuleJobRepository jobs)
    {
        _log = log;
        _jobs = jobs;
    }

    public async Task ProcessOneAsync(
        Guid appInstanceId,
        ExampleWorkerAppModuleOptions config,
        ExampleWorkerAppModuleJobWorkItem job,
        CancellationToken ct)
    {
        var startedUtc = DateTime.UtcNow;

        try
        {
            var result = new
            {
                job.JobId,
                job.RequestType,
                config.SampleMode,
                config.ScanBatchSize,
                ProcessedUtc = DateTime.UtcNow,
                EchoPayload = job.PayloadJson
            };

            var resultJson = JsonSerializer.Serialize(result);
            await _jobs.CompleteAsync(job.JobId, appInstanceId, startedUtc, resultJson, ct);
            _log.LogInformation("Completed example job {JobId} of type {RequestType}", job.JobId, job.RequestType);
        }
        catch (JsonException ex)
        {
            await FailJobAsync(job, appInstanceId, startedUtc, ex, ct);
        }
        catch (SqlException ex)
        {
            await FailJobAsync(job, appInstanceId, startedUtc, ex, ct);
        }
        catch (InvalidOperationException ex)
        {
            await FailJobAsync(job, appInstanceId, startedUtc, ex, ct);
        }
    }

    private async Task FailJobAsync(
        ExampleWorkerAppModuleJobWorkItem job,
        Guid appInstanceId,
        DateTime startedUtc,
        Exception ex,
        CancellationToken ct)
    {
        try
        {
            await _jobs.FailAsync(job.JobId, appInstanceId, startedUtc, ex.Message, ct);
        }
        catch (SqlException failEx)
        {
            _log.LogError(failEx, "Failed to persist failure state for example job {JobId}.", job.JobId);
        }
        catch (InvalidOperationException failEx)
        {
            _log.LogError(failEx, "Failed to persist failure state for example job {JobId}.", job.JobId);
        }

        _log.LogError(ex, "Failed example job {JobId}", job.JobId);
    }
}
