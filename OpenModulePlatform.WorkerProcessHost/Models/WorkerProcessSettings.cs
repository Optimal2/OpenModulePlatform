// File: OpenModulePlatform.WorkerProcessHost/Models/WorkerProcessSettings.cs
namespace OpenModulePlatform.WorkerProcessHost.Models;

public sealed class WorkerProcessSettings
{
    public Guid AppInstanceId { get; set; }

    public Guid WorkerInstanceId { get; set; }

    public string WorkerInstanceKey { get; set; } = string.Empty;

    public string WorkerTypeKey { get; set; } = string.Empty;

    public string PluginAssemblyPath { get; set; } = string.Empty;

    public string? ConfigurationJson { get; set; }

    public string ShutdownEventName { get; set; } = string.Empty;

    public void Validate()
    {
        if (AppInstanceId == Guid.Empty)
        {
            throw new InvalidOperationException("WorkerProcess:AppInstanceId must be a non-empty GUID.");
        }

        if (WorkerInstanceId == Guid.Empty)
        {
            WorkerInstanceId = AppInstanceId;
        }

        if (string.IsNullOrWhiteSpace(WorkerTypeKey))
        {
            throw new InvalidOperationException("WorkerProcess:WorkerTypeKey must be configured.");
        }

        if (string.IsNullOrWhiteSpace(PluginAssemblyPath))
        {
            throw new InvalidOperationException("WorkerProcess:PluginAssemblyPath must be configured.");
        }
    }
}
