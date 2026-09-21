// File: OpenModulePlatform.HostAgent.Runtime.Tests/WorkerTelemetryObservedStateGuardTests.cs
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.HostAgent.Runtime.Tests;

/// <summary>
/// R7-F7 follow-through. The resource telemetry collector samples the live worker
/// processes listed in omp.WorkerInstanceRuntimeStates, keyed by ObservedState.
/// When WorkerManager gained the Draining (6) state, a draining worker still owns
/// a live process burning CPU and memory on its in-flight job -- leaving 6 out of
/// the sampling set would blind the collector exactly while the worker finishes
/// its heaviest work. This guard pins the fixed set.
/// </summary>
public sealed class WorkerTelemetryObservedStateGuardTests
{
    [Fact]
    public void The_telemetry_sampling_set_includes_Draining()
    {
        var source = Normalize(OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.HostAgent.Runtime", "Services", "OmpHostArtifactRepository.ResourceTelemetry.cs"));

        Assert.Contains("rs.ObservedState IN (1, 2, 3, 6)", source);
        // The pre-fix set must be gone -- not just accompanied by the fixed one.
        Assert.DoesNotContain("rs.ObservedState IN (1, 2, 3);", source);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
