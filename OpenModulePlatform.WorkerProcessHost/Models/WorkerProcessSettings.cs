// File: OpenModulePlatform.WorkerProcessHost/Models/WorkerProcessSettings.cs
namespace OpenModulePlatform.WorkerProcessHost.Models;

public sealed class WorkerProcessSettings
{
    public Guid AppInstanceId { get; set; }

    public string WorkerTypeKey { get; set; } = string.Empty;

    public string PluginAssemblyPath { get; set; } = string.Empty;
}
