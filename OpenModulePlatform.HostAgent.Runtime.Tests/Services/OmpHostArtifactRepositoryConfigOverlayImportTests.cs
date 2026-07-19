using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Proves the "at most one enabled document per (OverlayKey, HostKey)" import semantics
/// of <see cref="OmpHostArtifactRepository.SaveImportedConfigOverlayAsync"/>, the
/// HostAgent counterpart of the portal save path.
/// </summary>
public sealed class OmpHostArtifactRepositoryConfigOverlayImportTests : IDisposable
{
    private const string OverlayKey = "test-overlay";
    private const string HostKey = "test-host";

    private readonly OmpHostArtifactRepositoryTestDatabase _database;
    private readonly OmpHostArtifactRepository _repository;

    public OmpHostArtifactRepositoryConfigOverlayImportTests()
    {
        _database = new OmpHostArtifactRepositoryTestDatabase();
        _database.CreateConfigOverlayTables();
        _repository = new OmpHostArtifactRepository(_database.CreateFactory());
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task ImportStrictlyNewerVersion_LeavesExactlyOneEnabledDocument()
    {
        var older = CreateOverlay("2026.07.10", "{ \"a\": 1 }");
        var newer = CreateOverlay("2026.07.19", "{ \"a\": 2 }");

        var first = await _repository.SaveImportedConfigOverlayAsync(older, replaceExisting: false, CancellationToken.None);
        var second = await _repository.SaveImportedConfigOverlayAsync(newer, replaceExisting: false, CancellationToken.None);

        Assert.True(first.Created);
        Assert.True(second.Created);

        var rows = _database.GetOverlayDocuments(OverlayKey, HostKey);
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
        var overlay = CreateOverlay("2026.07.10", "{ \"a\": 1 }");
        var imported = await _repository.SaveImportedConfigOverlayAsync(overlay, replaceExisting: false, CancellationToken.None);

        var before = _database.GetOverlayDocuments(OverlayKey, HostKey).Single();
        var filesBefore = _database.CountOverlayConfigurationFiles(imported.DocumentId);

        // A manually disabled row must stay disabled after an identical re-import.
        _database.SetOverlayDocumentEnabled(imported.DocumentId, false);

        var result = await _repository.SaveImportedConfigOverlayAsync(overlay, replaceExisting: true, CancellationToken.None);

        Assert.True(result.WasIdentical);
        Assert.False(result.Created);
        Assert.False(result.Replaced);

        var after = _database.GetOverlayDocuments(OverlayKey, HostKey).Single();
        Assert.False(after.IsEnabled);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
        Assert.Equal(filesBefore, _database.CountOverlayConfigurationFiles(imported.DocumentId));
    }

    [Fact]
    public async Task ImportOlderVersion_CreatesDisabledHistoricalRowAndKeepsNewerActive()
    {
        var newer = CreateOverlay("2026.07.19", "{ \"a\": 2 }");
        var older = CreateOverlay("2026.07.10", "{ \"a\": 1 }");

        var active = await _repository.SaveImportedConfigOverlayAsync(newer, replaceExisting: false, CancellationToken.None);
        var historical = await _repository.SaveImportedConfigOverlayAsync(older, replaceExisting: false, CancellationToken.None);

        Assert.True(historical.Created);

        var rows = _database.GetOverlayDocuments(OverlayKey, HostKey);
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
        var older = CreateOverlay("2026.07.10", "{ \"a\": 1 }");
        var newer = CreateOverlay("2026.07.19", "{ \"a\": 2 }");

        var first = await _repository.SaveImportedConfigOverlayAsync(older, replaceExisting: false, CancellationToken.None);
        await _repository.SaveImportedConfigOverlayAsync(newer, replaceExisting: false, CancellationToken.None);

        var replacement = CreateOverlay("2026.07.10", "{ \"a\": 1, \"b\": 3 }");
        var replaced = await _repository.SaveImportedConfigOverlayAsync(replacement, replaceExisting: true, CancellationToken.None);

        Assert.True(replaced.Replaced);
        Assert.Equal(first.DocumentId, replaced.DocumentId);

        var rows = _database.GetOverlayDocuments(OverlayKey, HostKey);
        Assert.Equal(2, rows.Count);

        var enabled = rows.Where(r => r.IsEnabled).ToList();
        Assert.Single(enabled);
        Assert.Equal(first.DocumentId, enabled[0].DocumentId);
    }

    private static PortableConfigOverlayDocument CreateOverlay(string version, string json)
        => new(
            OverlayKey: OverlayKey,
            OverlayVersion: version,
            HostKey: HostKey,
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
