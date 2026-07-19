using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Proves the "at most one enabled document per (OverlayKey, HostKey)" import semantics
/// of <see cref="OmpAdminRepository.SaveImportedConfigOverlayAsync"/>.
/// </summary>
public sealed class OmpAdminRepositoryConfigOverlayImportTests : IClassFixture<ConfigOverlayImportTestFixture>
{
    private const string OverlayKey = "test-overlay";

    // Unique host key per test: the fixture uses a shared database without per-test reset.
    private readonly string _hostKey = "test-host-" + Guid.NewGuid().ToString("N");

    private readonly ConfigOverlayImportTestFixture _fixture;
    private readonly OmpAdminRepository _repository;

    public OmpAdminRepositoryConfigOverlayImportTests(ConfigOverlayImportTestFixture fixture)
    {
        _fixture = fixture;
        _repository = fixture.CreatePortalRepository();
    }

    [Fact]
    public async Task ImportStrictlyNewerVersion_LeavesExactlyOneEnabledDocument()
    {
        var older = CreateOverlay(_hostKey, "2026.07.10", "{ \"a\": 1 }");
        var newer = CreateOverlay(_hostKey, "2026.07.19", "{ \"a\": 2 }");

        var first = await _repository.SaveImportedConfigOverlayAsync(older, replaceExisting: false, CancellationToken.None);
        var second = await _repository.SaveImportedConfigOverlayAsync(newer, replaceExisting: false, CancellationToken.None);

        Assert.True(first.Created);
        Assert.True(second.Created);

        var rows = await _fixture.GetDocumentsAsync(OverlayKey, _hostKey);
        Assert.Equal(2, rows.Count);

        var enabled = rows.Where(r => r.IsEnabled).ToList();
        Assert.Single(enabled);
        Assert.Equal(second.DocumentId, enabled[0].DocumentId);
        Assert.Equal("2026.07.19", enabled[0].OverlayVersion);

        var previous = rows.Single(r => r.DocumentId == first.DocumentId);
        Assert.False(previous.IsEnabled);
    }

    [Fact]
    public async Task ReimportSameVersionIdenticalContent_MakesNoWritesAndTogglesNothing()
    {
        var overlay = CreateOverlay(_hostKey, "2026.07.10", "{ \"a\": 1 }");
        var imported = await _repository.SaveImportedConfigOverlayAsync(overlay, replaceExisting: false, CancellationToken.None);

        var before = (await _fixture.GetDocumentsAsync(OverlayKey, _hostKey)).Single();
        var filesBefore = await _fixture.CountConfigurationFilesAsync(imported.DocumentId);

        // Simulate an operator manually disabling the row; an identical re-import must
        // not re-enable it (even with replaceExisting=true) and must not write anything.
        await _fixture.SetEnabledAsync(imported.DocumentId, false);

        var result = await _repository.SaveImportedConfigOverlayAsync(overlay, replaceExisting: true, CancellationToken.None);

        Assert.True(result.WasIdentical);
        Assert.False(result.Created);
        Assert.False(result.Replaced);

        var after = (await _fixture.GetDocumentsAsync(OverlayKey, _hostKey)).Single();
        Assert.False(after.IsEnabled);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
        Assert.Equal(filesBefore, await _fixture.CountConfigurationFilesAsync(imported.DocumentId));
    }

    [Fact]
    public async Task ImportOlderVersion_CreatesDisabledHistoricalRowAndKeepsNewerActive()
    {
        var newer = CreateOverlay(_hostKey, "2026.07.19", "{ \"a\": 2 }");
        var older = CreateOverlay(_hostKey, "2026.07.10", "{ \"a\": 1 }");

        var active = await _repository.SaveImportedConfigOverlayAsync(newer, replaceExisting: false, CancellationToken.None);
        var historical = await _repository.SaveImportedConfigOverlayAsync(older, replaceExisting: false, CancellationToken.None);

        Assert.True(historical.Created);

        var rows = await _fixture.GetDocumentsAsync(OverlayKey, _hostKey);
        Assert.Equal(2, rows.Count);

        var enabled = rows.Where(r => r.IsEnabled).ToList();
        Assert.Single(enabled);
        Assert.Equal(active.DocumentId, enabled[0].DocumentId);
        Assert.Equal("2026.07.19", enabled[0].OverlayVersion);

        var historicalRow = rows.Single(r => r.DocumentId == historical.DocumentId);
        Assert.False(historicalRow.IsEnabled);
    }

    [Fact]
    public async Task ReplaceSameVersionWithDifferentContent_DisablesOtherEnabledRows()
    {
        var older = CreateOverlay(_hostKey, "2026.07.10", "{ \"a\": 1 }");
        var newer = CreateOverlay(_hostKey, "2026.07.19", "{ \"a\": 2 }");

        var first = await _repository.SaveImportedConfigOverlayAsync(older, replaceExisting: false, CancellationToken.None);
        await _repository.SaveImportedConfigOverlayAsync(newer, replaceExisting: false, CancellationToken.None);

        // Re-import the older version with different content and replaceExisting=true:
        // the updated row becomes enabled and every other enabled row is disabled.
        var replacement = CreateOverlay(_hostKey, "2026.07.10", "{ \"a\": 1, \"b\": 3 }");
        var replaced = await _repository.SaveImportedConfigOverlayAsync(replacement, replaceExisting: true, CancellationToken.None);

        Assert.True(replaced.Replaced);
        Assert.Equal(first.DocumentId, replaced.DocumentId);

        var rows = await _fixture.GetDocumentsAsync(OverlayKey, _hostKey);
        Assert.Equal(2, rows.Count);

        var enabled = rows.Where(r => r.IsEnabled).ToList();
        Assert.Single(enabled);
        Assert.Equal(first.DocumentId, enabled[0].DocumentId);
    }

    private static PortableConfigOverlayDocument CreateOverlay(string hostKey, string version, string json)
        => new(
            OverlayKey: OverlayKey,
            OverlayVersion: version,
            HostKey: hostKey,
            FormatVersion: 1,
            OverlayJson: json,
            OverlaySha256: "sha256:" + json,
            ModuleKey: null,
            ModuleDefinitionVersion: null,
            AppKey: null,
            PackageType: null,
            TargetName: null,
            ArtifactVersion: null,
            SourceName: "config-overlay-import-tests",
            ConfigurationFiles: new[]
            {
                new PortableConfigOverlayConfigurationFile("appsettings.json", json)
            });
}
