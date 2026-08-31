// File: OpenModulePlatform.WorkerProcessHost/Models/WorkerProcessSettings.cs
namespace OpenModulePlatform.WorkerProcessHost.Models;

public sealed class WorkerProcessSettings
{
    public const string SectionName = "WorkerProcess";

    private const string ShutdownEventNamePrefix = "OpenModulePlatform.WorkerShutdown.";

    public Guid AppInstanceId { get; set; }

    public Guid WorkerInstanceId { get; set; }

    public string WorkerInstanceKey { get; set; } = string.Empty;

    public string WorkerTypeKey { get; set; } = string.Empty;

    public string PluginAssemblyPath { get; set; } = string.Empty;

    public string? PluginArtifactRootPath { get; set; }

    public string? WorkerHostComponentKey { get; set; }

    public string? WorkerHostArtifactVersion { get; set; }

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

        if (string.IsNullOrWhiteSpace(WorkerHostComponentKey) != string.IsNullOrWhiteSpace(WorkerHostArtifactVersion))
        {
            throw new InvalidOperationException(
                "WorkerProcess:WorkerHostComponentKey and WorkerProcess:WorkerHostArtifactVersion must be configured together.");
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

    /// <summary>
    /// Deterministic name of the manager-owned drain event for this worker.
    /// Keep in sync with the WorkerManager catalog naming convention.
    /// </summary>
    public string BuildDrainEventName()
    {
        return $"OpenModulePlatform.WorkerDrain.{WorkerInstanceId:N}";
    }

    /// <summary>
    /// Deterministic name of the manager-owned busy event for this worker.
    /// Keep in sync with the WorkerManager catalog naming convention.
    /// </summary>
    public string BuildBusyEventName()
    {
        return $"OpenModulePlatform.WorkerBusy.{WorkerInstanceId:N}";
    }
}
