using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.Installation.Tests;

public sealed class ArtifactDirectoryHashTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "omp-installation-hash-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallationDirectoryHash_SkipsLinkedDirectoriesLikeHostAgent()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Join(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Join(outside, "secret.txt"), "not part of the artifact");
        var artifact = Path.Join(_root, "artifact");
        Directory.CreateDirectory(artifact);
        File.WriteAllText(Path.Join(artifact, "real.txt"), "payload");
        Assert.True(TryCreateJunction(Path.Join(artifact, "linked"), outside), "The test requires permission to create a directory link.");

        var expected = await ArtifactHash.ComputeSha256Async(artifact, CancellationToken.None);

        Assert.Equal(expected, InstallationEngine.ComputeDirectorySha256(artifact));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
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
