// File: OpenModulePlatform.Bootstrapper.Tests/UnbumpedVersionWarningTests.cs
using Xunit;

namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// Locks the distinction between a measured source change and an undeterminable one.
/// </summary>
/// <remarks>
/// Before this split both cases printed "source changed", which claimed more than the check knows.
/// A component whose project closure resolves is stamped against the files that feed it, so a
/// mismatch is measured. A component without one -- an npm app such as OpenDocViewer, which has no
/// .csproj -- falls back to the repository-wide stamp (git HEAD plus uncommitted changes), so any
/// commit in that repository forces a rebuild even when it cannot reach the artifact.
///
/// Measured 2026-08-23: builds at 08:46 and 10:20 both warned about opendocviewer-web 2.4.63 with
/// no ODV change between them -- the only ODV commit that day touched scripts/, which the web build
/// never reads. Unpacking both artifacts showed 60 files, 0 differing in content and 36 differing
/// only in zip timestamps. In the same two runs a genuine content change in ikrock_web 0.3.36
/// produced no warning at all and surfaced only when the host rejected the import. A warning that
/// is always on for one component teaches the reader to skip it -- including where it is real.
///
/// These tests fail if the two messages are collapsed back into one.
/// </remarks>
public sealed class UnbumpedVersionWarningTests
{
    [Fact]
    public void ScopedStamp_ReportsAMeasuredSourceChange()
    {
        var text = ArtifactSourceStamp.BuildUnbumpedVersionWarning("ikrock-web", "0.3.36", hasScopedStamp: true);

        Assert.Contains("source changed", text);
        Assert.Contains("ikrock-web", text);
        Assert.Contains("0.3.36", text);
        // The scoped case must not carry the repository-wide caveat; that would blur the finding.
        Assert.DoesNotContain("whole repository", text);
    }

    [Fact]
    public void WithoutScopedStamp_DoesNotClaimTheSourceChanged()
    {
        var text = ArtifactSourceStamp.BuildUnbumpedVersionWarning("opendocviewer-web", "2.4.63", hasScopedStamp: false);

        // The core of the fix: this path never measured the source, so it must not assert it changed.
        Assert.DoesNotContain("source changed", text);
        Assert.Contains("no resolvable project closure", text);
        Assert.Contains("whole repository", text);
        Assert.Contains("may be unchanged", text);
    }

    [Fact]
    public void BothCases_StillTellTheReaderWhatToDo()
    {
        // Honesty must not cost the actionable half: an unbumped version is still the risk.
        foreach (var scoped in new[] { true, false })
        {
            var text = ArtifactSourceStamp.BuildUnbumpedVersionWarning("some-web", "1.2.3", scoped);
            Assert.StartsWith("  WARN    some-web:", text);
            Assert.Contains("bump the component before deploying", text);
            Assert.Contains("the import will reject it", text);
        }
    }

    [Fact]
    public void TheTwoMessagesAreDistinct()
    {
        var scoped = ArtifactSourceStamp.BuildUnbumpedVersionWarning("c", "1.0.0", hasScopedStamp: true);
        var unscoped = ArtifactSourceStamp.BuildUnbumpedVersionWarning("c", "1.0.0", hasScopedStamp: false);

        Assert.NotEqual(scoped, unscoped);
    }
}
