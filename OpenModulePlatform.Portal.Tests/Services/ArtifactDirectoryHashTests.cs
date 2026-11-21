using System.Reflection;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.Portal.Pages.Admin;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ArtifactDirectoryHashTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "omp-portal-hash-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PortalDirectoryHashes_SkipLinkedDirectoriesLikeHostAgent()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Join(_root, "real.txt"), "payload");
        var outside = Path.Join(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Join(outside, "secret.txt"), "not part of the artifact");
        var artifact = Path.Join(_root, "artifact");
        Directory.CreateDirectory(artifact);
        File.WriteAllText(Path.Join(artifact, "real.txt"), "payload");
        Assert.True(TryCreateJunction(Path.Join(artifact, "linked"), outside), "The test requires permission to create a directory link.");

        var expected = await ArtifactHash.ComputeSha256Async(artifact, CancellationToken.None);
        Assert.Equal(expected, await ComputePortalHashAsync(typeof(ArtifactUploadModel), artifact));
        Assert.Equal(expected, await ComputePortalHashAsync(typeof(PortableModulePackageService), artifact));
    }

    [Fact]
    public void AllDirectoryHashImplementations_UseOrdinalPathOrderingAndNoFollowEnumeration()
    {
        AssertHashImplementation("OpenModulePlatform.Portal", "Pages", "Admin", "ArtifactUpload.cshtml.cs");
        AssertHashImplementation("OpenModulePlatform.Portal", "Services", "PortableModulePackageService.cs");
        AssertHashImplementation("OpenModulePlatform.HostAgent.Runtime", "Services", "ArtifactHash.cs");
        AssertHashImplementation("OpenModulePlatform.Installation", "InstallationEngine.Artifacts.cs");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task<string> ComputePortalHashAsync(Type owner, string path)
    {
        var method = owner.GetMethod("ComputeDirectorySha256Async", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<string>>(method.Invoke(null, [path, CancellationToken.None]));
        return await task;
    }

    private static void AssertHashImplementation(params string[] relativePath)
    {
        var source = OmpRepositoryFiles.ReadRepositoryTextFile(relativePath);
        Assert.Contains("OmpReparsePointGuard.EnsureNotReparsePoint(path", source, StringComparison.Ordinal);
        Assert.Contains("OmpReparsePointGuard.RecursiveNoFollow", source, StringComparison.Ordinal);
        Assert.Contains("Path.GetRelativePath(path, file), StringComparer.Ordinal)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase)", source, StringComparison.Ordinal);
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
