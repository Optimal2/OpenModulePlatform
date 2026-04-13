// File: OpenModulePlatform.Worker.Abstractions/Models/WorkerExecutionContext.cs
namespace OpenModulePlatform.Worker.Abstractions.Models;

/// <summary>
/// Minimal execution context passed to a worker module.
/// </summary>
public sealed class WorkerExecutionContext
{
    /// <summary>
    /// Gets or sets the app instance being executed.
    /// </summary>
    public Guid AppInstanceId { get; init; }
}
