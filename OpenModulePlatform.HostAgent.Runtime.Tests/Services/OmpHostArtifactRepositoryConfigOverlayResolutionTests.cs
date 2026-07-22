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
        _database.CreateConfigurationFileResolutionTables();
        _database.InsertArtifactWithApp(ArtifactId, "web-app", "1.0.0", "test-module", "test-app");
        _repository = new OmpHostArtifactRepository(_database.CreateFactory());
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
        _database.SetOverlayDocumentEnabled(older.DocumentId, true);
        _database.SetOverlayDocumentEnabled(newer.DocumentId, true);
        _database.SetOverlayDocumentUpdatedUtc(newer.DocumentId, new DateTime(2026, 7, 19, 0, 0, 0, DateTimeKind.Utc));
        _database.SetOverlayDocumentUpdatedUtc(older.DocumentId, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));

        var files = await _repository.GetArtifactConfigurationFilesAsync(ArtifactId, HostKey, CancellationToken.None);

        var file = Assert.Single(files);
        Assert.Equal("appsettings.json", file.RelativePath);
        Assert.Equal("{ \"a\": 2 }", file.FileContent);
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
            SourceName: "config-overlay-resolution-tests",
            ConfigurationFiles: new[]
            {
                new PortableConfigOverlayConfigurationFile("appsettings.json", json)
            });
}
