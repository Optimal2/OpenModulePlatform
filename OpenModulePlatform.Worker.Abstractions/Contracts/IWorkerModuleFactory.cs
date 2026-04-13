// File: OpenModulePlatform.Worker.Abstractions/Contracts/IWorkerModuleFactory.cs
namespace OpenModulePlatform.Worker.Abstractions.Contracts;

/// <summary>
/// Creates worker module instances for a specific worker type.
/// </summary>
public interface IWorkerModuleFactory
{
    /// <summary>
    /// Gets the stable key that identifies the worker type.
    /// </summary>
    string WorkerTypeKey { get; }

    /// <summary>
    /// Creates a worker module instance.
    /// </summary>
    IWorkerModule Create(IServiceProvider serviceProvider);
}
