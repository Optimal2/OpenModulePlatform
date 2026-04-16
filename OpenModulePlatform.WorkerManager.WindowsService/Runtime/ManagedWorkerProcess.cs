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

    public void AttachProcess(Process process, EventWaitHandle shutdownEvent, DateTimeOffset startedUtc)
    {
        Process = process;
        ShutdownEvent = shutdownEvent;
        LastStartUtc = startedUtc;
        ExitObserved = false;
        StopRequested = false;
        LastExitCode = null;
        LastExitUtc = null;
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
        ShutdownEvent?.Dispose();
        ShutdownEvent = null;
        return true;
    }

    public async Task<bool> RequestStopAsync(TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        StopRequested = true;
        ShutdownEvent?.Set();

        var process = Process;
        if (process is null)
        {
            ShutdownEvent?.Dispose();
            ShutdownEvent = null;
            return true;
        }

        if (process.HasExited)
        {
            ObserveExitIfNeeded();
            return true;
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(stopTimeout, cancellationToken);
            ObserveExitIfNeeded();
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void Kill()
    {
        if (Process is { HasExited: false })
        {
            Process.Kill(entireProcessTree: true);
            Process.WaitForExit();
        }

        ObserveExitIfNeeded();
    }

    private void TrimRestartAttempts(DateTimeOffset nowUtc, TimeSpan restartWindow)
    {
        while (_restartAttempts.Count > 0 && nowUtc - _restartAttempts.Peek() >= restartWindow)
        {
            _restartAttempts.Dequeue();
        }
    }
}
