// File: OpenModulePlatform.Worker.Abstractions/Contracts/IWorkerDrainCoordinator.cs
namespace OpenModulePlatform.Worker.Abstractions.Contracts;

/// <summary>
/// Cooperative drain contract between the worker host and a worker module.
/// The manager requests a drain before it restarts a worker for a new artifact
/// version; a draining module must finish in-flight jobs but start no new ones.
/// Modules report job activity through <see cref="TryBeginJob"/> so the manager
/// only restarts the process while it is idle - a running job is never
/// interrupted by a version-change restart.
/// </summary>
public interface IWorkerDrainCoordinator
{
    /// <summary>
    /// Gets a value indicating whether the manager has asked this worker to
    /// stop starting new jobs so it can be restarted.
    /// </summary>
    bool IsDrainRequested { get; }

    /// <summary>
    /// Marks the start of a job. Returns a scope that must be disposed when the
    /// job reaches a safe completion point, or null when a drain is requested
    /// and the job must not start. While at least one scope is open the worker
    /// is reported busy and the manager will not restart the process.
    /// </summary>
    IDisposable? TryBeginJob();
}
