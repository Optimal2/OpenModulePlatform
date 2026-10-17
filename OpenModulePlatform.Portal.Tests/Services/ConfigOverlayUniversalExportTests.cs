using System.IO.Compression;
using System.Text.Json.Nodes;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ConfigOverlayUniversalExportTests : IDisposable
{
    private readonly string _root = Path.Join(
        Environment.GetEnvironmentVariable("AO_JOB_SCRATCH") ?? Path.GetTempPath(),
        $"overlay-export-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("AllHosts", "*")]
    [InlineData("Host", "web-01")]
    [InlineData("Host", "all-hosts")]
    [InlineData("Host", "web:01")]
    public async Task ProductionZipWriter_RoundTripsThroughUniversalAndOverlayReaders(string scope, string hostKey)
    {
        Directory.CreateDirectory(_root);
        var json = ConfigOverlayExportJson.Build(
            """{"overlayKey":"site-settings","overlayVersion":"1.2.3","moduleKey":"sample","artifactVersion":"1.0.0"}""",
            scope, hostKey,
            [new("appsettings.json", "{\"setting\":true}", "replace")]);
        var path = Path.Join(_root, "package.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var item = await PortableModulePackageService.AddConfigOverlayEntryAsync(
                archive, new(StringComparer.OrdinalIgnoreCase),
                ("site-settings", hostKey, scope, "1.2.3", json), CancellationToken.None);
            await PortableModulePackageService.WriteUniversalPackageManifestAsync(
                archive, "sample", "1.2.3", "Sample", new JsonArray(item), CancellationToken.None);
        }

        var package = new UniversalModulePackageReader().ExtractToDirectory(path, Path.Join(_root, "extracted"));
        var entry = Assert.Single(package.Items);
        Assert.Equal(UniversalModulePackageItemKind.ConfigOverlay, entry.Kind);
        Assert.DoesNotContain("*", entry.Path);
        var overlay = await new ConfigOverlayPackageReader().ReadConfigOverlayAsync(
            entry.ExtractedPath, entry.SourceName, CancellationToken.None);
        Assert.Equal(ConfigOverlayHostScopeNames.Parse(scope), overlay.HostScope);
        Assert.Equal(hostKey, overlay.HostKey);
        Assert.Equal("site-settings", overlay.OverlayKey);
        Assert.Equal("1.2.3", overlay.OverlayVersion);
        Assert.Equal("sample", overlay.ModuleKey);
        Assert.Equal("1.0.0", overlay.ArtifactVersion);
        var file = Assert.Single(overlay.ConfigurationFiles);
        Assert.Equal("appsettings.json", file.RelativePath);
        Assert.Equal("replace", file.MergeMode);
        Assert.Equal("{\"setting\":true}", file.FileContent);
    }

    [Theory]
    [InlineData("../settings", "1:2?3")]
    [InlineData("folder\\settings", "1/2/3")]
    public async Task OverlayIdentityFields_CannotBecomeArchivePaths(string overlayKey, string version)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Join(_root, "package.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var item = await PortableModulePackageService.AddConfigOverlayEntryAsync(
                archive, new(StringComparer.OrdinalIgnoreCase),
                (overlayKey, "*", "AllHosts", version, "{}"), CancellationToken.None);
            var segments = item["path"]!.GetValue<string>().Split('/');
            Assert.Equal(4, segments.Length);
            Assert.All(segments, segment =>
            {
                Assert.DoesNotContain("..", segment);
                Assert.Equal(-1, segment.IndexOfAny(Path.GetInvalidFileNameChars()));
            });
        }
    }

    [Fact]
    public async Task AllHostsAndHostNamedAllHosts_ExportAsSeparateItems()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Join(_root, "package.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new JsonArray();
            foreach (var (scope, hostKey) in new[] { ("AllHosts", "*"), ("Host", "all-hosts") })
            {
                var json = ConfigOverlayExportJson.Build(
                    """{"overlayKey":"site-settings","overlayVersion":"1.0.0"}""",
                    scope, hostKey, [new("appsettings.json", "{}")]);
                items.Add(await PortableModulePackageService.AddConfigOverlayEntryAsync(
                    archive, used, ("site-settings", hostKey, scope, "1.0.0", json), CancellationToken.None));
            }

            await PortableModulePackageService.WriteUniversalPackageManifestAsync(
                archive, "sample", "1.0.0", "Sample", items, CancellationToken.None);
        }

        var package = new UniversalModulePackageReader().ExtractToDirectory(path, Path.Join(_root, "extracted"));
        Assert.Equal(2, package.Items.Count);
        var scopes = new List<ConfigOverlayHostScope>();
        foreach (var item in package.Items)
        {
            var overlay = await new ConfigOverlayPackageReader().ReadConfigOverlayAsync(
                item.ExtractedPath, item.SourceName, CancellationToken.None);
            scopes.Add(overlay.HostScope);
        }

        Assert.Contains(ConfigOverlayHostScope.AllHosts, scopes);
        Assert.Contains(ConfigOverlayHostScope.Host, scopes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
