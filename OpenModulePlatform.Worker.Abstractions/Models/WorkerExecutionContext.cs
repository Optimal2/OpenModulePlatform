// File: OpenModulePlatform.Worker.Abstractions/Models/WorkerExecutionContext.cs
namespace OpenModulePlatform.Worker.Abstractions.Models;

/// <summary>
/// Execution context passed to a worker module.
/// </summary>
public sealed class WorkerExecutionContext
{
    /// <summary>
    /// Gets the app instance being executed.
    /// </summary>
    public Guid AppInstanceId { get; init; }

    /// <summary>
    /// Gets the worker instance being executed. For legacy app-instance workers this equals AppInstanceId.
    /// </summary>
    public Guid WorkerInstanceId { get; init; }

    /// <summary>
    /// Gets the stable worker instance key, when the manager resolved one.
    /// </summary>
    public string WorkerInstanceKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the stable worker type key resolved by the child host.
    /// </summary>
    public string WorkerTypeKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the fully qualified plugin assembly path used by the child host.
    /// </summary>
    public string PluginAssemblyPath { get; init; } = string.Empty;

    /// <summary>
    /// Gets optional worker-instance configuration JSON from OMP.
    /// </summary>
    public string? ConfigurationJson { get; init; }

    /// <summary>
    /// Gets the UTC timestamp when the child host created the runtime context.
    /// </summary>
    public DateTimeOffset StartedUtc { get; init; }
}
