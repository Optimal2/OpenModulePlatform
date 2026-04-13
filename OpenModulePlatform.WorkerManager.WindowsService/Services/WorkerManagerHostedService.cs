// File: OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerManagerHostedService.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.WorkerManager.WindowsService.Models;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

public sealed class WorkerManagerHostedService : BackgroundService
{
    private readonly ILogger<WorkerManagerHostedService> _logger;
    private readonly WorkerManagerSettings _settings;

    public WorkerManagerHostedService(
        ILogger<WorkerManagerHostedService> logger,
        IOptions<WorkerManagerSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WorkerManager started with no active functionality. RefreshSeconds={RefreshSeconds}",
            _settings.RefreshSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _settings.RefreshSeconds)), stoppingToken);
        }
    }
}
