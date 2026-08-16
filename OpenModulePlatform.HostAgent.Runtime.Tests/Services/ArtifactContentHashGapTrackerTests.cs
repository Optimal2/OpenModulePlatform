using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// R12-F12. The missing-hash signal used to be one Error line per artifact per provisioning
/// cycle and nothing else: never a count, so the gap could not be driven to zero, and never
/// a word about artifacts this host does not provision. These fix the aggregate that
/// replaced it.
/// </summary>
public sealed class ArtifactContentHashGapTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstObservationOnlyStartsTheClock()
    {
        var tracker = new ArtifactContentHashGapTracker();

        var observation = tracker.Observe(1, hasContentHash: false, Start);

        // The artifact itself is news and is reported per artifact, but one artifact seen is
        // not a measurement of the host, so no aggregate is stated yet.
        Assert.True(observation.IsNewlyMissingContentHash);
        Assert.Null(observation.Audit);
    }

    [Fact]
    public void AnArtifactLosingItsHashIsReportedOnceAndThenStaysQuiet()
    {
        var tracker = new ArtifactContentHashGapTracker();
        tracker.Observe(1, hasContentHash: true, Start);

        var first = tracker.Observe(2, hasContentHash: false, Start.AddSeconds(30));
        var second = tracker.Observe(2, hasContentHash: false, Start.AddSeconds(60));
        var third = tracker.Observe(2, hasContentHash: false, Start.AddSeconds(90));

        Assert.True(first.IsNewlyMissingContentHash);
        Assert.False(second.IsNewlyMissingContentHash);
        Assert.False(third.IsNewlyMissingContentHash);
    }

    [Fact]
    public void TheAggregateNamesEveryArtifactMissingAHash()
    {
        var tracker = new ArtifactContentHashGapTracker();
        tracker.Observe(1, hasContentHash: true, Start);
        tracker.Observe(7, hasContentHash: false, Start.AddSeconds(1));

        var observation = tracker.Observe(4, hasContentHash: false, Start.AddSeconds(2));

        var audit = Assert.IsType<ArtifactContentHashGapAudit>(observation.Audit);
        Assert.Equal([4, 7], audit.MissingContentHashArtifactIds);
        Assert.Equal(3, audit.ObservedArtifactCount);
    }

    [Fact]
    public void AnUnchangedCountIsNotRepeatedUntilTheIntervalElapses()
    {
        var tracker = new ArtifactContentHashGapTracker();
        tracker.Observe(1, hasContentHash: true, Start);
        tracker.Observe(2, hasContentHash: false, Start.AddSeconds(30));

        var quiet = tracker.Observe(2, hasContentHash: false, Start.AddMinutes(30));
        var afterInterval = tracker.Observe(
            2,
            hasContentHash: false,
            Start.AddSeconds(30) + ArtifactContentHashGapTracker.AuditInterval);

        Assert.Null(quiet.Audit);
        var audit = Assert.IsType<ArtifactContentHashGapAudit>(afterInterval.Audit);
        Assert.Equal([2], audit.MissingContentHashArtifactIds);
    }

    [Fact]
    public void FillingInAMissingHashDropsTheCountImmediately()
    {
        // The point of the whole exercise: an operator who records the hash must see the
        // number move, or "drive it to zero" is not something the log can confirm.
        var tracker = new ArtifactContentHashGapTracker();
        tracker.Observe(1, hasContentHash: true, Start);
        tracker.Observe(2, hasContentHash: false, Start.AddSeconds(30));

        var observation = tracker.Observe(2, hasContentHash: true, Start.AddSeconds(60));

        var audit = Assert.IsType<ArtifactContentHashGapAudit>(observation.Audit);
        Assert.Empty(audit.MissingContentHashArtifactIds);
        Assert.Equal(2, audit.ObservedArtifactCount);
    }

    [Fact]
    public void AnArtifactThisHostStoppedProvisioningIsEvicted()
    {
        // Without eviction a hash-less artifact that stops being desired here would hold the
        // count above zero for the life of the service, and the target would be unreachable
        // by construction.
        var tracker = new ArtifactContentHashGapTracker();
        tracker.Observe(1, hasContentHash: true, Start);
        tracker.Observe(2, hasContentHash: false, Start.AddSeconds(30));

        var observation = tracker.Observe(
            1,
            hasContentHash: true,
            Start.AddSeconds(30) + ArtifactContentHashGapTracker.RetentionWindow.Add(TimeSpan.FromMinutes(1)));

        var audit = Assert.IsType<ArtifactContentHashGapAudit>(observation.Audit);
        Assert.Empty(audit.MissingContentHashArtifactIds);
        Assert.Equal(1, audit.ObservedArtifactCount);
    }

    [Fact]
    public void AnArtifactStillObservedInsideTheWindowIsNotEvicted()
    {
        // The pass-through half of the eviction rule (metod 4.5): the sweep must not drop an
        // artifact that is simply provisioned less often than the retention window is long.
        var tracker = new ArtifactContentHashGapTracker();
        tracker.Observe(1, hasContentHash: true, Start);
        tracker.Observe(2, hasContentHash: false, Start.AddSeconds(30));
        tracker.Observe(2, hasContentHash: false, Start + ArtifactContentHashGapTracker.RetentionWindow);

        var observation = tracker.Observe(
            1,
            hasContentHash: true,
            Start + ArtifactContentHashGapTracker.RetentionWindow + ArtifactContentHashGapTracker.AuditInterval);

        var audit = Assert.IsType<ArtifactContentHashGapAudit>(observation.Audit);
        Assert.Equal([2], audit.MissingContentHashArtifactIds);
    }

    [Fact]
    public void AnArtifactRegressingToNoHashIsLoudAgain()
    {
        var tracker = new ArtifactContentHashGapTracker();
        tracker.Observe(1, hasContentHash: true, Start);
        tracker.Observe(2, hasContentHash: false, Start.AddSeconds(30));
        tracker.Observe(2, hasContentHash: true, Start.AddSeconds(60));

        var observation = tracker.Observe(2, hasContentHash: false, Start.AddSeconds(90));

        Assert.True(observation.IsNewlyMissingContentHash);
        var audit = Assert.IsType<ArtifactContentHashGapAudit>(observation.Audit);
        Assert.Equal([2], audit.MissingContentHashArtifactIds);
    }
}
