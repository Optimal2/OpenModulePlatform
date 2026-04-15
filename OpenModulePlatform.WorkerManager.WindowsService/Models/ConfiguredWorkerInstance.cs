// File: OpenModulePlatform.WorkerManager.WindowsService/Models/ConfiguredWorkerInstance.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public sealed class ConfiguredWorkerInstance
{
    public Guid AppInstanceId { get; set; }

    public string WorkerTypeKey { get; set; } = string.Empty;

    public string PluginAssemblyPath { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public void Validate(int index)
    {
        var prefix = $"WorkerManager:Workers:{index}";

        if (AppInstanceId == Guid.Empty)
        {
            throw new InvalidOperationException($"{prefix}:AppInstanceId must be a non-empty GUID.");
        }

        if (string.IsNullOrWhiteSpace(WorkerTypeKey))
        {
            throw new InvalidOperationException($"{prefix}:WorkerTypeKey must be configured.");
        }

        if (string.IsNullOrWhiteSpace(PluginAssemblyPath))
        {
            throw new InvalidOperationException($"{prefix}:PluginAssemblyPath must be configured.");
        }
    }
}
