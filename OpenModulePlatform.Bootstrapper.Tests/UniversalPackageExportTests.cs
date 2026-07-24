using System.IO.Compression;
using System.Text;

namespace OpenModulePlatform.Bootstrapper.Tests;

public sealed class UniversalPackageExportTests : IDisposable
{
    private const string ArtifactFileName = "odv__odv_site__web-app__odv-site__2.4.58.zip";
    private const string StaleArtifactFileName = "odv__odv_site__web-app__odv-site__2.6.9.zip";

    private readonly string _testRoot = Path.Join(
        Path.GetTempPath(),
        "omp-bootstrapper-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void FilterLatestKeepsHighestVersionPerArtifactIdentity()
    {
        // Reproduces the stale-leftover scenario: two artifact packages with the
        // same identity where a stale, higher-versioned leftover (2.6.9) sits next
        // to the correct package (2.4.58). The current GUI semantics deliberately
        // pick the highest version; making this manifest-aware is a separate
        // design decision tracked outside this change.
        var correct = CreateArtifactCandidate(ArtifactFileName, "2.4.58");
        var stale = CreateArtifactCandidate(StaleArtifactFileName, "2.6.9");

        var filtered = Program.FilterLatestUniversalPackageVersionedObjects([correct, stale]);

        var selected = Assert.Single(filtered);
        Assert.Equal(stale.PackagePath, selected.PackagePath);
    }

    [Fact]
    public void FilterLatestKeepsNonVersionedItemsUntouched()
    {
        var artifact = CreateArtifactCandidate(ArtifactFileName, "2.4.58");
        var definition = new Program.UniversalPackageCandidate(
            "module-definition",
            Path.Join(_testRoot, "omp_core.module-definition.json"),
            "module-definitions/omp_core.module-definition.json",
            "1.0.0");

        var filtered = Program.FilterLatestUniversalPackageVersionedObjects([artifact, definition]);

        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, item => item.PackagePath == definition.PackagePath);
    }

    [Fact]
    public void ExportFailsWhenArtifactPayloadContainsTopLevelRuntimeConfiguration()
    {
        var artifactPath = CreateArtifactPackage(
            ArtifactFileName,
            topLevelEntries: new Dictionary<string, string>
            {
                ["appsettings.json"] = "{}"
            });

        var request = CreateRequest(artifactPath, ArtifactFileName, "2.4.58");

        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.CreateUniversalPackageZip(request));
        Assert.Contains("runtime configuration", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("appsettings.json", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportFailsWhenNestedPayloadZipContainsRuntimeConfiguration()
    {
        var artifactPath = CreateArtifactPackage(
            ArtifactFileName,
            nestedPayloadEntries: new Dictionary<string, string>
            {
                ["configuration/odv.site.config.js"] = "window.odv = {};",
                ["bin/odv.dll"] = "dll"
            });

        var request = CreateRequest(artifactPath, ArtifactFileName, "2.4.58");

        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.CreateUniversalPackageZip(request));
        Assert.Contains("runtime configuration", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("odv.site.config.js", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportSucceedsForCleanArtifactPackageWithConfigurationSection()
    {
        var artifactPath = CreateArtifactPackage(
            ArtifactFileName,
            nestedPayloadEntries: new Dictionary<string, string>
            {
                ["bin/odv.dll"] = "dll"
            },
            topLevelEntries: new Dictionary<string, string>
            {
                // Configuration-section entries carry an index prefix and are
                // legitimate package content; they must not trip the guard.
                ["configuration/000-appsettings.json"] = "{}"
            });

        var request = CreateRequest(artifactPath, ArtifactFileName, "2.4.58");

        var result = Program.CreateUniversalPackageZip(request);

        Assert.True(File.Exists(result.PackagePath));
        using var exported = ZipFile.OpenRead(result.PackagePath);
        Assert.Contains(
            exported.Entries,
            entry => entry.FullName == "artifacts/" + ArtifactFileName);
    }

    private Program.UniversalPackageCandidate CreateArtifactCandidate(string fileName, string version)
        => new(
            "artifact-package",
            Path.Join(_testRoot, fileName),
            "artifacts/" + fileName,
            version);

    private Program.UniversalPackageBuildRequest CreateRequest(
        string artifactSourcePath,
        string artifactFileName,
        string version)
        => new(
            "test-package",
            "1.0.0",
            "Test package",
            string.Empty,
            null,
            "No target host",
            Path.Join(_testRoot, "export", "test-package__global__1.0.0.zip"),
            [
                new Program.UniversalPackageCandidate(
                    "artifact-package",
                    artifactSourcePath,
                    "artifacts/" + artifactFileName,
                    version)
            ]);

    private string CreateArtifactPackage(
        string fileName,
        IReadOnlyDictionary<string, string>? topLevelEntries = null,
        IReadOnlyDictionary<string, string>? nestedPayloadEntries = null)
    {
        Directory.CreateDirectory(_testRoot);
        var path = Path.Join(_testRoot, fileName);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (topLevelEntries is not null)
            {
                foreach (var (name, content) in topLevelEntries)
                {
                    WriteEntry(archive, name, content);
                }
            }

            if (nestedPayloadEntries is not null)
            {
                var nestedEntry = archive.CreateEntry("payload/artifact.zip");
                using var entryStream = nestedEntry.Open();
                using var memory = new MemoryStream();
                using (var nested = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (name, content) in nestedPayloadEntries)
                    {
                        WriteEntry(nested, name, content);
                    }
                }

                memory.Position = 0;
                memory.CopyTo(entryStream);
            }
        }

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
