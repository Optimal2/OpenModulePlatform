using System.IO.Compression;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.Worker.Abstractions.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class WorkerHostCompatibilityImportTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "OpenModulePlatform",
        "WorkerHostCompatibilityImportTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidateWorkerHostRequirement_RejectsOlderSelectedAndProvisionedHostWithAllVersions()
    {
        var requirement = Requirement("0.3.46");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ArtifactZipImportService.ValidateWorkerHostRequirement(requirement, "0.3.45", "0.3.45"));

        Assert.Contains("omp-workerprocesshost", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0.3.46", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0.3.45", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateWorkerHostRequirement_AllowsEqualOrNewerSelectedHost()
    {
        ArtifactZipImportService.ValidateWorkerHostRequirement(Requirement("0.3.46"), "0.3.46", "0.3.46");
        ArtifactZipImportService.ValidateWorkerHostRequirement(Requirement("0.3.46"), "0.3.47", "0.3.47");
    }

    [Fact]
    public void ValidateWorkerHostRequirement_RejectsSelectedHostThatHasNotBeenProvisioned()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ArtifactZipImportService.ValidateWorkerHostRequirement(Requirement("0.3.46"), "0.3.47", null));

        Assert.Contains("0.3.47", exception.Message, StringComparison.Ordinal);
        Assert.Contains("<not provisioned>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateWorkerHostRequirement_RejectsWhenEitherSelectedOrProvisionedVersionIsTooOld()
    {
        var selectedAhead = Assert.Throws<InvalidOperationException>(() =>
            ArtifactZipImportService.ValidateWorkerHostRequirement(Requirement("0.3.46"), "0.3.47", "0.3.45"));
        var provisionedAhead = Assert.Throws<InvalidOperationException>(() =>
            ArtifactZipImportService.ValidateWorkerHostRequirement(Requirement("0.3.46"), "0.3.45", "0.3.47"));

        Assert.Contains("0.3.47", selectedAhead.Message, StringComparison.Ordinal);
        Assert.Contains("0.3.45", selectedAhead.Message, StringComparison.Ordinal);
        Assert.Contains("0.3.45", provisionedAhead.Message, StringComparison.Ordinal);
        Assert.Contains("0.3.47", provisionedAhead.Message, StringComparison.Ordinal);
    }

    // Measured 2026-09-30: a universal package carrying a new WorkerProcessHost (0.3.71)
    // and worker plugins requiring 0.3.21 lost the plugins on its first import, because the
    // host it had just selected was not provisioned until the next HostAgent cycle.
    [Fact]
    public void ValidateWorkerHostRequirement_AllowsSelectedHostImportedFromTheSamePackageBeforeProvisioning()
    {
        ArtifactZipImportService.ValidateWorkerHostRequirement(
            Requirement("0.3.21"),
            "0.3.71",
            null,
            ["0.3.71"]);
    }

    [Fact]
    public void ValidateWorkerHostRequirement_StillRejectsUnprovisionedSelectedHostThatIsNotInThePackage()
    {
        var otherPackageHost = Assert.Throws<InvalidOperationException>(() =>
            ArtifactZipImportService.ValidateWorkerHostRequirement(Requirement("0.3.21"), "0.3.71", null, ["0.3.70"]));
        var noPackageHost = Assert.Throws<InvalidOperationException>(() =>
            ArtifactZipImportService.ValidateWorkerHostRequirement(Requirement("0.3.21"), "0.3.71", null, []));

        Assert.Contains("<not provisioned>", otherPackageHost.Message, StringComparison.Ordinal);
        Assert.Contains("<not provisioned>", noPackageHost.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateWorkerHostRequirement_StillRejectsSamePackageHostThatIsTooOld()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ArtifactZipImportService.ValidateWorkerHostRequirement(Requirement("0.3.80"), "0.3.71", null, ["0.3.71"]));

        Assert.Contains("0.3.80", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateWorkerHostRequirement_StillRejectsWhenThePackageHostIsNotTheSelectedOne()
    {
        // The package brought a compatible host, but another (older) artifact is selected:
        // WorkerManager launches the selected host, so the package host does not count.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ArtifactZipImportService.ValidateWorkerHostRequirement(Requirement("0.3.46"), "0.3.45", "0.3.45", ["0.3.71"]));

        Assert.Contains("0.3.45", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderWorkerHostArtifactsFirst_MovesWorkerHostPackagesAheadAndKeepsTheRestInOrder()
    {
        string[] paths =
        [
            "example__example_worker__worker-plugin__example-worker__0.3.189.zip",
            "omp_core__omp_portal__web__omp-portal__0.3.500.zip",
            "omp_core__omp_workerprocesshost__worker-host__omp-workerprocesshost__0.3.71.zip",
            "other__other_worker__worker-plugin__other-worker__0.1.25.zip"
        ];

        var ordered = ArtifactZipImportService.OrderWorkerHostArtifactsFirst(paths, static path => path);

        Assert.Equal(
            [paths[2], paths[0], paths[1], paths[3]],
            ordered);
    }

    [Fact]
    public void ArtifactPackageWriter_CarriesWorkerHostRequirementInEnvelopeAndPayload()
    {
        var payloadRoot = Path.Join(_root, "payload");
        var packagePath = Path.Join(_root, "plugin.zip");
        var extractionRoot = Path.Join(_root, "extracted");
        Directory.CreateDirectory(payloadRoot);
        File.WriteAllText(Path.Join(payloadRoot, "Plugin.dll"), "fixture");

        new ArtifactPackageWriter().CreateFromPayloadDirectory(
            payloadRoot,
            packagePath,
            [],
            minWorkerHostVersion: "0.3.46");

        var extracted = new ArtifactPackageExtractor().Extract(packagePath, extractionRoot);

        Assert.Equal("omp-workerprocesshost", extracted.WorkerHostRequirement?.ComponentKey);
        Assert.Equal("0.3.46", extracted.WorkerHostRequirement?.MinVersion);
        Assert.True(File.Exists(Path.Join(
            extracted.ArtifactContentPath,
            WorkerPluginCompatibilityManifest.FileName)));

        using var package = ZipFile.OpenRead(packagePath);
        using var reader = new StreamReader(package.GetEntry(ArtifactPackageExtractor.ManifestEntryName)!.Open());
        var manifest = reader.ReadToEnd();
        Assert.Contains("\"workerHost\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"0.3.46\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactPackageWriter_PreservesEmbeddedRequirementWhenReExportingPayload()
    {
        var payloadRoot = Path.Join(_root, "payload");
        var firstPackagePath = Path.Join(_root, "first.zip");
        var firstExtractionRoot = Path.Join(_root, "first-extracted");
        var exportedPackagePath = Path.Join(_root, "exported.zip");
        var exportedExtractionRoot = Path.Join(_root, "exported-extracted");
        Directory.CreateDirectory(payloadRoot);
        File.WriteAllText(Path.Join(payloadRoot, "Plugin.dll"), "fixture");

        var writer = new ArtifactPackageWriter();
        writer.CreateFromPayloadDirectory(
            payloadRoot,
            firstPackagePath,
            [],
            minWorkerHostVersion: "0.3.46");
        var firstExtraction = new ArtifactPackageExtractor().Extract(firstPackagePath, firstExtractionRoot);

        writer.CreateFromPayloadDirectory(firstExtraction.ArtifactContentPath, exportedPackagePath, []);
        var exported = new ArtifactPackageExtractor().Extract(exportedPackagePath, exportedExtractionRoot);

        Assert.Equal("omp-workerprocesshost", exported.WorkerHostRequirement?.ComponentKey);
        Assert.Equal("0.3.46", exported.WorkerHostRequirement?.MinVersion);
    }

    private static WorkerHostCompatibilityRequirement Requirement(string version)
        => new()
        {
            ComponentKey = WorkerPluginCompatibilityManifest.DefaultWorkerHostComponentKey,
            MinVersion = version
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
