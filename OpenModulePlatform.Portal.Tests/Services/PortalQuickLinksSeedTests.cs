using System.Text.RegularExpressions;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Pins the seeded admin quick-links box (omp.link_boxes + omp.link_box_items)
/// to the shipped initialize SQL. The Configuration overlays page
/// (/admin/configoverlays) must appear after a fresh seed, and the upgrade step
/// must add it to installations that already have the box without duplicating
/// the row on a second run.
/// </summary>
public sealed class PortalQuickLinksSeedTests : IClassFixture<PortalQuickLinksSeedTestFixture>
{
    private const string BoxKey = "portal.admin-quicklinks";

    private readonly PortalQuickLinksSeedTestFixture _fixture;

    public PortalQuickLinksSeedTests(PortalQuickLinksSeedTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FreshSeed_InsertsConfigurationOverlaysNextToHostDeployments()
    {
        var (seed, _) = ReadQuickLinksBatches();
        await _fixture.ResetQuickLinksBoxAsync(BoxKey);
        await _fixture.ExecuteBatchAsync(seed);

        var all = await _fixture.ReadQuickLinksAsync(BoxKey);
        Assert.Equal(20, all.Count);

        var overlays = all.Where(static row => row.Url == "/admin/configoverlays").ToArray();
        var row = Assert.Single(overlays);
        Assert.Equal("Configuration overlays", row.Label);
        Assert.Equal("topology", row.GroupKey);
        Assert.Equal(9, row.SortOrder);

        // Still directly after Host deployments in the seeded ordering.
        Assert.Equal("/admin/hostdeployments", all.Single(static r => r.SortOrder == 8).Url);
    }

    [Fact]
    public async Task UpgradeStep_AddsItemToExistingBoxAndIsIdempotent()
    {
        var (seed, upgrade) = ReadQuickLinksBatches();
        await _fixture.ResetQuickLinksBoxAsync(BoxKey);
        await _fixture.ExecuteBatchAsync(seed);

        // Simulate an installation whose box was seeded before the
        // Configuration overlays item existed.
        await _fixture.ExecuteBatchAsync(
            "DELETE FROM omp.link_box_items WHERE box_key = N'portal.admin-quicklinks' AND url = N'/admin/configoverlays';");
        Assert.DoesNotContain(await _fixture.ReadQuickLinksAsync(BoxKey), static row => row.Url == "/admin/configoverlays");

        await _fixture.ExecuteBatchAsync(upgrade);
        var overlays = (await _fixture.ReadQuickLinksAsync(BoxKey))
            .Where(static row => row.Url == "/admin/configoverlays")
            .ToArray();
        var row = Assert.Single(overlays);
        Assert.Equal("Configuration overlays", row.Label);
        Assert.Equal("topology", row.GroupKey);

        // Re-running the upgrade step must not duplicate the row.
        await _fixture.ExecuteBatchAsync(upgrade);
        Assert.Single(
            await _fixture.ReadQuickLinksAsync(BoxKey),
            static row => row.Url == "/admin/configoverlays");
    }

    private static (string Seed, string Upgrade) ReadQuickLinksBatches()
    {
        var sql = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.Portal", "sql", "2-initialize-omp-portal.sql");
        var batches = Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline)
            .Where(static batch => !string.IsNullOrWhiteSpace(batch))
            .ToArray();

        var seed = batches.Single(static batch => batch.Contains("INSERT INTO omp.link_boxes", StringComparison.Ordinal));
        var upgrade = batches.Single(static batch =>
            batch.Contains("INSERT INTO omp.link_box_items", StringComparison.Ordinal)
            && !batch.Contains("INSERT INTO omp.link_boxes", StringComparison.Ordinal));
        return (seed, upgrade);
    }
}
