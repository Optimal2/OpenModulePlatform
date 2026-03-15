// File: OpenModulePlatform.Service.ExampleServiceAppModule/Services/ExampleServiceAppModuleJobProcessor.cs
using OpenModulePlatform.Service.ExampleServiceAppModule.Models;
using System.Text.Json;

namespace OpenModulePlatform.Service.ExampleServiceAppModule.Services;

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

    public async Task ProcessOneAsync(Guid hostInstallationId, ExampleServiceAppModuleOptions config, ExampleServiceAppModuleJobWorkItem job, CancellationToken ct)
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
            await _jobs.CompleteAsync(job.JobId, hostInstallationId, startedUtc, resultJson, ct);
            _log.LogInformation("Completed example job {JobId} of type {RequestType}", job.JobId, job.RequestType);
        }
        catch (Exception ex)
        {
            await _jobs.FailAsync(job.JobId, hostInstallationId, startedUtc, ex.Message, ct);
            _log.LogError(ex, "Failed example job {JobId}", job.JobId);
        }
    }
}
