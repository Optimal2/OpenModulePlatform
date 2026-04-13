// File: OpenModulePlatform.WorkerManager.WindowsService/Runtime/ManagedWorkerProcess.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Runtime;

/// <summary>
/// Represents a supervised worker process entry in the manager.
/// </summary>
public sealed class ManagedWorkerProcess
{
    /// <summary>
    /// Gets or sets the app instance handled by the child process.
    /// </summary>
    public Guid AppInstanceId { get; init; }
}
