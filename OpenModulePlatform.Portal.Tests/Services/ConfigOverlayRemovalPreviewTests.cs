using System.Text.Json;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Pages.Admin;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>Causal fixtures for the read-only removal-impact preview:
/// excluding one saved document must reveal the exact fallback winner.
/// In-memory only; no database, no disk inspection.</summary>
public sealed class ConfigOverlayRemovalPreviewTests
{
    private static OverlayAdminFakeStore StoreWith(params ConfigOverlayDocumentRow[] overlays)
    {
        var store = new OverlayAdminFakeStore { Snapshot = ConfigOverlayAdministrationTests.Snapshot(overlays) };
        foreach (var overlay in overlays)
            store.Details[overlay.ConfigOverlayDocumentId] = new(overlay, "",
                [ConfigOverlayAdministrationTests.File(overlay.ConfigOverlayDocumentId, overlay.ConfigOverlayDocumentId)]);
        return store;
    }

    [Fact]
    public async Task ExactHostRemovalFallsBackToAllHosts()
    {
        var host = ConfigOverlayAdministrationTests.Overlay(3);
        var allHosts = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var store = StoreWith(host, allHosts);
        var preview = await new ConfigOverlayAdministration(store).PreviewRemovalAsync(store.Snapshot, 3, "host-a", 42, default);
        var file = Assert.Single(preview.Files);
        Assert.Equal("FallsBack", file.Outcome);
        Assert.Equal("overlay-3", file.CurrentWinner!.OverlayKey);
        Assert.Equal("overlay-2", file.WinnerAfterRemoval!.OverlayKey);
        Assert.Equal("AllHosts", file.WinnerAfterRemoval.HostScope);
        Assert.False(preview.DetailUnavailable);
    }

    [Fact]
    public async Task RemovalWithNoFallbackLeavesNoEffectiveFile()
    {
        var store = StoreWith(ConfigOverlayAdministrationTests.Overlay(3));
        var preview = await new ConfigOverlayAdministration(store).PreviewRemovalAsync(store.Snapshot, 3, "host-a", 42, default);
        var file = Assert.Single(preview.Files);
        Assert.Equal("NoEffectiveFile", file.Outcome);
        Assert.NotNull(file.CurrentWinner);
        Assert.Null(file.WinnerAfterRemoval);
    }

