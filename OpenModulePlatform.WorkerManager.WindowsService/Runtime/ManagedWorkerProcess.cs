// File: OpenModulePlatform.WorkerManager.WindowsService/Runtime/ManagedWorkerProcess.cs
using System.Diagnostics;
using OpenModulePlatform.WorkerManager.WindowsService.Models;

namespace OpenModulePlatform.WorkerManager.WindowsService.Runtime;

/// <summary>
/// Represents a supervised worker process entry in the manager.
/// </summary>
public sealed class ManagedWorkerProcess
{
    private readonly Queue<DateTimeOffset> _restartAttempts = new();

    public ManagedWorkerProcess(DesiredWorkerInstance definition)
    {
        Definition = definition;
    }

    public DesiredWorkerInstance Definition { get; private set; }

    public Process? Process { get; private set; }

    public EventWaitHandle? ShutdownEvent { get; private set; }

    public EventWaitHandle? DrainEvent { get; private set; }

    public EventWaitHandle? BusyEvent { get; private set; }

    public DateTimeOffset? DrainStartedUtc { get; private set; }

    public int? ProcessId => Process?.HasExited == false ? Process.Id : null;

    public DateTimeOffset? LastStartUtc { get; private set; }

    public DateTimeOffset? LastExitUtc { get; private set; }

    public int? LastExitCode { get; private set; }

    public bool ExitObserved { get; private set; }

    public bool StopRequested { get; private set; }

    /// <summary>
    /// The artifact the CURRENT process was started from (R12-F2).
    /// </summary>
    /// <remarks>
    /// Captured from the definition at AttachProcess rather than read from
    /// <see cref="Definition"/> when an observation is published, because the two stop
    /// agreeing exactly when it matters: a worker whose restart is blocked (drain that
    /// never completes, restart-policy backoff, an unstartable replacement definition)
    /// keeps a process from the old artifact while the manager already holds a newer
    /// definition. Publishing the definition would then report a version nothing is
    /// running, which is the failure the witness exists to expose, reintroduced inside
    /// the witness itself.
    /// </remarks>
    public int? StartedArtifactId { get; private set; }

    public string? StartedArtifactVersion { get; private set; }

    /// <summary>
    /// The WorkerProcessHost artifact the current process was launched with (R12-F2).
    /// </summary>
    /// <remarks>
    /// Captured per process for the same reason as the worker's own artifact: the host
    /// executable is re-resolved on every start, so the only build a running process can be
    /// said to be using is the one that was resolved when it started. Null when the path came
    /// from configuration instead of the artifact catalogue.
    /// </remarks>
    public int? StartedHostArtifactId { get; private set; }

    public string? StartedHostArtifactVersion { get; private set; }

    public bool HasEquivalentConfiguration(DesiredWorkerInstance desired)
    {
        return Definition.HasEquivalentConfiguration(desired);
    }

    public void UpdateDefinition(DesiredWorkerInstance desired)
    {
        Definition = desired;
    }

    public bool IsRunning()
    {
        return Process is { HasExited: false };
    }

    public void AttachProcess(
        Process process,
        EventWaitHandle shutdownEvent,
        DateTimeOffset startedUtc,
        ResolvedWorkerProcessHost? workerProcessHost = null,
        EventWaitHandle? drainEvent = null,
        EventWaitHandle? busyEvent = null)
    {
        Process = process;
        ShutdownEvent = shutdownEvent;
        DrainEvent = drainEvent;
        BusyEvent = busyEvent;
        // R12-F2. Freeze the artifact identity at the moment the process exists; from here
        // on the definition may move ahead of it without the running process changing.
        StartedArtifactId = Definition.ArtifactId;
        StartedArtifactVersion = Definition.ArtifactVersion;
        StartedHostArtifactId = workerProcessHost?.ArtifactId;
        StartedHostArtifactVersion = workerProcessHost?.Version;
        LastStartUtc = startedUtc;
        ExitObserved = false;
        StopRequested = false;
        LastExitCode = null;
        LastExitUtc = null;
        DrainStartedUtc = null;
    }

    /// <summary>
    /// Signals the worker to finish in-flight jobs and start no new ones.
    /// Returns true the first time the drain begins for the current process.
    /// </summary>
    public bool BeginDrain(DateTimeOffset nowUtc)
    {
        if (DrainEvent is null)
        {
            return false;
        }

        if (DrainStartedUtc.HasValue)
        {
            return false;
        }

        DrainEvent.Set();
        DrainStartedUtc = nowUtc;
        return true;
    }

