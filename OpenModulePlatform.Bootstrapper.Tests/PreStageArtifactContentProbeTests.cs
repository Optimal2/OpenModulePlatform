using System.IO.Compression;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// The measurement that feeds the pre-stage gate: every artifact package inside a
/// universal package is hashed the way the host agent import hashes it, and set
/// against the SHA-256 registered for the same identity and version.
/// </summary>
public sealed class PreStageArtifactContentProbeTests : IDisposable
{
    private const string PackageName = "content_webapp__content_webapp_webapp__web-app__content-webapp__0.3.316.zip";

    private readonly string _root = Path.Join(Path.GetTempPath(), "omp-prestage-probe-tests-" + Guid.NewGuid().ToString("N"));

    public PreStageArtifactContentProbeTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheArtifactPackageFileNameYieldsTheImportIdentity()
    {
        var identity = PreStageArtifactContentProbe.TryParseArtifactPackageName(PackageName);

        Assert.NotNull(identity);
        Assert.Equal("content_webapp", identity.ModuleKey);
        Assert.Equal("content_webapp_webapp", identity.AppKey);
        Assert.Equal("web-app", identity.PackageType);
        Assert.Equal("content-webapp", identity.TargetName);
        Assert.Equal("0.3.316", identity.Version);
        Assert.Null(PreStageArtifactContentProbe.TryParseArtifactPackageName("content_webapp.module-definition.json"));
    }

    [Theory]
    [InlineData("content_webapp__content_webapp_webapp__web-app__content-webapp__0.3.316+abc123.zip", true)]
    [InlineData("content_webapp__content_webapp_webapp__web-app__content-webapp__.0.3.316.zip", false)]
    [InlineData("content_webapp__content_webapp_webapp__web-app__-content-webapp__0.3.316.zip", false)]
    [InlineData("content_webapp__content_webapp_webapp__web-app__content webapp__0.3.316.zip", false)]
    public void TheFileNameTokenRuleIsTheImports(string fileName, bool accepted)
    {
        // ArtifactZipImportService.MetadataTokenPattern: a leading letter or digit,
        // then letters, digits, '.', '_', '+' and '-'.
        Assert.Equal(accepted, PreStageArtifactContentProbe.TryParseArtifactPackageName(fileName) is not null);
    }

    [Fact]
    public async Task APackageInASubfolderOfArtifactsIsMeasuredLikeTheImportFindsIt()
    {
        var universal = BuildUniversalPackage("nested", "artifacts/web/");
        var expectedHash = await HashOfContentAsync("nested");

        var components = await PreStageArtifactContentProbe.MeasureAsync(
            universal,
            (_, _) => Task.FromResult<PreStageRegisteredArtifact?>(new("0.3.316", expectedHash)),
            Path.Join(_root, "work"),
            CancellationToken.None);

        var component = Assert.Single(components);
        Assert.Equal("content-webapp", component.ComponentKey);
        Assert.Equal(expectedHash, component.PackageSha256);
    }

    [Fact]
    public async Task ContentThatDiffersFromTheRegisteredHashIsRefusedAndIdenticalContentPasses()
    {
        var universal = BuildUniversalPackage("staticwebassets endpoints, Last-Modified: Mon, 28 Sep 2026");
        var expectedHash = await HashOfContentAsync("staticwebassets endpoints, Last-Modified: Mon, 28 Sep 2026");
        var otherHash = await HashOfContentAsync("staticwebassets endpoints, Last-Modified: Mon, 21 Sep 2026");

        var changed = await PreStageArtifactContentProbe.MeasureAsync(
            universal,
            (_, _) => Task.FromResult<PreStageRegisteredArtifact?>(new("0.3.316", otherHash)),
            Path.Join(_root, "work"),
            CancellationToken.None);
        var changedVerdict = PreStageVersionGate.Evaluate(changed, databaseChecked: true, databaseFailure: null);

        var component = Assert.Single(changed);
        Assert.Equal(expectedHash, component.PackageSha256);
        Assert.False(changedVerdict.MayProceed);
        Assert.Contains("content-webapp", changedVerdict.Message, StringComparison.Ordinal);

        var identical = await PreStageArtifactContentProbe.MeasureAsync(
            universal,
            (_, _) => Task.FromResult<PreStageRegisteredArtifact?>(new("0.3.316", expectedHash)),
            Path.Join(_root, "work"),
            CancellationToken.None);

        Assert.True(PreStageVersionGate.Evaluate(identical, databaseChecked: true, databaseFailure: null).MayProceed);
    }

    [Fact]
    public async Task AnArtifactNotRegisteredAtThisVersionIsNotExtracted()
    {
        var universal = BuildUniversalPackage("anything");

        var components = await PreStageArtifactContentProbe.MeasureAsync(
            universal,
            (_, _) => Task.FromResult<PreStageRegisteredArtifact?>(null),
            Path.Join(_root, "work"),
            CancellationToken.None);

        var component = Assert.Single(components);
        Assert.Null(component.PackageSha256);
        Assert.True(PreStageVersionGate.Evaluate(components, databaseChecked: true, databaseFailure: null).MayProceed);
    }

    private string BuildUniversalPackage(string endpointsText, string artifactFolder = "artifacts/")
    {
        // A legacy (manifest-less) artifact package: the whole zip is the content.
        var artifactPath = Path.Join(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var artifact = ZipFile.Open(artifactPath, ZipArchiveMode.Create))
        {
            WriteEntry(artifact, "app.staticwebassets.endpoints.json", endpointsText);
            WriteEntry(artifact, "wwwroot/site.css", "body{}");
        }

        var universalPath = Path.Join(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var universal = ZipFile.Open(universalPath, ZipArchiveMode.Create))
        {
            universal.CreateEntryFromFile(artifactPath, artifactFolder + PackageName);
            WriteEntry(universal, "module-definitions/content_webapp.module-definition.json", "{}");
        }

        return universalPath;
    }

    private async Task<string> HashOfContentAsync(string endpointsText)
    {
        var directory = Path.Join(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(directory, "wwwroot"));
        await File.WriteAllTextAsync(Path.Join(directory, "app.staticwebassets.endpoints.json"), endpointsText);
        await File.WriteAllTextAsync(Path.Join(directory, "wwwroot", "site.css"), "body{}");
        return await ArtifactHash.ComputeSha256Async(directory, CancellationToken.None);
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }
}
