// File: OpenModulePlatform.WorkerManager.WindowsService.Tests/HostRestartCheckTests.cs
using OpenModulePlatform.WorkerManager.WindowsService.Models;

namespace OpenModulePlatform.WorkerManager.WindowsService.Tests;

/// <summary>
/// The pure restart predicate for worker-host upgrades. The null semantics are the
/// load-bearing part: null means "unknown", and unknown must NEVER read as "changed" —
/// a configured WorkerProcessPath yields no artifact identity at all, and a transient
/// resolve miss yields no desired identity for one cycle. Reading either as a change
/// would recycle healthy workers in a loop.
/// </summary>
public sealed class HostRestartCheckTests
{
    [Theory]
    [InlineData(null, null)] // no witness, no desired: config-path installations, forever
    [InlineData(null, 43)]   // legacy/config-path witness: unknown is not "changed"
    [InlineData(42, null)]   // resolve miss this cycle: no opinion, retry next cycle
    public void RequiresHostRestart_is_false_when_either_side_is_unknown(int? started, int? desired)
    {
        Assert.False(HostRestartCheck.RequiresHostRestart(started, desired));
    }

    [Fact]
    public void RequiresHostRestart_is_false_when_the_running_host_matches_the_desired_host()
    {
        Assert.False(HostRestartCheck.RequiresHostRestart(42, 42));
    }

    [Fact]
    public void RequiresHostRestart_is_true_when_the_desired_host_artifact_differs()
    {
        Assert.True(HostRestartCheck.RequiresHostRestart(42, 43));
    }

    [Fact]
    public void RequiresHostRestart_compares_artifact_ids_not_version_strings()
    {
        // A re-uploaded artifact can carry the same version string under a new
        // ArtifactId. The deployment diagnostics compare ReportedHostArtifactId
        // against the desired ArtifactId (integers), so the restart decision must
        // do the same — otherwise the diagnostics report a Pending drift that the
        // manager never acts on. The predicate takes only ids by design: version
        // strings are log text, not identity.
        Assert.True(HostRestartCheck.RequiresHostRestart(42, 43));
        Assert.False(HostRestartCheck.RequiresHostRestart(43, 43));
    }
}
