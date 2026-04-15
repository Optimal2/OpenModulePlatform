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

        return new WorkerExecutionContext
        {
            AppInstanceId = settings.AppInstanceId,
            WorkerTypeKey = settings.WorkerTypeKey,
            PluginAssemblyPath = Path.GetFullPath(settings.PluginAssemblyPath),
            StartedUtc = DateTimeOffset.UtcNow
        };
    }
}
