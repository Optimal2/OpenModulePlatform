// File: OpenModulePlatform.WorkerProcessHost/Models/WorkerProcessSettings.cs
namespace OpenModulePlatform.WorkerProcessHost.Models;

public sealed class WorkerProcessSettings
{
    private const string ShutdownEventNamePrefix = "OpenModulePlatform.WorkerShutdown.";

    public Guid AppInstanceId { get; set; }

    public Guid WorkerInstanceId { get; set; }

    public string WorkerInstanceKey { get; set; } = string.Empty;

    public string WorkerTypeKey { get; set; } = string.Empty;

    public string PluginAssemblyPath { get; set; } = string.Empty;

    public string? ConfigurationJson { get; set; }

    public string ShutdownEventName { get; set; } = string.Empty;

    public int MaxPrivateMemoryMegabytes { get; set; } = 1536;

    public int MemoryCheckIntervalSeconds { get; set; } = 30;

    public int MemoryLimitConsecutiveSamples { get; set; } = 2;

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

        if (!string.IsNullOrWhiteSpace(ShutdownEventName))
        {
            var expectedShutdownEventName = BuildShutdownEventName(WorkerInstanceId);
            if (!string.Equals(ShutdownEventName.Trim(), expectedShutdownEventName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "WorkerProcess:ShutdownEventName must match the OMP worker shutdown naming convention for WorkerProcess:WorkerInstanceId.");
            }

            ShutdownEventName = expectedShutdownEventName;
        }

        if (string.IsNullOrWhiteSpace(WorkerTypeKey))
        {
            throw new InvalidOperationException("WorkerProcess:WorkerTypeKey must be configured.");
        }

        if (string.IsNullOrWhiteSpace(PluginAssemblyPath))
        {
            throw new InvalidOperationException("WorkerProcess:PluginAssemblyPath must be configured.");
        }

        if (MaxPrivateMemoryMegabytes < 0)
        {
            throw new InvalidOperationException("WorkerProcess:MaxPrivateMemoryMegabytes cannot be negative.");
        }

        if (MemoryCheckIntervalSeconds < 5)
        {
            throw new InvalidOperationException("WorkerProcess:MemoryCheckIntervalSeconds must be at least 5.");
        }

        if (MemoryLimitConsecutiveSamples < 1)
        {
            throw new InvalidOperationException("WorkerProcess:MemoryLimitConsecutiveSamples must be at least 1.");
        }
    }

    private static string BuildShutdownEventName(Guid workerInstanceId)
    {
        return $"{ShutdownEventNamePrefix}{workerInstanceId:N}";
    }
}
