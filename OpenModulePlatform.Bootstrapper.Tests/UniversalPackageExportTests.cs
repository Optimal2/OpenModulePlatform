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
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
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
        // The guard runs before the output package is created, so a failed
        // export must not leave a truncated zip behind.
        Assert.False(File.Exists(request.OutputPath));
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

    [Fact]
    public void ExportSkipsRuntimeConfigurationGuardForNonArtifactItems()
    {
        // Widget-data zips are not artifact packages; the guard scope boundary
        // is TryParseUniversalPackageArtifactIdentity and must not reject them.
        var widgetDataPath = CreateArtifactPackage(
            "omp_core__dashboard__1.0.0.zip",
            topLevelEntries: new Dictionary<string, string>
            {
                ["appsettings.json"] = "{}"
            });
        var request = new Program.UniversalPackageBuildRequest(
            "test-package",
            "1.0.0",
            "Test package",
            string.Empty,
            null,
            "No target host",
            Path.Join(_testRoot, "export", "test-package__global__1.0.0.zip"),
            [
                new Program.UniversalPackageCandidate(
                    "widget-data",
                    widgetDataPath,
                    "widget-data/omp_core__dashboard__1.0.0.zip",
                    "1.0.0")
            ]);

        var result = Program.CreateUniversalPackageZip(request);

        Assert.True(File.Exists(result.PackagePath));
    }

    [Fact]
    public void ExportFailsOnCaseInsensitiveRuntimeConfigurationName()
    {
        var artifactPath = CreateArtifactPackage(
            ArtifactFileName,
            topLevelEntries: new Dictionary<string, string>
            {
                ["AppSettings.JSON"] = "{}"
            });

        var request = CreateRequest(artifactPath, ArtifactFileName, "2.4.58");

        Assert.Throws<InvalidOperationException>(
            () => Program.CreateUniversalPackageZip(request));
    }

    [Fact]
    public void ExportFailsOnUnprefixedConfigurationEntry()
    {
        // Legitimate configuration-section entries are always index-prefixed
        // (configuration/000-name.ext). An unprefixed match would also be
        // rejected by the import-time validators, so the export must fail.
        var artifactPath = CreateArtifactPackage(
            ArtifactFileName,
            topLevelEntries: new Dictionary<string, string>
            {
                ["configuration/appsettings.json"] = "{}"
            });

        var request = CreateRequest(artifactPath, ArtifactFileName, "2.4.58");

        Assert.Throws<InvalidOperationException>(
            () => Program.CreateUniversalPackageZip(request));
    }

    [Fact]
    public void ExportFailureMessageListsAllOffenders()
    {
        var artifactPath = CreateArtifactPackage(
            ArtifactFileName,
            topLevelEntries: new Dictionary<string, string>
            {
                ["appsettings.json"] = "{}",
                ["odv.site.config.js"] = "window.odv = {};"
            });

        var request = CreateRequest(artifactPath, ArtifactFileName, "2.4.58");

        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.CreateUniversalPackageZip(request));
        Assert.Contains("appsettings.json", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("odv.site.config.js", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportFailsWithClearErrorWhenNestedPayloadZipIsCorrupt()
    {
        var artifactPath = CreateArtifactPackage(
            ArtifactFileName,
            topLevelEntries: new Dictionary<string, string>
            {
                ["payload/artifact.zip"] = "this is not a zip"
            });

        var request = CreateRequest(artifactPath, ArtifactFileName, "2.4.58");

        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.CreateUniversalPackageZip(request));
        Assert.Contains("not a readable zip payload", exception.Message, StringComparison.Ordinal);
        Assert.Contains("payload/artifact.zip", exception.Message, StringComparison.Ordinal);
    }

    // Two builds within the same UTC minute get the same automatic name. The
    // export used to File.Delete an existing output and write over it, so the
    // second build replaced the first package silently. These tests use only
    // IOException and the message, so they compile and run against that old
    // behavior too -- which is how they were seen failing before the fix.
    [Fact]
    public void ExportRefusesToReplaceAnExistingPackage()
    {
        var artifactPath = CreateArtifactPackage(
            ArtifactFileName,
            nestedPayloadEntries: new Dictionary<string, string> { ["bin/app.dll"] = "first" });
        var first = CreateRequest(artifactPath, ArtifactFileName, "2.4.58");
        var firstResult = Program.CreateUniversalPackageZip(first);
        var firstBytes = File.ReadAllBytes(firstResult.PackagePath);

        var second = first with { PackageVersion = "9.9.9" };
        var ex = Assert.ThrowsAny<IOException>(() => Program.CreateUniversalPackageZip(second));

        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
        Assert.Contains(first.OutputPath, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(firstBytes, File.ReadAllBytes(firstResult.PackagePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(first.OutputPath)!));
    }

    [Fact]
    public async Task ConcurrentExportsOfTheSameNameNeverReplaceEachOther()
    {
        var artifactPath = CreateArtifactPackage(
            ArtifactFileName,
            nestedPayloadEntries: new Dictionary<string, string> { ["bin/app.dll"] = "dll" });
        var request = CreateRequest(artifactPath, ArtifactFileName, "2.4.58");

        const int builders = 6;
        using var start = new Barrier(builders);
        // LongRunning gives every builder its own thread, so the barrier releases
        // them together instead of waiting on a pool that has fewer threads.
        var builds = Enumerable.Range(0, builders)
            .Select(index => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    Program.CreateUniversalPackageZip(request with { PackageVersion = "race-" + index });
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        // Wait for every build to finish, failed ones included, without throwing:
        // each build's own exception is read from its task below.
        await Task.WhenAll(builds.Select(static build => build.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default)));
        var outcomes = builds
            .Select(static build => build.Exception?.GetBaseException())
            .ToArray();

        Assert.Equal(1, outcomes.Count(static outcome => outcome is null));
        Assert.All(
            outcomes.Where(static outcome => outcome is not null),
            outcome =>
            {
                var io = Assert.IsAssignableFrom<IOException>(outcome);
                Assert.Contains("already exists", io.Message, StringComparison.Ordinal);
            });

        // The one package on disk is whole and is the winner's own.
        var winner = Array.FindIndex(outcomes, static outcome => outcome is null);
        using var exported = ZipFile.OpenRead(request.OutputPath);
        var manifestEntry = exported.GetEntry("omp-universal-package.json");
        Assert.NotNull(manifestEntry);
        using var reader = new StreamReader(manifestEntry.Open());
        Assert.Contains($"\"race-{winner}\"", reader.ReadToEnd(), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(request.OutputPath)!));
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
