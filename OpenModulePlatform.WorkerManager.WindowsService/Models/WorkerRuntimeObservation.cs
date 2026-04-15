// File: OpenModulePlatform.WorkerManager.WindowsService/Models/WorkerRuntimeObservation.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public sealed class WorkerRuntimeObservation
{
    public Guid AppInstanceId { get; init; }

    public string RuntimeKind { get; init; } = string.Empty;

    public string WorkerTypeKey { get; init; } = string.Empty;

    public byte ObservedState { get; init; } = WorkerObservedStates.Unknown;

    public int? ProcessId { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }

    public DateTimeOffset? LastSeenUtc { get; init; }

    public DateTimeOffset? LastExitUtc { get; init; }

    public int? LastExitCode { get; init; }

    public string? StatusMessage { get; init; }
}
