// File: OpenModulePlatform.WorkerManager.WindowsService.Tests/WorkerManagerWiringGuardTests.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Tests;

/// <summary>
/// Source guards for the parts of the fixes that live in code paths no unit test
/// can drive without a Windows service, a WMI provider, or a SQL Server: WMI
/// disposal (R7-F5) and the reconcile-loop wiring of the drain fixes
/// (R7-F1, W6, R7-F7). Each guard names the pre-fix shape it forbids, so reverting
/// the fix -- not just renaming something -- is what fails the test.
/// </summary>
public sealed class WorkerManagerWiringGuardTests
{
    [Fact]
    public void The_service_name_wmi_lookup_disposes_the_collection_and_every_object()
    {
        // R7-F5. The old LINQ chain leaked the ManagementObjectCollection and every
        // ManagementObject in it to the finalizer on every call. The fixed shape
        // disposes both, matching the orphan scan in the same service.
        var client = Normalize(ReadRepositoryTextFile(
            "OpenModulePlatform.WorkerManager.WindowsService", "Services", "HostAgentRpcClient.cs"));

        Assert.Contains("using var services = searcher.Get();", client);
        Assert.Contains("using (service)", client);
        // The leaky chain must be gone -- not just accompanied by a disposed copy.
        Assert.DoesNotContain(".OfType<ManagementObject>()", client);
    }

    [Fact]
    public void The_running_observation_is_routed_through_the_drain_aware_state()
    {
        // R7-F7. The heartbeat publisher must not hard-code Running again; the
        // drain-aware selection lives on ManagedWorkerProcess where it is
        // unit-tested.
        var service = Normalize(ReadRepositoryTextFile(
            "OpenModulePlatform.WorkerManager.WindowsService", "Services", "WorkerManagerHostedService.cs"));

        Assert.Contains("managed.GetRunningObservationState()", service);
    }

    [Fact]
    public void The_drain_timeout_decision_is_the_tested_predicate()
    {
        // W6 (R6-F6). The timeout decision used to be an inline expression that
        // only logged; it is now the unit-tested IsDrainTimedOut predicate, and the
        // restart proceeds when it fires.
        var service = Normalize(ReadRepositoryTextFile(
            "OpenModulePlatform.WorkerManager.WindowsService", "Services", "WorkerManagerHostedService.cs"));

        Assert.Contains("managed.IsDrainTimedOut(nowUtc, drainTimeout)", service);
    }

    [Fact]
    public void Both_branches_that_must_cancel_a_pending_drain_do_so()
    {
        // R5-F1 (the change was reverted before the drain completed) and R7-F1 (the
        // new definition proved unstartable above the drain path) are the two ways
        // a begun drain becomes permanent unless that branch cancels it. Both
        // branches must call CancelDrain -- one call site means one of the two
        // leaks again.
        var service = Normalize(ReadRepositoryTextFile(
            "OpenModulePlatform.WorkerManager.WindowsService", "Services", "WorkerManagerHostedService.cs"));

        var occurrences = service.Split("managed.CancelDrain()", StringSplitOptions.None).Length - 1;
        Assert.True(occurrences >= 2, $"Expected both drain-cancelling branches, found {occurrences} CancelDrain call(s).");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "OpenModulePlatform.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
    }

    private static string ReadRepositoryTextFile(params string[] relativePathSegments)
    {
        var rootedSegment = relativePathSegments.FirstOrDefault(Path.IsPathRooted);
        if (rootedSegment is not null)
        {
            throw new ArgumentException("Repository test paths must be relative.", nameof(rootedSegment));
        }

        var segments = new string[relativePathSegments.Length + 1];
        segments[0] = FindRepositoryRoot();
        Array.Copy(relativePathSegments, 0, segments, 1, relativePathSegments.Length);
        return File.ReadAllText(Path.Join(segments));
    }
}
