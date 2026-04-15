// File: OpenModulePlatform.WorkerManager.WindowsService/Models/DesiredWorkerInstance.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public sealed class DesiredWorkerInstance
{
    public Guid AppInstanceId { get; init; }

    public string WorkerTypeKey { get; init; } = string.Empty;

    public string PluginAssemblyPath { get; init; } = string.Empty;

    public string ShutdownEventName { get; init; } = string.Empty;

    public bool HasEquivalentConfiguration(DesiredWorkerInstance other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return AppInstanceId == other.AppInstanceId
            && string.Equals(WorkerTypeKey, other.WorkerTypeKey, StringComparison.Ordinal)
            && string.Equals(PluginAssemblyPath, other.PluginAssemblyPath, StringComparison.Ordinal)
            && string.Equals(ShutdownEventName, other.ShutdownEventName, StringComparison.Ordinal);
    }
}
