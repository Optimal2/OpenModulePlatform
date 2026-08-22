using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Runs the portal save path (<see cref="OmpAdminRepository.SaveImportedConfigOverlayAsync"/>)
/// against the real core schema, which includes the filtered unique index
/// UX_omp_ConfigOverlayDocuments_Enabled_Key_Host. SQL Server checks unique indexes
/// per statement, so a save path that inserts or re-enables a row BEFORE disabling
/// the currently enabled sibling violates the index mid-transaction and fails with
/// error 2601 even though the final state would have been valid. These tests exist
/// because the raw-SQL uniqueness tests alone cannot see the application's write
/// order; a green run here is the proof that the normal upgrade flow survives the
/// index at every intermediate statement.
/// </summary>
[Collection(ConfigOverlayActiveUniquenessCollection.CollectionName)]
public sealed class OmpConfigOverlaySavePathUniquenessTests
{
    // Unique overlay key per test: the fixture uses a shared database without per-test reset.
    private readonly string _overlayKey = "save-path-uniqueness-" + Guid.NewGuid().ToString("N");
    private const string HostKey = "save-path-uniqueness-host";

    private readonly ConfigOverlayActiveUniquenessTestFixture _fixture;
    private readonly OmpAdminRepository _repository;

    public OmpConfigOverlaySavePathUniquenessTests(ConfigOverlayActiveUniquenessTestFixture fixture)
    {
        _fixture = fixture;
        _repository = fixture.CreatePortalRepository();
    }

    [Fact]
    public async Task ImportNewerVersionThroughSavePath_LeavesExactlyOneEnabledDocument()
    {
        var older = CreateOverlay("2026.07.10", "{ \"a\": 1 }");
        var newer = CreateOverlay("2026.07.19", "{ \"a\": 2 }");

        var first = await _repository.SaveImportedConfigOverlayAsync(older, replaceExisting: false, CancellationToken.None);

        // The normal upgrade flow: a strictly newer version is imported while the
        // previous version is still enabled. This must not hit the filtered unique
        // index at any intermediate statement.
        var second = await _repository.SaveImportedConfigOverlayAsync(newer, replaceExisting: false, CancellationToken.None);

        Assert.True(first.Created);
        Assert.True(second.Created);

        var rows = await _fixture.GetDocumentsAsync(_overlayKey, HostKey);
        Assert.Equal(2, rows.Count);

        var enabled = rows.Where(r => r.IsEnabled).ToList();
        Assert.Single(enabled);
        Assert.Equal(second.DocumentId, enabled[0].DocumentId);
        Assert.Equal("2026.07.19", enabled[0].OverlayVersion);
    }

    [Fact]
    public async Task ReplaceSameVersionThroughSavePath_LeavesExactlyOneEnabledDocument()
    {
        var older = CreateOverlay("2026.07.10", "{ \"a\": 1 }");
        var newer = CreateOverlay("2026.07.19", "{ \"a\": 2 }");

        var first = await _repository.SaveImportedConfigOverlayAsync(older, replaceExisting: false, CancellationToken.None);
        await _repository.SaveImportedConfigOverlayAsync(newer, replaceExisting: false, CancellationToken.None);

        // The update path: re-import the older version with different content while
        // the newer version is enabled. The update re-enables the older row and must
        // disable the newer sibling without violating the index mid-transaction.
        var replacement = CreateOverlay("2026.07.10", "{ \"a\": 1, \"b\": 3 }");
        var replaced = await _repository.SaveImportedConfigOverlayAsync(replacement, replaceExisting: true, CancellationToken.None);

        Assert.True(replaced.Replaced);
        Assert.Equal(first.DocumentId, replaced.DocumentId);

        var rows = await _fixture.GetDocumentsAsync(_overlayKey, HostKey);
        Assert.Equal(2, rows.Count);

        var enabled = rows.Where(r => r.IsEnabled).ToList();
        Assert.Single(enabled);
        Assert.Equal(first.DocumentId, enabled[0].DocumentId);
    }

    private PortableConfigOverlayDocument CreateOverlay(string version, string json)
        => new(
            OverlayKey: _overlayKey,
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
            SourceName: "config-overlay-save-path-uniqueness-tests",
            ConfigurationFiles: new[]
            {
                new PortableConfigOverlayConfigurationFile("appsettings.json", json)
            });
}
