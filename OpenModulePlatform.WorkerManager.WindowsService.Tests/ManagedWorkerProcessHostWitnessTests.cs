// File: OpenModulePlatform.WorkerManager.WindowsService.Tests/ManagedWorkerProcessHostWitnessTests.cs
using System.Diagnostics;
using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Runtime;

namespace OpenModulePlatform.WorkerManager.WindowsService.Tests;

/// <summary>
/// The per-process host witness (R12-F2) and the reconcile decision built on it.
/// These tests attach Process.GetCurrentProcess() and unregistered events; they must
/// never touch the stop/kill paths — the attached process is the test runner itself.
/// </summary>
public sealed class ManagedWorkerProcessHostWitnessTests
{
    private static DesiredWorkerInstance CreateDefinition(string artifactVersion = "0.3.128") => new()
    {
        AppInstanceId = new Guid("11111111-1111-1111-1111-111111111111"),
        WorkerInstanceId = new Guid("22222222-2222-2222-2222-222222222222"),
        WorkerInstanceKey = "test-worker",
        WorkerTypeKey = "test.worker",
        ArtifactId = 302,
        ArtifactVersion = artifactVersion,
        PluginRelativePath = "Test.Worker.dll",
        PluginAssemblyPath = @"C:\does\not\matter\Test.Worker.dll",
        ConfigurationJson = null,
        ShutdownEventName = @"Local\test-shutdown",
    };

    private static ManagedWorkerProcess CreateAttached(
        ResolvedWorkerProcessHost? host,
        out EventWaitHandle drainEvent)
    {
        var managed = new ManagedWorkerProcess(CreateDefinition());
        drainEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        managed.AttachProcess(
            Process.GetCurrentProcess(),
            new EventWaitHandle(false, EventResetMode.ManualReset),
            DateTimeOffset.UtcNow,
            host,
            drainEvent,
            new EventWaitHandle(false, EventResetMode.ManualReset));
        return managed;
    }

    [Fact]
    public void AttachProcess_freezes_the_host_witness()
    {
        var managed = CreateAttached(
            new ResolvedWorkerProcessHost(@"C:\hosts\WorkerProcessHost.exe", 43, "0.3.43"),
            out _);

        Assert.Equal(43, managed.StartedHostArtifactId);
        Assert.Equal("0.3.43", managed.StartedHostArtifactVersion);
    }

    [Fact]
    public void AttachProcess_with_a_configured_path_leaves_the_witness_unknown()
    {
        // A configured WorkerManager:WorkerProcessPath names a file and nothing else:
        // ResolvedWorkerProcessHost(path, null, null). Unverifiable must read as
        // unverifiable — and RequiresHostRestart must then stay permanently false.
        var managed = CreateAttached(
            new ResolvedWorkerProcessHost(@"C:\configured\WorkerProcessHost.exe", null, null),
            out _);

        Assert.Null(managed.StartedHostArtifactId);
        Assert.Null(managed.StartedHostArtifactVersion);
        Assert.False(HostRestartCheck.RequiresHostRestart(managed.StartedHostArtifactId, 43));
    }

    [Fact]
    public void The_witness_survives_definition_drift()
    {
        // R12-F2 regression guard: the witness describes the RUNNING process. Updating
        // the definition (as the reconcile loop does after a drain) must not move it.
        var managed = CreateAttached(
            new ResolvedWorkerProcessHost(@"C:\hosts\WorkerProcessHost.exe", 42, "0.3.42"),
            out _);

        managed.UpdateDefinition(CreateDefinition());

        Assert.Equal(42, managed.StartedHostArtifactId);
        Assert.Equal("0.3.42", managed.StartedHostArtifactVersion);
    }

    [Fact]
    public void Todays_bug_an_identical_definition_hides_a_host_upgrade()
    {
        // The production symptom of 2026-08-23: workers ran host build 0.3.42 while
        // 0.3.43 was desired, and nothing ever restarted them. The definition alone
        // cannot see it — host identity is deliberately NOT part of the model — so the
        // combined reconcile decision must include the witness-vs-desired predicate.
        var managed = CreateAttached(
            new ResolvedWorkerProcessHost(@"C:\hosts\WorkerProcessHost.exe", 42, "0.3.42"),
            out _);
        var identicalDesired = CreateDefinition();
        int? desiredHostArtifactId = 43;

        // The definition comparison sees no difference — that part is BY DESIGN.
        Assert.True(managed.HasEquivalentConfiguration(identicalDesired));

        // The combined decision, as the reconcile loop now forms it, must fire.
        var restartNeeded = !managed.HasEquivalentConfiguration(identicalDesired)
            || (managed.IsRunning() && HostRestartCheck.RequiresHostRestart(
                    managed.StartedHostArtifactId, desiredHostArtifactId));

        Assert.True(restartNeeded);
    }

    [Fact]
    public void A_worker_and_host_change_in_the_same_package_is_one_decision()
    {
        // The normal deploy shape: one universal package moves both the worker
        // artifact and the host. The combined predicate is a single disjunction —
        // one branch, one drain, one restart covering both changes.
        var managed = CreateAttached(
            new ResolvedWorkerProcessHost(@"C:\hosts\WorkerProcessHost.exe", 42, "0.3.42"),
            out _);
        var changedDesired = CreateDefinition(artifactVersion: "0.3.129");

        var restartNeeded = !managed.HasEquivalentConfiguration(changedDesired)
            || (managed.IsRunning() && HostRestartCheck.RequiresHostRestart(
                    managed.StartedHostArtifactId, 43));

        Assert.True(restartNeeded);
    }

    [Fact]
    public void A_host_rollback_cancels_a_pending_drain()
    {
        // R5-F1 mechanics for the host path: host bumped 42 -> 43 starts a drain; the
        // operator rolls back to 42 before the drain completes. The combined predicate
        // turns false, the else branch calls CancelDrain, and the worker resumes.
        var managed = CreateAttached(
            new ResolvedWorkerProcessHost(@"C:\hosts\WorkerProcessHost.exe", 42, "0.3.42"),
            out var drainEvent);

        Assert.True(managed.BeginDrain(DateTimeOffset.UtcNow));
        Assert.True(drainEvent.WaitOne(0));

        // Rolled back: witness 42 == desired 42 again.
        Assert.False(HostRestartCheck.RequiresHostRestart(managed.StartedHostArtifactId, 42));

        Assert.True(managed.CancelDrain());
        Assert.False(drainEvent.WaitOne(0));
        Assert.Null(managed.DrainStartedUtc);
        Assert.False(managed.CancelDrain());
    }

    [Fact]
    public void A_process_that_never_started_has_no_witness_and_no_restart_opinion()
    {
        // The reconcile loop gates the host comparison on IsRunning(); a worker with
        // no process has a null witness, and null never reads as "changed".
        var managed = new ManagedWorkerProcess(CreateDefinition());

        Assert.False(managed.IsRunning());
        Assert.Null(managed.StartedHostArtifactId);
        Assert.False(HostRestartCheck.RequiresHostRestart(managed.StartedHostArtifactId, 43));
    }
}
