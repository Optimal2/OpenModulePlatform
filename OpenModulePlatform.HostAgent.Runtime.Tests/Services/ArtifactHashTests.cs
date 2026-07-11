using System.Text;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class ArtifactHashTests
{
    [Fact]
    public async Task ComputeSha256Async_SingleFile_MatchesKnownHelloHash()
    {
        var path = await WriteTempFileAsync("hello");

        var hash = await ArtifactHash.ComputeSha256Async(path, CancellationToken.None);

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
    }

    [Fact]
    public async Task ComputeSha256Async_Directory_IsStableForSameContents()
    {
        var dir = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Join(dir, "a.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Join(dir, "sub", "b.txt"), "beta");

        var first = await ArtifactHash.ComputeSha256Async(dir, CancellationToken.None);
        var second = await ArtifactHash.ComputeSha256Async(dir, CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ComputeSha256Async_NonExistentPath_ThrowsFileNotFoundException()
    {
        var path = Path.Join(Path.GetTempPath(), $"omp-missing-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<FileNotFoundException>(() => ArtifactHash.ComputeSha256Async(path, CancellationToken.None));
    }

    [Fact]
    public async Task ComputeSha256Async_EmptyFile_MatchesKnownEmptyHash()
    {
        var path = await WriteTempFileAsync("");

        var hash = await ArtifactHash.ComputeSha256Async(path, CancellationToken.None);

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-hash-dir-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(Path.Join(path, "sub"));
        return path;
    }

    private static async Task<string> WriteTempFileAsync(string content)
    {
        var path = Path.Join(Path.GetTempPath(), $"omp-hash-file-{Guid.NewGuid():N}.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(content));
        return path;
    }
}