    [Fact]
    public async Task UnrelatedApplicationSelectorsAreExcluded()
    {
        var foreign = ConfigOverlayAdministrationTests.Overlay(9, module: "other-module");
        var store = StoreWith(foreign);
        var preview = await new ConfigOverlayAdministration(store).PreviewRemovalAsync(store.Snapshot, 9, "host-a", 42, default);
        var file = Assert.Single(preview.Files);
        Assert.Equal("Unchanged", file.Outcome);
        Assert.Contains(file.Notes, n => n.Contains("another application", StringComparison.Ordinal));
        Assert.DoesNotContain(JsonSerializer.Serialize(preview), "private-secret", StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledDocumentRemovalChangesNothing()
    {
        var store = StoreWith(ConfigOverlayAdministrationTests.Overlay(3, enabled: false));
        var preview = await new ConfigOverlayAdministration(store).PreviewRemovalAsync(store.Snapshot, 3, "host-a", 42, default);
        Assert.True(preview.DocumentWasEnabled == false);
        var file = Assert.Single(preview.Files);
        Assert.Equal("Unchanged", file.Outcome);
        Assert.Contains(file.Notes, n => n.Contains("already disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewBindsExactOptimisticTokenAndStaleSnapshotDiffers()
    {
        var row = ConfigOverlayAdministrationTests.Overlay(3);
        var store = StoreWith(row);
        var preview = await new ConfigOverlayAdministration(store).PreviewRemovalAsync(store.Snapshot, 3, "host-a", 42, default);
        var tokenAtPreview = OmpAdminRepository.OverlayEditToken(preview.OverlaySha256, preview.UpdatedUtc);
        Assert.Equal(ConfigOverlaysModel_Token(row), tokenAtPreview);
        var changed = new ConfigOverlayDocumentRow
        {
            ConfigOverlayDocumentId = row.ConfigOverlayDocumentId,
            OverlayKey = row.OverlayKey,
            OverlayVersion = row.OverlayVersion,
            HostKey = row.HostKey,
            HostScope = row.HostScope,
            OverlaySha256 = row.OverlaySha256,
            UpdatedUtc = row.UpdatedUtc.AddMinutes(1),
        };
        Assert.NotEqual(tokenAtPreview, ConfigOverlaysModel_Token(changed));
    }

    private static string ConfigOverlaysModel_Token(ConfigOverlayDocumentRow row)
        => ConfigOverlaysModel.Token(row);

    [Fact]
    public async Task AllHostsMergeContributionBelowExactHostWinnerIsNotUnchanged()
    {
        // Selected AllHosts layer contributes a distinct key through merge;
        // the exact-host winner stays the winner after removal, but the
        // effective content loses that key, so this is not "Unchanged".
        var allHosts = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var host = ConfigOverlayAdministrationTests.Overlay(3);
        var store = StoreWith(allHosts, host);
        store.Details[2] = new(allHosts, "", [ConfigOverlayAdministrationTests.File(2, 2, """{"shared":"from-all-hosts"}""")]);
        store.Details[3] = new(host, "", [ConfigOverlayAdministrationTests.File(3, 3, """{"host":true}""")]);
        var preview = await new ConfigOverlayAdministration(store).PreviewRemovalAsync(store.Snapshot, 2, "host-a", 42, default);
        var file = Assert.Single(preview.Files);
        Assert.Equal("overlay-3", file.CurrentWinner!.OverlayKey);
        Assert.Equal("overlay-3", file.WinnerAfterRemoval!.OverlayKey);
        Assert.Equal("ChangesEffectiveContent", file.Outcome);
        Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(preview), StringComparison.Ordinal);
        Assert.DoesNotContain("from-all-hosts", JsonSerializer.Serialize(preview), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenuinelyShadowedMergeContributionStaysUnchanged()
    {
        // Exact-host replace discards the lower AllHosts layer entirely, so
        // removing the shadowed AllHosts document changes nothing.
        var allHosts = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var host = ConfigOverlayAdministrationTests.Overlay(3);
        var store = StoreWith(allHosts, host);
        store.Details[2] = new(allHosts, "", [ConfigOverlayAdministrationTests.File(2, 2, """{"shared":"from-all-hosts"}""")]);
        store.Details[3] = new(host, "", [ConfigOverlayAdministrationTests.File(3, 3, """{"host":true}""", "replace")]);
        var preview = await new ConfigOverlayAdministration(store).PreviewRemovalAsync(store.Snapshot, 2, "host-a", 42, default);
        var file = Assert.Single(preview.Files);
        Assert.Equal("Unchanged", file.Outcome);
        Assert.Equal("overlay-3", file.WinnerAfterRemoval!.OverlayKey);
    }

    [Fact]
    public async Task MergeContributionWithIdenticalEffectiveContentStaysUnchanged()
    {
        var allHosts = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var host = ConfigOverlayAdministrationTests.Overlay(3);
        var store = StoreWith(allHosts, host);
        store.Details[2] = new(allHosts, "", [ConfigOverlayAdministrationTests.File(2, 2, """{"shared":true}""")]);
        store.Details[3] = new(host, "", [ConfigOverlayAdministrationTests.File(3, 3, """{"shared":true}""")]);
        var preview = await new ConfigOverlayAdministration(store).PreviewRemovalAsync(store.Snapshot, 2, "host-a", 42, default);
        Assert.Equal("Unchanged", Assert.Single(preview.Files).Outcome);
    }

    [Fact]
    public async Task ReplaceWinnerRemovalFallsBack()
    {
        var allHosts = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var host = ConfigOverlayAdministrationTests.Overlay(3);
        var store = StoreWith(host, allHosts);
        store.Details[3] = new(host, "", [ConfigOverlayAdministrationTests.File(3, 3, """{"host":true}""", "replace")]);
        store.Details[2] = new(allHosts, "", [ConfigOverlayAdministrationTests.File(2, 2, """{"shared":true}""")]);
        var preview = await new ConfigOverlayAdministration(store).PreviewRemovalAsync(store.Snapshot, 3, "host-a", 42, default);
        Assert.Equal("FallsBack", Assert.Single(preview.Files).Outcome);
    }

    [Fact]
    public async Task MissingDetailIsPartialUnknownWithoutContent()
    {
        var row = ConfigOverlayAdministrationTests.Overlay(3);
        var store = new OverlayAdminFakeStore { Snapshot = ConfigOverlayAdministrationTests.Snapshot(row) };
        var preview = await new ConfigOverlayAdministration(store).PreviewRemovalAsync(store.Snapshot, 3, "host-a", 42, default);
        Assert.True(preview.DetailUnavailable);
        Assert.Empty(preview.Files);
        Assert.Contains(preview.Diagnostics, d => d.Contains("partial", StringComparison.Ordinal));
        Assert.DoesNotContain("Content", JsonSerializer.Serialize(preview));
    }

    [Fact]
    public async Task PreviewRejectsUnknownDocumentForeignHostAndArtifactBeforeReads()
    {
        var store = new OverlayAdminFakeStore { Snapshot = ConfigOverlayAdministrationTests.Snapshot(ConfigOverlayAdministrationTests.Overlay(3)) };
        var service = new ConfigOverlayAdministration(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewRemovalAsync(store.Snapshot, 999, "host-a", 42, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewRemovalAsync(store.Snapshot, 3, "foreign-host", 42, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewRemovalAsync(store.Snapshot, 3, "host-a", 999, default));
        Assert.Equal(0, store.SourceReads);
    }
}
