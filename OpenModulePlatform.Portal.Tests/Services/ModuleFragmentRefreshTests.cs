using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using Xunit;
using PortalIndex = OpenModulePlatform.Portal.Pages.IndexModel;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The refresh contract between a module fragment and the dashboard: the module's
/// <c>data-widget-refresh</c> request is read and removed by the sanitizer, and the
/// dashboard's interval choices are normalized on the way in and out of storage.
/// </summary>
public sealed class ModuleFragmentRefreshTests
{
    [Fact]
    public void Sanitize_ReadsTheRefreshRequest_AndRemovesTheAttribute()
    {
        var result = ModuleFragmentHtmlSanitizer.Sanitize(
            "<div data-widget-refresh=\"60\" data-widget-mode=\"wide\"><strong>42</strong></div>",
            "/sample");

        Assert.Equal(60, result.RefreshSeconds);
        Assert.Equal("is-wide", result.ModeClass);
        Assert.DoesNotContain("data-widget-refresh", result.Html, StringComparison.Ordinal);
        Assert.Contains("<strong>42</strong>", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_WithoutTheRequest_LeavesRefreshUnset()
    {
        var result = ModuleFragmentHtmlSanitizer.Sanitize("<div><strong>42</strong></div>", "/sample");

        Assert.Null(result.RefreshSeconds);
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData(" 300 ", 300)]
    [InlineData("5", 15)]
    [InlineData("99999", 3600)]
    [InlineData("0", null)]
    [InlineData("-30", null)]
    [InlineData("soon", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void GetRefreshSeconds_ClampsWholeSecondsAndRejectsTheRest(string? value, int? expected)
    {
        Assert.Equal(expected, ModuleFragmentWidget.GetRefreshSeconds(value));
    }

    [Fact]
    public void RefreshIntervalText_NamesEveryMenuChoiceDistinctly()
    {
        var keys = DashboardRefreshIntervals.Allowed.Select(PortalIndex.RefreshIntervalText).ToArray();

        Assert.Equal(DashboardRefreshIntervals.Allowed.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Off", PortalIndex.RefreshIntervalText(DashboardRefreshIntervals.Off));
        Assert.Equal("Every minute", PortalIndex.RefreshIntervalText(DashboardRefreshIntervals.Default));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(300, 300)]
    [InlineData(45, 60)]
    [InlineData(-1, 60)]
    [InlineData(null, 60)]
    public void DashboardRefreshIntervals_Normalize_KeepsOnlyTheMenuChoices(int? seconds, int expected)
    {
        Assert.Equal(expected, DashboardRefreshIntervals.Normalize(seconds));
    }
}
