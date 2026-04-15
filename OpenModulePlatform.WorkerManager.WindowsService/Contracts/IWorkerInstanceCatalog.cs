// File: OpenModulePlatform.WorkerManager.WindowsService/Contracts/IWorkerInstanceCatalog.cs
using OpenModulePlatform.WorkerManager.WindowsService.Models;

namespace OpenModulePlatform.WorkerManager.WindowsService.Contracts;

public interface IWorkerInstanceCatalog
{
    Task<IReadOnlyList<DesiredWorkerInstance>> GetDesiredWorkersAsync(CancellationToken cancellationToken);
}
