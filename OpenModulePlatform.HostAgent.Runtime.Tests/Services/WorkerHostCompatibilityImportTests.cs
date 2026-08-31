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
    public void ValidateWorkerHostRequirement_RejectsOlderSelectedHostWithBothVersions()
    {
        var requirement = Requirement("0.3.46");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ArtifactZipImportService.ValidateWorkerHostRequirement(requirement, "0.3.45"));

        Assert.Contains("omp-workerprocesshost", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0.3.46", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0.3.45", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateWorkerHostRequirement_AllowsEqualOrNewerSelectedHost()
    {
        ArtifactZipImportService.ValidateWorkerHostRequirement(Requirement("0.3.46"), "0.3.46");
        ArtifactZipImportService.ValidateWorkerHostRequirement(Requirement("0.3.46"), "0.3.47");
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
