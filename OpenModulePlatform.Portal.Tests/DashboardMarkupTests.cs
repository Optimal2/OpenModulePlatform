using System.Text.RegularExpressions;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests;

public sealed class DashboardMarkupTests
{
    private static string Page => OmpRepositoryFiles.ReadRepositoryTextFile(
        "OpenModulePlatform.Portal", "Pages", "Index.cshtml");

    [Fact]
    public void Canvas_ExposesNamedRegion()
    {
        var canvas = Assert.Single(Page.Split('\n'), line => line.Contains("data-dashboard-canvas"));
        Assert.Contains("role=\"region\"", canvas);
        Assert.Contains("aria-label=", canvas);
    }

    [Fact]
    public void ImageLibraryTabs_LinkToLabelledPanelsWithWidgetScopedIds()
    {
        var ids = new HashSet<string>();
        foreach (var mode in new[] { "image", "zip" })
        {
            var tab = Assert.Single(Page.Split('\n'), line => line.Contains($"data-blank-widget-admin-tab=\"{mode}\""));
            var pane = Assert.Single(Page.Split('\n'), line => line.Contains($"data-blank-widget-admin-pane=\"{mode}\""));
            var tabId = Attribute(tab, "id");
            var paneId = Attribute(pane, "id");
            Assert.Contains("@widget.UserActiveWidgetId", tabId);
            Assert.Contains("@widget.UserActiveWidgetId", paneId);
            Assert.True(ids.Add(tabId));
            Assert.True(ids.Add(paneId));
            Assert.Equal(paneId, Attribute(tab, "aria-controls"));
            Assert.Equal(tabId, Attribute(pane, "aria-labelledby"));
            Assert.Equal("tabpanel", Attribute(pane, "role"));
        }
    }

    [Fact]
    public void UnreleasedChangelog_HasOneFixedSection()
    {
        var changelog = OmpRepositoryFiles.ReadRepositoryTextFile("CHANGELOG.md");
        var unreleased = changelog.Split("## [Unreleased]", StringSplitOptions.None)[1]
            .Split("\n## [", StringSplitOptions.None)[0];
        Assert.Single(unreleased.Split('\n'), line => line.Trim() == "### Fixed");
    }

    private static string Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"(?:^|\\s){name}=\"([^\"]+)\"");
        Assert.True(match.Success, $"Missing {name}: {tag.Trim()}");
        return match.Groups[1].Value;
    }
}
