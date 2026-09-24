// File: OpenModulePlatform.Portal.Tests/Web/PeriodPresetsTests.cs
using OpenModulePlatform.Portal.Pages.Admin;

namespace OpenModulePlatform.Portal.Tests.Web;

/// <summary>
/// The log pages resolve the range picker's preset key on the server, so a
/// rolling period keeps rolling after a reload and the neutral key means no
/// dates at all; custom or unknown keys leave the posted dates alone.
/// </summary>
public sealed class PeriodPresetsTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    [Fact]
    public void RollingKeys_ResolveFromToday()
    {
        Assert.Equal((Today, Today), PeriodPresets.Apply("today", null, null));
        Assert.Equal((Today.AddDays(-6), Today), PeriodPresets.Apply("7d", null, null));
        Assert.Equal((Today.AddDays(-29), Today), PeriodPresets.Apply("30d", null, null));
        Assert.Equal((Today.AddDays(-89), Today), PeriodPresets.Apply("90d", null, null));
    }

    [Fact]
    public void KnownKey_WinsOverTheDatesThatTravelWithIt()
    {
        var stale = new DateOnly(2020, 1, 1);
        Assert.Equal((Today.AddDays(-6), Today), PeriodPresets.Apply("7d", stale, stale));
        Assert.Equal(((DateOnly?)null, (DateOnly?)null), PeriodPresets.Apply("all", stale, stale));
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("14d")]
    public void CustomEmptyOrUnknownKey_KeepsTheDates(string? key)
    {
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 9);
        Assert.Equal((from, to), PeriodPresets.Apply(key, from, to));
    }
}
