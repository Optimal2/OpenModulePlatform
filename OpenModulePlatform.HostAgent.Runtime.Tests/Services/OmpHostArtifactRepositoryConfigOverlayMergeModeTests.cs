using System.Text.Json.Nodes;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Proves the per-file <c>mergeMode</c> semantics of
/// <see cref="OmpHostArtifactRepository.GetArtifactConfigurationFilesAsync"/>:
/// a winning overlay file deep-merges onto the artifact-owned base row for
/// <c>.json</c> paths by default, replaces it otherwise, and an explicit mode
/// overrides the default in both directions.
/// </summary>
public sealed class OmpHostArtifactRepositoryConfigOverlayMergeModeTests : IDisposable
{
    private const string OverlayKey = "test-overlay";
    private const string HostKey = "test-host";
    private const int ArtifactId = 42;

    private readonly OmpHostArtifactRepositoryTestDatabase _database;
    private readonly OmpHostArtifactRepository _repository;

    public OmpHostArtifactRepositoryConfigOverlayMergeModeTests()
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
    public async Task JsonOverlayWithoutMergeMode_MergesOntoArtifactBase()
    {
        _database.InsertArtifactConfigurationFile(
            ArtifactId,
            "appsettings.json",
            """{ "a": 1, "b": { "x": 1, "y": 2 } }""",
            packageFileContent: null);
        await SaveOverlayAsync(new PortableConfigOverlayConfigurationFile(
            "appsettings.json",
            """{ "b": { "x": 9 }, "c": 3 }"""));

        var file = await ResolveSingleFileAsync();

        var merged = JsonNode.Parse(file.FileContent)!.AsObject();
        Assert.Equal(1, merged["a"]!.GetValue<int>());
        Assert.Equal(9, merged["b"]!["x"]!.GetValue<int>());
        Assert.Equal(2, merged["b"]!["y"]!.GetValue<int>());
        Assert.Equal(3, merged["c"]!.GetValue<int>());
    }

    [Fact]
    public async Task JsonOverlayWithReplaceMode_ReplacesArtifactBase()
    {
        _database.InsertArtifactConfigurationFile(
            ArtifactId,
            "appsettings.json",
            """{ "a": 1 }""",
            packageFileContent: null);
        await SaveOverlayAsync(new PortableConfigOverlayConfigurationFile(
            "appsettings.json",
            """{ "b": 2 }""",
            "replace"));

        var file = await ResolveSingleFileAsync();

        Assert.Equal("""{ "b": 2 }""", file.FileContent);
    }

    [Fact]
    public async Task NonJsonOverlayWithoutMergeMode_ReplacesArtifactBase()
    {
        _database.InsertArtifactConfigurationFile(
            ArtifactId,
            "site.config.js",
            """{ "a": 1 }""",
            packageFileContent: null);
        await SaveOverlayAsync(new PortableConfigOverlayConfigurationFile(
            "site.config.js",
            """{ "b": 2 }"""));

        var file = await ResolveSingleFileAsync();

        Assert.Equal("""{ "b": 2 }""", file.FileContent);
    }

    [Fact]
    public async Task NonJsonOverlayWithExplicitMergeMode_MergesOntoArtifactBase()
    {
        _database.InsertArtifactConfigurationFile(
            ArtifactId,
            "site.config",
            """{ "a": 1 }""",
            packageFileContent: null);
        await SaveOverlayAsync(new PortableConfigOverlayConfigurationFile(
            "site.config",
            """{ "b": 2 }""",
            "merge"));

        var file = await ResolveSingleFileAsync();

        var merged = JsonNode.Parse(file.FileContent)!.AsObject();
        Assert.Equal(1, merged["a"]!.GetValue<int>());
        Assert.Equal(2, merged["b"]!.GetValue<int>());
    }

    [Fact]
    public async Task JsonOverlayWithoutArtifactBase_StandsAlone()
    {
        await SaveOverlayAsync(new PortableConfigOverlayConfigurationFile(
            "extra.json",
            """{ "only": "overlay" }"""));

        var file = await ResolveSingleFileAsync();

        Assert.Equal("""{ "only": "overlay" }""", file.FileContent);
    }

    [Fact]
    public async Task MergeMode_IsPersistedPerConfigurationFile()
    {
        await SaveOverlayAsync(
            new PortableConfigOverlayConfigurationFile("appsettings.json", """{ "b": 2 }""", "replace"),
            new PortableConfigOverlayConfigurationFile("extra.json", """{ "c": 3 }"""));

        var documentId = _database.GetOverlayDocuments(OverlayKey, HostKey).Single().DocumentId;
        var files = _database.GetOverlayConfigurationFiles(documentId);

        Assert.Equal(2, files.Count);
        Assert.Equal("replace", files.Single(file => file.RelativePath == "appsettings.json").MergeMode);
        Assert.Null(files.Single(file => file.RelativePath == "extra.json").MergeMode);
    }

    private async Task SaveOverlayAsync(params PortableConfigOverlayConfigurationFile[] files)
    {
        var json = files[0].FileContent;
        await _repository.SaveImportedConfigOverlayAsync(
            new PortableConfigOverlayDocument(
                OverlayKey: OverlayKey,
                OverlayVersion: "1.0.0",
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
                SourceName: "config-overlay-merge-mode-tests",
                ConfigurationFiles: files),
            replaceExisting: false,
            CancellationToken.None);
    }

    private async Task<ArtifactConfigurationFileDescriptor> ResolveSingleFileAsync()
    {
        var files = await _repository.GetArtifactConfigurationFilesAsync(ArtifactId, HostKey, CancellationToken.None);
        return Assert.Single(files);
    }
}
