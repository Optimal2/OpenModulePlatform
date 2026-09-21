// File: OpenModulePlatform.Bootstrapper.Tests/SelectiveBuildConfigurationFilesTests.cs
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using OpenModulePlatform.Artifacts;
using Xunit;

namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// Regression tests for manifest-declared artifact configuration files in the selective build.
/// </summary>
/// <remarks>
/// Found 2026-09-21 while verifying a Portal configuration change in a running installation: the
/// Bootstrapper refresh built the artifact with an empty configuration-files section, so the
/// packaged appsettings.json (SecurityHeaders, NLog) never reached the host even though
/// omp-components.json declared it and the script-built packages carried it. Three things have to
/// hold: the manifest entry is read, the file ends up in the artifact, and a content change in the
/// file changes the source stamp so the artifact is rebuilt at all.
/// </remarks>
public sealed class SelectiveBuildConfigurationFilesTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "omp-selective-config-tests-" + Guid.NewGuid().ToString("N"));

    public SelectiveBuildConfigurationFilesTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort; the temp folder is unique per test class instance.
        }
    }

    private Program.ManifestComponent ReadPortalComponent(string configurationJson)
    {
        var packagingDirectory = Path.Join(_root, "OpenModulePlatform.Portal", "Packaging");
        Directory.CreateDirectory(packagingDirectory);
        File.WriteAllText(Path.Join(packagingDirectory, "appsettings.json"), configurationJson, new UTF8Encoding(false));

        var manifest = JsonNode.Parse("""
            {
              "components": [
                {
                  "componentKey": "omp-portal-web",
                  "moduleKey": "omp_portal",
                  "appKey": "omp_portal",
                  "packageType": "web-app",
                  "targetName": "omp-portal",
                  "version": "0.3.999",
                  "relativePathTemplate": "omp-portal/web/{version}",
                  "projectPath": "OpenModulePlatform.Portal",
                  "packageFileTemplate": "payload/OpenModulePlatform.Portal.zip",
                  "artifactConfigurationFiles": [
                    { "relativePath": "appsettings.json", "sourcePath": "OpenModulePlatform.Portal/Packaging/appsettings.json" }
                  ]
                }
              ]
            }
            """)!;

        return Assert.Single(Program.ReadManifestComponents(manifest, _root, "openmoduleplatform"));
    }

    [Fact]
    public void ReadManifestComponents_ReadsArtifactConfigurationFilesResolvedAgainstSourceRoot()
    {
        var component = ReadPortalComponent("{ \"SecurityHeaders\": { \"Enabled\": true } }");

        var file = Assert.Single(component.ArtifactConfigurationFiles!);
        Assert.Equal("appsettings.json", file.RelativePath);
        Assert.Equal(
            Path.GetFullPath(Path.Join(_root, "OpenModulePlatform.Portal", "Packaging", "appsettings.json")),
            file.SourcePath);
    }

    [Fact]
    public void SelectiveBuild_PackagesTheDeclaredConfigurationFile()
    {
        const string configuration = "{ \"SecurityHeaders\": { \"Enabled\": true }, \"NLog\": { } }";
        var component = ReadPortalComponent(configuration);
        var payloadRoot = Path.Join(_root, "publish");
        Directory.CreateDirectory(payloadRoot);
        File.WriteAllText(Path.Join(payloadRoot, "OpenModulePlatform.Portal.dll"), "not really a dll");
        var destination = Path.Join(_root, "artifact.zip");

        var configurationFiles = Program.ReadArtifactConfigurationFilesForBuild(component);
        new ArtifactPackageWriter().CreateFromPayloadDirectory(payloadRoot, destination, configurationFiles);

        var packaged = Assert.Single(Program.ReadArtifactPackageConfigurationFilesOnly(destination));
        Assert.Equal("appsettings.json", packaged.RelativePath);
        Assert.Equal(configuration, packaged.FileContent);

        using var archive = ZipFile.OpenRead(destination);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("payload/appsettings.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadArtifactConfigurationFilesForBuild_FailsLoudlyWhenTheSourceIsMissing()
    {
        var component = ReadPortalComponent("{}");
        File.Delete(component.ArtifactConfigurationFiles![0].SourcePath);

        var exception = Assert.Throws<FileNotFoundException>(() => Program.ReadArtifactConfigurationFilesForBuild(component));
        Assert.Contains("omp-portal-web", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationFilesStamp_ChangesWithTheFileContent()
    {
        var component = ReadPortalComponent("{ \"Portal\": { \"Title\": \"A\" } }");
        var before = Program.GetArtifactConfigurationFilesStamp(component);

        File.WriteAllText(component.ArtifactConfigurationFiles![0].SourcePath, "{ \"Portal\": { \"Title\": \"B\" } }", new UTF8Encoding(false));
        var after = Program.GetArtifactConfigurationFilesStamp(component);

        Assert.NotEqual(before, after);
        Assert.StartsWith("config:appsettings.json=", before, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationFilesStamp_IsStableWhenNothingIsDeclared()
    {
        var manifest = JsonNode.Parse("""
            {
              "components": [
                {
                  "componentKey": "omp-hostagent-service",
                  "moduleKey": "omp_core",
                  "appKey": "omp_hostagent",
                  "packageType": "host-agent",
                  "targetName": "omp-hostagent",
                  "version": "0.3.999",
                  "relativePathTemplate": "omp-hostagent/{version}",
                  "projectPath": "OpenModulePlatform.HostAgent.WindowsService",
                  "packageFileTemplate": "payload/OpenModulePlatform.HostAgent.WindowsService.zip"
                }
              ]
            }
            """)!;
        var component = Assert.Single(Program.ReadManifestComponents(manifest, _root, "openmoduleplatform"));

        Assert.Empty(component.ArtifactConfigurationFiles!);
        Assert.Empty(Program.ReadArtifactConfigurationFilesForBuild(component));
        Assert.Equal("config:none", Program.GetArtifactConfigurationFilesStamp(component));
    }
}
