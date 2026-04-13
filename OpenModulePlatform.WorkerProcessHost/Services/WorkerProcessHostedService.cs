// File: OpenModulePlatform.WorkerProcessHost/Services/WorkerProcessHostedService.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.WorkerProcessHost.Models;

namespace OpenModulePlatform.WorkerProcessHost.Services;

public sealed class WorkerProcessHostedService : BackgroundService
{
    private readonly ILogger<WorkerProcessHostedService> _logger;
    private readonly WorkerProcessSettings _settings;

    public WorkerProcessHostedService(
        ILogger<WorkerProcessHostedService> logger,
        IOptions<WorkerProcessSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WorkerProcessHost started with no active functionality. AppInstanceId={AppInstanceId}, WorkerTypeKey={WorkerTypeKey}",
            _settings.AppInstanceId,
            _settings.WorkerTypeKey);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
