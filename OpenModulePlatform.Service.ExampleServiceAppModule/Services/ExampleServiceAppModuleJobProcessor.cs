// File: OpenModulePlatform.Service.ExampleServiceAppModule/Services/ExampleServiceAppModuleJobProcessor.cs
using OpenModulePlatform.Service.ExampleServiceAppModule.Models;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace OpenModulePlatform.Service.ExampleServiceAppModule.Services;

/// <summary>
/// Processes one claimed job for the example service-backed module.
/// </summary>
public sealed class ExampleServiceAppModuleJobProcessor
{
    private readonly ILogger<ExampleServiceAppModuleJobProcessor> _log;
    private readonly ExampleServiceAppModuleJobRepository _jobs;

    public ExampleServiceAppModuleJobProcessor(
        ILogger<ExampleServiceAppModuleJobProcessor> log,
        ExampleServiceAppModuleJobRepository jobs)
    {
        _log = log;
        _jobs = jobs;
    }

    public async Task ProcessOneAsync(
        Guid appInstanceId,
        ExampleServiceAppModuleOptions config,
        ExampleServiceAppModuleJobWorkItem job,
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
        ExampleServiceAppModuleJobWorkItem job,
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