    /// <summary>
    /// Resets an in-progress drain when the configuration change that triggered it
    /// was reverted to the running configuration before the in-flight job finished.
    /// Without this the drain event stays set and the worker starts no new jobs yet
    /// keeps reporting Running, idling indefinitely (R5-F1). Returns true when a
    /// pending drain was actually cleared.
    /// </summary>
    public bool CancelDrain()
    {
        if (!DrainStartedUtc.HasValue)
        {
            return false;
        }

        DrainEvent?.Reset();
        DrainStartedUtc = null;
        return true;
    }

    /// <summary>
    /// True while the worker reports an in-flight job through its busy event.
    /// Workers without drain support never set the busy event and read as idle,
    /// which keeps the pre-drain restart behavior for them.
    /// </summary>
    public bool IsBusy()
    {
        return BusyEvent?.WaitOne(0) == true;
    }

    public bool SupportsDrain => DrainEvent is not null;

    /// <summary>
    /// True while a drain is in progress for the current process: the drain event is
    /// set and the worker is finishing its in-flight job without admitting new work.
    /// </summary>
    public bool IsDraining => DrainStartedUtc.HasValue;

    /// <summary>
    /// The observed state a still-running worker should publish (R7-F7): Draining
    /// while a drain is in progress, Running otherwise. A draining worker keeps
    /// heartbeating on the same cadence either way -- only the claim changes.
    /// </summary>
    public byte GetRunningObservationState()
        => IsDraining ? WorkerObservedStates.Draining : WorkerObservedStates.Running;

    /// <summary>
    /// True when a drain began, the worker still reports busy, and the configured
    /// deadline has passed. The restart then proceeds and the in-flight job returns
    /// to pending when its lease expires (W6) -- a bounded, self-healing outcome,
    /// unlike a drain that never ends.
    /// </summary>
    public bool IsDrainTimedOut(DateTimeOffset nowUtc, TimeSpan drainTimeout)
    {
        return DrainStartedUtc.HasValue
            && IsBusy()
            && nowUtc - DrainStartedUtc.Value >= drainTimeout;
    }

    public void RecordStartAttempt(DateTimeOffset nowUtc, TimeSpan restartWindow)
    {
        _restartAttempts.Enqueue(nowUtc);
        TrimRestartAttempts(nowUtc, restartWindow);
    }


    public DateTimeOffset? GetNextEligibleStartUtc(DateTimeOffset nowUtc, TimeSpan restartWindow, int maxRestartsPerWindow)
    {
        TrimRestartAttempts(nowUtc, restartWindow);

        if (_restartAttempts.Count < maxRestartsPerWindow)
        {
            return nowUtc;
        }

        return _restartAttempts.Peek().Add(restartWindow);
    }

    public bool NeedsExitObservation()
    {
        return Process is { HasExited: true } && !ExitObserved;
    }

    public bool ObserveExitIfNeeded()
    {
        var process = Process;
        if (process is null || !process.HasExited || ExitObserved)
        {
            return false;
        }

        LastExitUtc = DateTimeOffset.UtcNow;
        LastExitCode = process.ExitCode;
        ExitObserved = true;
        process.Dispose();
        Process = null;
        DisposeNamedEvents();
        return true;
    }

    private void DisposeNamedEvents()
    {
        ShutdownEvent?.Dispose();
        ShutdownEvent = null;
        DrainEvent?.Dispose();
        DrainEvent = null;
        BusyEvent?.Dispose();
        BusyEvent = null;
        DrainStartedUtc = null;
    }

    public async Task<bool> RequestStopAsync(TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        StopRequested = true;
        ShutdownEvent?.Set();

        var process = Process;
        if (process is null)
        {
            DisposeNamedEvents();
            return true;
        }

        if (process.HasExited)
        {
            ObserveExitIfNeeded();
            return true;
        }

        if (await WaitForProcessExitAsync(process, stopTimeout, cancellationToken))
        {
            ObserveExitIfNeeded();
            return true;
        }

        return false;
    }

    public async Task<bool> KillAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var process = Process;
        if (process is null)
        {
            DisposeNamedEvents();
            return true;
        }

        if (process.HasExited)
        {
            ObserveExitIfNeeded();
            return true;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            ObserveExitIfNeeded();
            return true;
        }

        if (await WaitForProcessExitAsync(process, timeout, cancellationToken))
        {
            ObserveExitIfNeeded();
            return true;
        }

        return false;
    }

    private static async Task<bool> WaitForProcessExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(waitCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void TrimRestartAttempts(DateTimeOffset nowUtc, TimeSpan restartWindow)
    {
        while (_restartAttempts.Count > 0 && nowUtc - _restartAttempts.Peek() >= restartWindow)
        {
            _restartAttempts.Dequeue();
        }
    }
}
