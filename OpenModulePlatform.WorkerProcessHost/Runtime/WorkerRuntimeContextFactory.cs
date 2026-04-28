// File: OpenModulePlatform.WorkerProcessHost/Runtime/WorkerRuntimeContextFactory.cs
using OpenModulePlatform.Worker.Abstractions.Models;
using OpenModulePlatform.WorkerProcessHost.Models;

namespace OpenModulePlatform.WorkerProcessHost.Runtime;

/// <summary>
/// Creates the execution context passed to the worker module.
/// </summary>
public sealed class WorkerRuntimeContextFactory
{
    public WorkerExecutionContext Create(WorkerProcessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var workerInstanceId = settings.WorkerInstanceId == Guid.Empty
            ? settings.AppInstanceId
            : settings.WorkerInstanceId;

        return new WorkerExecutionContext
        {
            AppInstanceId = settings.AppInstanceId,
            WorkerInstanceId = workerInstanceId,
            WorkerInstanceKey = settings.WorkerInstanceKey.Trim(),
            WorkerTypeKey = settings.WorkerTypeKey,
            PluginAssemblyPath = Path.GetFullPath(settings.PluginAssemblyPath),
            ConfigurationJson = settings.ConfigurationJson,
            StartedUtc = DateTimeOffset.UtcNow
        };
    }
}
