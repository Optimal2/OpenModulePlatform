// File: OpenModulePlatform.WorkerManager.WindowsService/Models/WorkerRuntimeObservation.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public sealed class WorkerRuntimeObservation
{
    public Guid AppInstanceId { get; init; }

    public Guid WorkerInstanceId { get; init; }

    public string WorkerInstanceKey { get; init; } = string.Empty;

    public string RuntimeKind { get; init; } = string.Empty;

    public string WorkerTypeKey { get; init; } = string.Empty;

    public byte ObservedState { get; init; } = WorkerObservedStates.Unknown;

    public int? ProcessId { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }

    public DateTimeOffset? LastSeenUtc { get; init; }

    public DateTimeOffset? LastExitUtc { get; init; }

    public int? LastExitCode { get; init; }

    public string? StatusMessage { get; init; }

    /// <summary>
    /// The artifact the observed process was started from, or null when there is no live
    /// process to witness (R12-F2).
    /// </summary>
    /// <remarks>
    /// Null is a deliberate, readable answer here and not an omission: "no process, so no
    /// running version" is what the diagnostics scripts print as a stated unknown. Reporting
    /// the definition's artifact for a worker that is stopped or that never started would
    /// claim a running build that does not exist.
    /// </remarks>
    public int? RuntimeArtifactId { get; init; }

    public string? RuntimeArtifactVersion { get; init; }

    /// <summary>
    /// The WorkerProcessHost artifact the observed process was launched with, under the same
    /// live-process rule as <see cref="RuntimeArtifactId"/> (R12-F2).
    /// </summary>
    public int? RuntimeHostArtifactId { get; init; }

    public string? RuntimeHostArtifactVersion { get; init; }
}
