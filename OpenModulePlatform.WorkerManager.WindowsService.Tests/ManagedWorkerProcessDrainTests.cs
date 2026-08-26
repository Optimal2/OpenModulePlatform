// File: OpenModulePlatform.WorkerManager.WindowsService.Tests/ManagedWorkerProcessDrainTests.cs
using System.Diagnostics;
using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Runtime;

namespace OpenModulePlatform.WorkerManager.WindowsService.Tests;

/// <summary>
/// The drain lifecycle and its regression patterns. Three drain defects shipped on
/// three consecutive review rounds (R5-F1: a drain never cancelled when its trigger
/// was reverted; R6-F6/W6: a drain deadline that only logged and never expired;
/// R7-F1: a preflight branch that left an already-started drain set forever), and
/// none of them would have survived a test. R7-F7 then added the missing piece of
/// visibility: a draining worker now publishes Draining, not Running.
///
/// Like the host-witness tests these attach Process.GetCurrentProcess() and
/// unregistered events; they must never touch the stop/kill paths.
/// </summary>
public sealed class ManagedWorkerProcessDrainTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(60);

    private static DesiredWorkerInstance CreateDefinition() => new()
    {
        AppInstanceId = new Guid("11111111-1111-1111-1111-111111111111"),
        WorkerInstanceId = new Guid("22222222-2222-2222-2222-222222222222"),
        WorkerInstanceKey = "test-worker",
        WorkerTypeKey = "test.worker",
        ArtifactId = 302,
        ArtifactVersion = "0.3.128",
        PluginRelativePath = "Test.Worker.dll",
        PluginAssemblyPath = @"C:\does\not\matter\Test.Worker.dll",
        ConfigurationJson = null,
        ShutdownEventName = @"Local\test-shutdown",
    };

    private static ManagedWorkerProcess CreateAttached(
        out EventWaitHandle drainEvent,
        out EventWaitHandle busyEvent)
    {
        var managed = new ManagedWorkerProcess(CreateDefinition());
        drainEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        busyEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        managed.AttachProcess(
            Process.GetCurrentProcess(),
            new EventWaitHandle(false, EventResetMode.ManualReset),
            DateTimeOffset.UtcNow,
            workerProcessHost: null,
            drainEvent,
            busyEvent);
        return managed;
    }

    [Fact]
    public void A_worker_without_a_drain_publishes_Running()
    {
        var managed = CreateAttached(out _, out _);

        Assert.False(managed.IsDraining);
        Assert.Equal(WorkerObservedStates.Running, managed.GetRunningObservationState());
    }

    [Fact]
    public void A_draining_worker_publishes_Draining_not_Running()
    {
        // R7-F7. The drain used to be invisible: the worker held its process,
        // admitted zero jobs, and still reported Running to the Portal and the
        // deployment gate.
        var managed = CreateAttached(out var drainEvent, out _);

        Assert.True(managed.BeginDrain(DateTimeOffset.UtcNow));

        Assert.True(managed.IsDraining);
        Assert.True(drainEvent.WaitOne(0));
        Assert.Equal(WorkerObservedStates.Draining, managed.GetRunningObservationState());
    }

    [Fact]
    public void BeginDrain_is_idempotent_for_the_same_process()
    {
        var managed = CreateAttached(out _, out _);
        var startedUtc = DateTimeOffset.UtcNow;

        Assert.True(managed.BeginDrain(startedUtc));
        Assert.False(managed.BeginDrain(startedUtc.AddSeconds(30)));
        Assert.Equal(startedUtc, managed.DrainStartedUtc);
    }

    [Fact]
    public void A_reverted_change_cancels_the_drain_and_the_worker_resumes()
    {
        // R5-F1. The configuration change that started the drain is reverted before
        // the in-flight job completes: the drain event must be reset and the worker
        // must go back to admitting work AND to reporting Running. Before the fix
        // the event stayed set and the worker idled forever.
        var managed = CreateAttached(out var drainEvent, out _);
        Assert.True(managed.BeginDrain(DateTimeOffset.UtcNow));

        Assert.True(managed.CancelDrain());

        Assert.False(managed.IsDraining);
        Assert.Null(managed.DrainStartedUtc);
        Assert.False(drainEvent.WaitOne(0));
        Assert.Equal(WorkerObservedStates.Running, managed.GetRunningObservationState());

        // A second cancel is a no-op, not an error.
        Assert.False(managed.CancelDrain());
    }

    [Fact]
    public void A_blocked_restart_can_cancel_a_drain_that_already_began()
    {
        // R7-F1. The preflight branch sits ABOVE the drain call: once the new
        // definition proves unstartable the drain path is never re-entered, so the
        // branch itself must cancel the pending drain. The observable contract at
        // this level: BeginDrain followed by CancelDrain leaves no trace.
        var managed = CreateAttached(out var drainEvent, out _);
        Assert.True(managed.BeginDrain(DateTimeOffset.UtcNow));

        Assert.True(managed.CancelDrain());

        Assert.False(managed.IsDraining);
        Assert.False(drainEvent.WaitOne(0));
        // ... and a later, startable change can drain again from a clean slate.
        Assert.True(managed.BeginDrain(DateTimeOffset.UtcNow));
        Assert.True(managed.IsDraining);
    }

    [Fact]
    public void A_busy_worker_inside_the_deadline_holds_the_restart()
    {
        // The whole point of drain: an in-flight job is never interrupted inside
        // the deadline.
        var managed = CreateAttached(out _, out var busyEvent);
        busyEvent.Set();
        Assert.True(managed.BeginDrain(DateTimeOffset.UtcNow));

        Assert.True(managed.IsBusy());
        Assert.False(managed.IsDrainTimedOut(DateTimeOffset.UtcNow, DrainTimeout));
    }

    [Fact]
    public void A_busy_worker_past_the_deadline_lets_the_restart_proceed()
    {
        // R6-F6/W6. The timeout used to only log, forever: a worker whose busy flag
        // was stuck admitted no jobs ever again. Past the deadline the restart
        // proceeds and the job returns to pending when its lease expires.
        var managed = CreateAttached(out _, out var busyEvent);
        busyEvent.Set();
        var startedUtc = DateTimeOffset.UtcNow - DrainTimeout - TimeSpan.FromSeconds(1);
        Assert.True(managed.BeginDrain(startedUtc));

        Assert.True(managed.IsDrainTimedOut(DateTimeOffset.UtcNow, DrainTimeout));
    }

    [Fact]
    public void An_idle_draining_worker_is_never_timed_out()
    {
        // No busy flag means the drain is already complete; the restart proceeds
        // through the not-busy branch, never through the timeout.
        var managed = CreateAttached(out _, out _);
        Assert.True(managed.BeginDrain(DateTimeOffset.UtcNow - DrainTimeout - TimeSpan.FromSeconds(1)));

        Assert.False(managed.IsBusy());
        Assert.False(managed.IsDrainTimedOut(DateTimeOffset.UtcNow, DrainTimeout));
    }

    [Fact]
    public void A_worker_without_drain_support_never_drains()
    {
        // Legacy workers have no drain event: BeginDrain is a no-op and the old
        // immediate stop-and-replace behavior is kept for them.
        var managed = new ManagedWorkerProcess(CreateDefinition());

        Assert.False(managed.SupportsDrain);
        Assert.False(managed.BeginDrain(DateTimeOffset.UtcNow));
        Assert.False(managed.IsDraining);
        Assert.False(managed.CancelDrain());
    }

    [Fact]
    public void A_restart_clears_the_drain_state_with_the_process()
    {
        // After the restart the drain belongs to the dead process. AttachProcess
        // resetting DrainStartedUtc is what lets the NEW process accept jobs.
        var managed = CreateAttached(out _, out _);
        Assert.True(managed.BeginDrain(DateTimeOffset.UtcNow));
        Assert.True(managed.IsDraining);

        managed.AttachProcess(
            Process.GetCurrentProcess(),
            new EventWaitHandle(false, EventResetMode.ManualReset),
            DateTimeOffset.UtcNow,
            workerProcessHost: null,
            new EventWaitHandle(false, EventResetMode.ManualReset),
            new EventWaitHandle(false, EventResetMode.ManualReset));

        Assert.False(managed.IsDraining);
        Assert.Null(managed.DrainStartedUtc);
        Assert.Equal(WorkerObservedStates.Running, managed.GetRunningObservationState());
    }
}
