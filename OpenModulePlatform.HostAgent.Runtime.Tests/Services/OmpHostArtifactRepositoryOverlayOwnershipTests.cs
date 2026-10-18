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

    [Fact]
    public void ResolveDesiredOverlayFiles_ExcludesReservedAndPayloadPaths()
    {
        var files = new[]
        {
            new ArtifactConfigurationFileDescriptor
            {
                ArtifactConfigurationFileId = 1,
                ArtifactId = 42,
                RelativePath = @"App_Data\omp-deployment.lock.json",
                FileContent = "{ }",
                IsOverlayOwnedWholeFile = true
            },
            new ArtifactConfigurationFileDescriptor
            {
                ArtifactConfigurationFileId = 2,
                ArtifactId = 42,
                RelativePath = "payload.config.js",
                FileContent = "// overlay",
                IsOverlayOwnedWholeFile = true
            },
            new ArtifactConfigurationFileDescriptor
            {
                ArtifactConfigurationFileId = 3,
                ArtifactId = 42,
                RelativePath = @"App_Data\feature.json",
                FileContent = "{ }",
                IsOverlayOwnedWholeFile = true
            }
        };
        var payloadPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "payload.config.js" };

        var entries = AppliedOverlayFileReconciler.ResolveDesiredOverlayFiles(
            files, new Dictionary<string, string>(), payloadPaths);

        var entry = Assert.Single(entries);
        Assert.Equal(@"App_Data\feature.json", entry.RelativePath);
    }

    [Fact]
    public void RemoveOwnedFiles_ReportsOnlyActuallyRemovedCount()
    {
        var root = Path.Join(Path.GetTempPath(), "omp-remove-count-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Join(root, "a.js"), "aaa");
            var plan = new OverlayRemovalPlan(
                HasRemovals: true,
                RemoveEntries:
                [
                    new AppliedOverlayFileEntry { RelativePath = "a.js", ContentLength = 3, ContentSha256 = ComputeSha256("aaa") },
                    new AppliedOverlayFileEntry { RelativePath = "gone.js", ContentLength = 4, ContentSha256 = ComputeSha256("gone") }
                ],
                Warning: null,
                ShouldRefreshInventory: false,
                RetainedEntries: []);

            var result = AppliedOverlayFileReconciler.RemoveOwnedFiles(
                root, plan, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            Assert.Equal(1, result.RemovedCount);
            Assert.Null(result.Warning);
            Assert.False(File.Exists(Path.Join(root, "a.js")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ComputeSha256(string content)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
