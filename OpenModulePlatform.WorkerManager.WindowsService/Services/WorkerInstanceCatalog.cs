// File: OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerInstanceCatalog.cs
using Microsoft.Extensions.Options;
using OpenModulePlatform.WorkerManager.WindowsService.Contracts;
using OpenModulePlatform.WorkerManager.WindowsService.Models;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

public sealed class WorkerInstanceCatalog : IWorkerInstanceCatalog
{
    private readonly IOptionsMonitor<WorkerManagerSettings> _settings;
    private readonly ConfiguredWorkerInstanceCatalog _configuredCatalog;
    private readonly OmpDatabaseWorkerInstanceCatalog _ompDatabaseCatalog;

    public WorkerInstanceCatalog(
        IOptionsMonitor<WorkerManagerSettings> settings,
        ConfiguredWorkerInstanceCatalog configuredCatalog,
        OmpDatabaseWorkerInstanceCatalog ompDatabaseCatalog)
    {
        _settings = settings;
        _configuredCatalog = configuredCatalog;
        _ompDatabaseCatalog = ompDatabaseCatalog;
    }

    public Task<IReadOnlyList<DesiredWorkerInstance>> GetDesiredWorkersAsync(CancellationToken cancellationToken)
    {
        var catalogMode = _settings.CurrentValue.GetCatalogMode();

        return string.Equals(catalogMode, WorkerCatalogModes.OmpDatabase, StringComparison.OrdinalIgnoreCase)
            ? _ompDatabaseCatalog.GetDesiredWorkersAsync(cancellationToken)
            : _configuredCatalog.GetDesiredWorkersAsync(cancellationToken);
    }
}
