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
    public void Plan_UnknownPayloadInventory_PlansNoRemovalWarnsAndRetains()
    {
        // The recorded overlay-owned file really collides with the artifact payload
        // (the source ships the same path), but payload enumeration fails partway --
        // simulated deterministically through the enumerator seam, so no ACL or
        // system mutation is needed. The incomplete inventory cannot prove the
        // collision, so no removal may be planned: the file stays, a warning names
        // the unknown inventory (never a false "protected payload" claim), and the
        // entry is retained so a later healthy cycle retries instead of forgetting.
        var root = Path.Join(Path.GetTempPath(), "omp-partial-payload-" + Guid.NewGuid().ToString("N"));
        var stateRoot = Path.Join(root, "state");
        var target = Path.Join(root, "target");
        var source = Path.Join(root, "source");
        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(source);
        try
        {
            const string relativePath = "payload.config.js";
            const string content = "// overlay";
            File.WriteAllText(Path.Join(source, relativePath), "// payload");
            File.WriteAllText(Path.Join(target, relativePath), content);
            var appInstanceId = Guid.NewGuid();
            var entry = new AppliedOverlayFileEntry
            {
                RelativePath = relativePath,
                ContentLength = System.Text.Encoding.UTF8.GetByteCount(content),
                ContentSha256 = ComputeSha256(content)
            };
            Assert.Null(AppliedOverlayFileInventory.TryWrite(stateRoot, target, appInstanceId, "app", "host", [entry]));

            var deployment = new WebAppDeploymentDescriptor
            {
                HostId = Guid.NewGuid(),
                HostKey = "host",
                AppInstanceId = appInstanceId,
                AppInstanceKey = "app",
                AppKey = "app",
                ModuleInstanceKey = "module",
                DisplayName = "Test App",
                ArtifactId = 1,
                Version = "1.0.0",
                SourceLocalPath = source,
                InstallPath = target
            };

            var incomplete = AppliedOverlayFileReconciler.EnumeratePayloadPaths(source, FailAfterFirstFile);
            Assert.False(incomplete.IsComplete);

            var plan = AppliedOverlayFileReconciler.Plan(
                stateRoot,
                target,
                deployment,
                desiredFiles: [],
                payload: incomplete);

            Assert.False(plan.HasRemovals);
            Assert.Empty(plan.RemoveEntries);
            Assert.NotNull(plan.Warning);
            Assert.Contains("payload", plan.Warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cannot be ruled out", plan.Warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No file was removed", plan.Warning, StringComparison.Ordinal);
            Assert.Contains(plan.RetainedEntries, retained => string.Equals(retained.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            Assert.False(plan.ShouldRefreshInventory);

            var removal = AppliedOverlayFileReconciler.RemoveOwnedFiles(
                target, plan, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Assert.Equal(0, removal.RemovedCount);
            Assert.Equal(content, File.ReadAllText(Path.Join(target, relativePath)));

            // Recovery control: with a complete enumeration the same fixture proves
            // the collision, drops the record without deleting, and stays quiet --
            // the unknown-inventory cycle caused no permanent deployment harm.
            var complete = AppliedOverlayFileReconciler.EnumeratePayloadPaths(source);
            Assert.True(complete.IsComplete);
            Assert.Contains(relativePath, complete.Paths);

            var recovered = AppliedOverlayFileReconciler.Plan(
                stateRoot,
                target,
                deployment,
                desiredFiles: [],
                payload: complete);

            Assert.False(recovered.HasRemovals);
            Assert.Empty(recovered.RemoveEntries);
            Assert.Null(recovered.Warning);
            Assert.Equal(content, File.ReadAllText(Path.Join(target, relativePath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<string> FailAfterFirstFile(string source)
    {
        yield return Path.Join(source, "payload.config.js");
        throw new IOException("Simulated mid-enumeration payload failure.");
    }

    [Fact]
    public void Plan_ForeignHostInventory_WarnsAboutHostNotAppInstance()
    {
        var root = Path.Join(Path.GetTempPath(), "omp-foreign-host-" + Guid.NewGuid().ToString("N"));
        var stateRoot = Path.Join(root, "state");
        var target = Path.Join(root, "target");
        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(target);
        try
        {
            const string content = "// overlay";
            File.WriteAllText(Path.Join(target, "odv.site.config.js"), content);
            var appInstanceId = Guid.NewGuid();
            var entry = new AppliedOverlayFileEntry
            {
                RelativePath = "odv.site.config.js",
                ContentLength = System.Text.Encoding.UTF8.GetByteCount(content),
                ContentSha256 = ComputeSha256(content)
            };
            Assert.Null(AppliedOverlayFileInventory.TryWrite(stateRoot, target, appInstanceId, "app", "other-host", [entry]));

            var deployment = new WebAppDeploymentDescriptor
            {
                HostId = Guid.NewGuid(),
                HostKey = "test-host",
                AppInstanceId = appInstanceId,
                AppInstanceKey = "app",
                AppKey = "app",
                ModuleInstanceKey = "module",
                DisplayName = "Test App",
                ArtifactId = 1,
                Version = "1.0.0",
                SourceLocalPath = Path.Join(root, "source"),
                InstallPath = target
            };

            var plan = AppliedOverlayFileReconciler.Plan(
                stateRoot,
                target,
                deployment,
                desiredFiles: [],
                payload: new AppliedOverlayFileReconciler.PayloadPathInventory(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), IsComplete: true));

            Assert.False(plan.HasRemovals);
            Assert.NotNull(plan.Warning);
            Assert.Contains("other-host", plan.Warning, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("app instance", plan.Warning, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
