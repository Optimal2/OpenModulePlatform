// File: OpenModulePlatform.WorkerManager.WindowsService/Services/ConfiguredWorkerInstanceCatalog.cs
using Microsoft.Extensions.Options;
using OpenModulePlatform.WorkerManager.WindowsService.Contracts;
using OpenModulePlatform.WorkerManager.WindowsService.Models;

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

            if (!seen.Add(configured.AppInstanceId))
            {
                throw new InvalidOperationException(
                    $"WorkerManager:Workers contains duplicate AppInstanceId '{configured.AppInstanceId}'.");
            }

            desired.Add(new DesiredWorkerInstance
            {
                AppInstanceId = configured.AppInstanceId,
                WorkerTypeKey = configured.WorkerTypeKey.Trim(),
                PluginAssemblyPath = ResolvePath(configured.PluginAssemblyPath),
                ShutdownEventName = BuildShutdownEventName(configured.AppInstanceId)
            });
        }

        return Task.FromResult<IReadOnlyList<DesiredWorkerInstance>>(desired);
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.IsNullOrWhiteSpace(relativePath)
            ? Path.GetFullPath(baseDirectory)
            : Path.GetFullPath($"{baseDirectory}{Path.DirectorySeparatorChar}{relativePath}");
    }

    private static string BuildShutdownEventName(Guid appInstanceId)
    {
        return $"OpenModulePlatform.WorkerShutdown.{appInstanceId:N}";
    }
}
