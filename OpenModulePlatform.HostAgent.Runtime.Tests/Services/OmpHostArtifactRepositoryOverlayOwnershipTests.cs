using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Pure tests for the whole-file overlay ownership classification and the desired-set
/// resolution it feeds. These prove the classification logic without the SQL Server
/// fixtures the repository-level resolution tests require.
/// </summary>
public sealed class OmpHostArtifactRepositoryOverlayOwnershipTests
{
    [Fact]
    public void IsOverlayOwnedWholeFile_OverlayRowWithoutArtifactBase_IsOwned()
    {
        var artifactOwnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "appsettings.json" };

        Assert.True(OmpHostArtifactRepository.IsOverlayOwnedWholeFile(1, "odv.site.config.js", artifactOwnedPaths));
    }

    [Fact]
    public void IsOverlayOwnedWholeFile_ArtifactRow_IsNeverOwned()
    {
        var artifactOwnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "odv.site.config.js" };

        Assert.False(OmpHostArtifactRepository.IsOverlayOwnedWholeFile(0, "odv.site.config.js", artifactOwnedPaths));
    }

    [Fact]
    public void IsOverlayOwnedWholeFile_OverlayMergedOntoArtifactBase_IsNotOwned()
    {
        var artifactOwnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "appsettings.json" };

        Assert.False(OmpHostArtifactRepository.IsOverlayOwnedWholeFile(1, "appsettings.json", artifactOwnedPaths));
    }

    [Fact]
    public void ResolveDesiredOverlayFiles_KeepsOnlyOverlayOwnedWholeFilesAndRendersTokens()
    {
        var files = new[]
        {
            new ArtifactConfigurationFileDescriptor
            {
                ArtifactConfigurationFileId = 1,
                ArtifactId = 42,
                RelativePath = "appsettings.json",
                FileContent = "{ }"
            },
            new ArtifactConfigurationFileDescriptor
            {
                ArtifactConfigurationFileId = 2,
                ArtifactId = 42,
                RelativePath = "odv.site.config.js",
                FileContent = "window.Base = '{{Omp.HostKey}}';",
                IsOverlayOwnedWholeFile = true
            },
            new ArtifactConfigurationFileDescriptor
            {
                ArtifactConfigurationFileId = 3,
                ArtifactId = 42,
                RelativePath = "artifact.config.js",
                FileContent = "window.Artifact = true;"
            }
        };
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Omp.HostKey"] = "test-host"
        };

        var entries = AppliedOverlayFileReconciler.ResolveDesiredOverlayFiles(files, variables);

        var entry = Assert.Single(entries);
        Assert.Equal("odv.site.config.js", entry.RelativePath);
        Assert.Equal(ComputeSha256("window.Base = 'test-host';"), entry.ContentSha256);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("window.Base = 'test-host';"), entry.ContentLength);
    }

    private static string ComputeSha256(string content)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
