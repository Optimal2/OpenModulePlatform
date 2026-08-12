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

    public bool DrainTimeoutLogged { get; set; }

    /// <summary>
    /// When the drain-timeout warning was last emitted, so a wedged drain keeps
    /// producing a periodic signal instead of falling silent after one line (R6-F6).
    /// </summary>
    public DateTimeOffset? DrainTimeoutLastLoggedUtc { get; set; }

    public int? ProcessId => Process?.HasExited == false ? Process.Id : null;

    public DateTimeOffset? LastStartUtc { get; private set; }

    public DateTimeOffset? LastExitUtc { get; private set; }

    public int? LastExitCode { get; private set; }

    public bool ExitObserved { get; private set; }

    public bool StopRequested { get; private set; }

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
        EventWaitHandle? drainEvent = null,
        EventWaitHandle? busyEvent = null)
    {
        Process = process;
        ShutdownEvent = shutdownEvent;
        DrainEvent = drainEvent;
        BusyEvent = busyEvent;
        LastStartUtc = startedUtc;
        ExitObserved = false;
        StopRequested = false;
        LastExitCode = null;
        LastExitUtc = null;
        DrainStartedUtc = null;
        DrainTimeoutLogged = false;
        DrainTimeoutLastLoggedUtc = null;
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
        DrainTimeoutLogged = false;
        DrainTimeoutLastLoggedUtc = null;
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
        DrainTimeoutLogged = false;
        DrainTimeoutLastLoggedUtc = null;
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
        DrainTimeoutLogged = false;
        DrainTimeoutLastLoggedUtc = null;
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
