using System.IO.Compression;
using System.Text;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Package builds stamp source provenance into the universal package manifest
/// (sourceCommitSha, sourceDirty and, for a consumer that compiles against
/// sibling repositories, a sharedSources array) and into every artifact
/// manifest (sourceRepositoryKey, sourceCommitSha, sourceDirty). The HostAgent
/// import must treat those fields as optional: a package built before the
/// stamp existed and a package that carries it must import identically.
/// </summary>
public sealed class SourceProvenanceManifestFieldsTests : IDisposable
{
    private const string ArtifactEntryPath = "artifacts/provmod__provapp__web-app__prov-target__1.2.3.zip";

    private readonly string _testRoot = Path.Join(
        Path.GetTempPath(),
        "omp-provenance-fields-" + Guid.NewGuid().ToString("N"));

    public SourceProvenanceManifestFieldsTests() => Directory.CreateDirectory(_testRoot);

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

    [Theory]
    [InlineData("")]
    [InlineData("""
        "sourceCommitSha": "0123456789abcdef0123456789abcdef01234567",
        "sourceDirty": false,
        """)]
    [InlineData("""
        "sourceCommitSha": "0123456789abcdef0123456789abcdef01234567",
        "sourceDirty": true,
        "sharedSources": [
          { "repositoryKey": "openmoduleplatform", "commitSha": "89abcdef0123456789abcdef0123456789abcdef", "dirty": true }
        ],
        """)]
    public void UniversalPackageReadsTheSameWithOrWithoutProvenanceFields(string provenanceFields)
    {
        var packagePath = Path.Join(_testRoot, "package.zip");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, ArtifactEntryPath, CreateArtifactPackageBytes(string.Empty));
            WriteEntry(archive, UniversalModulePackageReader.ManifestEntryName, Encoding.UTF8.GetBytes($$"""
                {
                  "formatVersion": 1,
                  "objectType": "universal-module-package",
                  "packageKey": "provenance-package",
                  "packageVersion": "20260929-1200",
                  {{provenanceFields}}
                  "items": [
                    { "kind": "artifact-package", "path": "{{ArtifactEntryPath}}", "version": "1.2.3" }
                  ]
                }
                """));
        }

        var package = new UniversalModulePackageReader().ExtractToDirectory(
            packagePath,
            Path.Join(_testRoot, "extracted"));

        Assert.Equal("provenance-package", package.PackageKey);
        Assert.Equal("20260929-1200", package.PackageVersion);
        var item = Assert.Single(package.Items);
        Assert.Equal(UniversalModulePackageItemKind.ArtifactPackage, item.Kind);
        Assert.Equal("1.2.3", item.Version);
        Assert.True(File.Exists(item.ExtractedPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("""
        "sourceRepositoryKey": "openmoduleplatform",
        "sourceCommitSha": "0123456789abcdef0123456789abcdef01234567",
        "sourceDirty": true,
        """)]
    public void ArtifactPackageExtractsTheSameWithOrWithoutProvenanceFields(string provenanceFields)
    {
        var artifactPath = Path.Join(_testRoot, "artifact.zip");
        File.WriteAllBytes(artifactPath, CreateArtifactPackageBytes(provenanceFields));

        var result = new ArtifactPackageExtractor().Extract(artifactPath, Path.Join(_testRoot, "staging"));

        Assert.True(result.UsesManifestEnvelope);
        Assert.Equal("1.0.0", result.MinModuleDefinitionVersion);
        Assert.Equal(
            "payload",
            File.ReadAllText(Path.Join(result.ArtifactContentPath, "app.txt")));
    }

    private static byte[] CreateArtifactPackageBytes(string provenanceFields)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "payload/app.txt", Encoding.UTF8.GetBytes("payload"));
            WriteEntry(archive, "omp-artifact-package.json", Encoding.UTF8.GetBytes($$"""
                {
                  "formatVersion": 1,
                  "moduleKey": "provmod",
                  "appKey": "provapp",
                  "packageType": "web-app",
                  "targetName": "prov-target",
                  "version": "1.2.3",
                  {{provenanceFields}}
                  "minModuleDefinitionVersion": "1.0.0",
                  "payload": { "type": "directory", "path": "payload" }
                }
                """));
        }

        return memory.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
