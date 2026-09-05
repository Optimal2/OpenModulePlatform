using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Proves that <see cref="OmpHostArtifactRepository.GetArtifactConfigurationFilesAsync"/>
/// resolves competing enabled overlay rows deterministically: the highest OverlayVersion
/// wins even when an older OverlayVersion carries a newer UpdatedUtc.
/// </summary>
public sealed class OmpHostArtifactRepositoryConfigOverlayResolutionTests : IDisposable
{
    private const string OverlayKey = "test-overlay";
    private const string HostKey = "test-host";
    private const int ArtifactId = 42;

    private readonly OmpHostArtifactRepositoryTestDatabase _database;
    private readonly OmpHostArtifactRepository _repository;

    public OmpHostArtifactRepositoryConfigOverlayResolutionTests()
    {
        _database = new OmpHostArtifactRepositoryTestDatabase();
        try
        {
            _database.CreateConfigurationFileResolutionTables();
            _database.InsertArtifactWithApp(ArtifactId, "web-app", "1.0.0", "test-module", "test-app");
            _repository = new OmpHostArtifactRepository(_database.CreateFactory());
        }
        catch
        {
            // A throwing constructor means xUnit never calls Dispose(); dispose the
            // fixture here or its database leaks on every failing run.
            _database.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task TwoEnabledOverlayRows_HighestOverlayVersionWinsRegardlessOfUpdatedUtc()
    {
        var older = await _repository.SaveImportedConfigOverlayAsync(
            CreateOverlay("2026.07.10", "{ \"a\": 1 }"), replaceExisting: false, CancellationToken.None);
        var newer = await _repository.SaveImportedConfigOverlayAsync(
            CreateOverlay("2026.07.19", "{ \"a\": 2 }"), replaceExisting: false, CancellationToken.None);

        // Construct the defense-in-depth scenario: two enabled rows for the same
        // (OverlayKey, HostKey), where the OLDER OverlayVersion has the NEWER UpdatedUtc.
        // The production filtered unique index forbids this state, so drop the mirrored
        // index first: this simulates a pre-upgrade database, where the deterministic
        // resolution below is the only guard against ambiguous overlay selection.
        _database.DropOverlayEnabledUniqueIndex();
        _database.SetOverlayDocumentEnabled(older.DocumentId, true);
        _database.SetOverlayDocumentEnabled(newer.DocumentId, true);
        _database.SetOverlayDocumentUpdatedUtc(newer.DocumentId, new DateTime(2026, 7, 19, 0, 0, 0, DateTimeKind.Utc));
        _database.SetOverlayDocumentUpdatedUtc(older.DocumentId, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));

        var files = await _repository.GetArtifactConfigurationFilesAsync(ArtifactId, HostKey, CancellationToken.None);

        var file = Assert.Single(files);
        Assert.Equal("appsettings.json", file.RelativePath);
        Assert.Equal("{ \"a\": 2 }", file.FileContent);
    }

    [Fact]
    public async Task OverlayPinnedToOlderMinimum_AppliesToNewerArtifactVersion()
    {
        // ADR 0006: artifactVersion is a minimum. An overlay pinned to 0.3.183
        // must still apply when the artifact has moved on to 0.3.229.
        const int newArtifactId = 43;
        _database.InsertArtifactForExistingApp(newArtifactId, "web-app", "0.3.229");
        await _repository.SaveImportedConfigOverlayAsync(
            CreateOverlay("1.0.0", "overlay-wins", artifactVersion: "0.3.183", relativePath: "site.config.js"),
            replaceExisting: false,
            CancellationToken.None);

        var files = await _repository.GetArtifactConfigurationFilesAsync(newArtifactId, HostKey, CancellationToken.None);

        var file = Assert.Single(files);
        Assert.Equal("site.config.js", file.RelativePath);
        Assert.Equal("overlay-wins", file.FileContent);
    }

    [Fact]
    public async Task OverlayPinnedToNewerMinimum_DoesNotApplyToOlderArtifactVersion()
    {
        // ADR 0006: the same pinned overlay must NOT apply to an artifact older
        // than its minimum; the artifact's own configuration file stands.
        const int oldArtifactId = 44;
        _database.InsertArtifactForExistingApp(oldArtifactId, "web-app", "0.3.100");
        _database.InsertArtifactConfigurationFile(oldArtifactId, "site.config.js", "artifact-default", packageFileContent: null);
        await _repository.SaveImportedConfigOverlayAsync(
            CreateOverlay("1.0.0", "overlay-wins", artifactVersion: "0.3.183", relativePath: "site.config.js"),
            replaceExisting: false,
            CancellationToken.None);

        var files = await _repository.GetArtifactConfigurationFilesAsync(oldArtifactId, HostKey, CancellationToken.None);

        var file = Assert.Single(files);
        Assert.Equal("site.config.js", file.RelativePath);
        Assert.Equal("artifact-default", file.FileContent);
    }

    [Fact]
    public async Task OverlayWithoutArtifactVersion_AppliesRegardlessOfArtifactVersion()
    {
        const int newArtifactId = 45;
        _database.InsertArtifactForExistingApp(newArtifactId, "web-app", "9.9.9");
        await _repository.SaveImportedConfigOverlayAsync(
            CreateOverlay("1.0.0", "overlay-wins"),
            replaceExisting: false,
            CancellationToken.None);

        var files = await _repository.GetArtifactConfigurationFilesAsync(newArtifactId, HostKey, CancellationToken.None);

        var file = Assert.Single(files);
        Assert.Equal("overlay-wins", file.FileContent);
    }

    private static PortableConfigOverlayDocument CreateOverlay(
        string version,
        string content,
        string? artifactVersion = null,
        string? mergeMode = null,
        string relativePath = "appsettings.json")
        => new(
            OverlayKey: OverlayKey,
            OverlayVersion: version,
            HostKey: HostKey,
            FormatVersion: 1,
            OverlayJson: content,
            OverlaySha256: "sha256:" + content,
            ModuleKey: null,
            ModuleDefinitionVersion: null,
            AppKey: null,
            PackageType: null,
            TargetName: null,
            ArtifactVersion: artifactVersion,
            SourceName: "config-overlay-resolution-tests",
            ConfigurationFiles: new[]
            {
                new PortableConfigOverlayConfigurationFile(relativePath, content, mergeMode)
            });
}
