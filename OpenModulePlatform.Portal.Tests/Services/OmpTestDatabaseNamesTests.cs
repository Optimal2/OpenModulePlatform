using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Per-process test database names (2026-09-03): two concurrent Portal test hosts
/// used to share one fixed database per fixture and failed each other (measured:
/// 23 + 13 failures under four-way load). The name carries the owning process so
/// runs never collide, and the sweep only drops what a dead process left behind.
/// </summary>
public sealed class OmpTestDatabaseNamesTests
{
    [Fact]
    public void ForPortalTests_IsStableWithinTheProcessAndCarriesTheOwner()
    {
        var first = OmpTestDatabaseNames.ForPortalTests("PushEvents");
        var second = OmpTestDatabaseNames.ForPortalTests("PushEvents");

        Assert.Equal(first, second);
        Assert.StartsWith(OmpTestDatabaseNames.PortalPrefix + "PushEvents_", first, StringComparison.Ordinal);
        Assert.True(OmpTestDatabaseNames.TryParseOwner(first, out var pid, out var startTicks));
        Assert.Equal(Environment.ProcessId, pid);
        Assert.True(startTicks > 0);
    }

    [Fact]
    public void ForPortalTests_DifferentSuffixesGiveDifferentNames()
    {
        Assert.NotEqual(
            OmpTestDatabaseNames.ForPortalTests("AuthResolution"),
            OmpTestDatabaseNames.ForPortalTests("SeedSqlOrdering"));
    }

    [Fact]
    public void ForPortalTests_RejectsSuffixesThatWouldBreakOwnerParsing()
    {
        Assert.Throws<ArgumentException>(() => OmpTestDatabaseNames.ForPortalTests("Config_Overlay"));
        Assert.Throws<ArgumentException>(() => OmpTestDatabaseNames.ForPortalTests(""));
    }

    [Fact]
    public void TryParseOwner_RejectsLegacyFixedNamesAndForeignNames()
    {
        Assert.False(OmpTestDatabaseNames.TryParseOwner("OpenModulePlatform_PortalTests_PushEvents", out _, out _));
        Assert.False(OmpTestDatabaseNames.TryParseOwner("OpenModulePlatform_PortalTests_PushEvents_notapid_1", out _, out _));
        Assert.False(OmpTestDatabaseNames.TryParseOwner("OmpHostAgentTests_x_1_2_abc", out _, out _));
        Assert.True(OmpTestDatabaseNames.TryParseOwner(
            OmpTestDatabaseNames.BuildPortalName("HostDrift", 4711, 638000000000000000), out var pid, out var ticks));
        Assert.Equal(4711, pid);
        Assert.Equal(638000000000000000, ticks);
    }

    [Fact]
    public void ShouldSweep_NeverDropsADatabaseOwnedByALiveProcess()
    {
        var name = OmpTestDatabaseNames.BuildPortalName("PushEvents", 4711, 1);
        var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

        var sweep = OmpTestDatabaseNames.ShouldSweep(name, now.AddDays(-30), now, (_, _) => true, out var reason);

        Assert.False(sweep);
        Assert.Contains("live process 4711", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldSweep_DropsADatabaseWhoseOwnerIsDeadAtAnyAge()
    {
        var name = OmpTestDatabaseNames.BuildPortalName("PushEvents", 4711, 1);
        var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

        var sweep = OmpTestDatabaseNames.ShouldSweep(name, now.AddMinutes(-1), now, (_, _) => false, out var reason);

        Assert.True(sweep);
        Assert.Contains("no longer running", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldSweep_UsesTheAgeRuleWhenTheOwnerIsUnknownOrUndeterminable()
    {
        var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        const string legacy = "OpenModulePlatform_PortalTests_PushEvents";
        var tagged = OmpTestDatabaseNames.BuildPortalName("PushEvents", 4711, 1);

        Assert.False(OmpTestDatabaseNames.ShouldSweep(legacy, now.AddHours(-1), now, (_, _) => null, out _));
        Assert.True(OmpTestDatabaseNames.ShouldSweep(legacy, now.AddHours(-25), now, (_, _) => null, out _));
        Assert.False(OmpTestDatabaseNames.ShouldSweep(tagged, now.AddHours(-1), now, (_, _) => null, out _));
        Assert.True(OmpTestDatabaseNames.ShouldSweep(tagged, now.AddHours(-25), now, (_, _) => null, out _));
    }

    [Fact]
    public void ShouldSweep_IgnoresDatabasesOutsideThePortalPrefix()
    {
        var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(OmpTestDatabaseNames.ShouldSweep("OmpHostAgentTests_x_1_2_abc", now.AddDays(-30), now, (_, _) => false, out var reason));
        Assert.Equal("not a Portal test database", reason);
    }
}
