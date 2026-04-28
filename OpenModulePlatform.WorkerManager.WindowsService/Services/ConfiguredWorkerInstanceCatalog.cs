using Microsoft.Extensions.Options;
using OpenModulePlatform.WorkerManager.WindowsService.Contracts;
using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Utilities;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

public sealed class ConfiguredWorkerInstanceCatalog : IWorkerInstanceCatalog
{
    private readonly IOptionsMonitor<WorkerManagerSettings> _settings;

    public ConfiguredWorkerInstanceCatalog(IOptionsMonitor<WorkerManagerSettings> settings)
    {
        _settings = settings;
    }

    public Task<IReadOnlyList<DesiredWorkerInstance>> GetDesiredWorkersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var current = _settings.CurrentValue;
        current.Validate();

        var desired = new List<DesiredWorkerInstance>();
        var seen = new HashSet<Guid>();

        for (var i = 0; i < current.Workers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var configured = current.Workers[i];
            configured.Validate(i);

            if (!configured.Enabled)
            {
                continue;
            }

            var workerInstanceId = configured.ResolveWorkerInstanceId();
            if (!seen.Add(workerInstanceId))
            {
                throw new InvalidOperationException(
                    $"WorkerManager:Workers contains duplicate WorkerInstanceId '{workerInstanceId}'.");
            }

            desired.Add(new DesiredWorkerInstance
            {
                AppInstanceId = configured.AppInstanceId,
                WorkerInstanceId = workerInstanceId,
                WorkerInstanceKey = string.IsNullOrWhiteSpace(configured.WorkerInstanceKey)
                    ? configured.AppInstanceId.ToString("N")
                    : configured.WorkerInstanceKey.Trim(),
                WorkerTypeKey = configured.WorkerTypeKey.Trim(),
                PluginAssemblyPath = ResolvePath(configured.PluginAssemblyPath),
                ConfigurationJson = configured.ConfigurationJson,
                ShutdownEventName = BuildShutdownEventName(workerInstanceId)
            });
        }

        return Task.FromResult<IReadOnlyList<DesiredWorkerInstance>>(desired);
    }

    private static string ResolvePath(string path)
    {
        return PathResolutionUtility.ResolvePath(path);
    }

    private static string BuildShutdownEventName(Guid workerInstanceId)
    {
        return $"OpenModulePlatform.WorkerShutdown.{workerInstanceId:N}";
    }
}
