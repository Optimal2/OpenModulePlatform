// File: OpenModulePlatform.Worker.Abstractions/Contracts/IWorkerModule.cs
using OpenModulePlatform.Worker.Abstractions.Models;

namespace OpenModulePlatform.Worker.Abstractions.Contracts;

/// <summary>
/// Represents a worker module that can execute for one app instance.
/// </summary>
public interface IWorkerModule
{
    /// <summary>
    /// Runs the worker module until completion or cancellation.
    /// </summary>
    Task RunAsync(WorkerExecutionContext context, CancellationToken cancellationToken);
}
