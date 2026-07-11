using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class ArtifactProvisionerTests : IDisposable
{
    private readonly string _centralRoot;
    private readonly string _localRoot;

    public ArtifactProvisionerTests()
    {
        _centralRoot = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-central-{Guid.NewGuid():N}"));
        _localRoot = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-local-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(_centralRoot);
        Directory.CreateDirectory(_localRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_centralRoot, recursive: true);
            Directory.Delete(_localRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task EnsureAsync_ExistingLocalMatchingHash_SucceedsWithoutCopy()
    {
        var sourcePath = WriteSourceFile("file.txt", "content");
        var expectedHash = await HashAsync(sourcePath);
        var localPath = Path.Join(_localRoot, "pkg", "target", "1.0");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllTextAsync(localPath, "content");

        var result = await CreateProvisioner().EnsureAsync(CreateDescriptor("file.txt", expectedHash), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(localPath, result.LocalPath);
        Assert.Equal(expectedHash, result.ContentHash);
    }

    [Fact]
    public async Task EnsureAsync_ExistingLocalMismatchingHash_RenamesCorruptAndCopies()
    {
        var sourcePath = WriteSourceFile("file.txt", "right");
        var expectedHash = await HashAsync(sourcePath);
        var localPath = Path.Join(_localRoot, "pkg", "target", "1.0");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllTextAsync(localPath, "wrong");

        var result = await CreateProvisioner().EnsureAsync(CreateDescriptor("file.txt", expectedHash), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("right", await File.ReadAllTextAsync(localPath));
        var corruptPath = Directory.GetFiles(Path.GetDirectoryName(localPath)!, "*.corrupt-*").Single();
        Assert.Equal("wrong", await File.ReadAllTextAsync(corruptPath));
    }

    [Fact]
    public async Task EnsureAsync_SourceMissing_ReturnsFailed()
    {
        var result = await CreateProvisioner().EnsureAsync(CreateDescriptor("missing.txt", "abcd"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ArtifactProvisioningState.Failed, result.State);
        Assert.Contains("source path does not exist", result.ErrorMessage);
    }

    [Fact]
    public async Task EnsureAsync_StagedHashMismatch_ReturnsFailedAndDoesNotCreateFinalPath()
    {
        WriteSourceFile("file.txt", "abc");
        var expectedHash = await HashAsync(WriteSourceFile("other.txt", "xyz"));
        var localPath = Path.Join(_localRoot, "pkg", "target", "1.0");

        var result = await CreateProvisioner().EnsureAsync(CreateDescriptor("file.txt", expectedHash), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ArtifactProvisioningState.HashMismatch, result.State);
        Assert.False(File.Exists(localPath));
    }

    [Fact]
    public async Task EnsureAsync_DirectoryArtifact_RoundTrip()
    {
        var sourceDir = Path.Join(_centralRoot, "dir");
        Directory.CreateDirectory(Path.Join(sourceDir, "sub"));
        await File.WriteAllTextAsync(Path.Join(sourceDir, "a.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Join(sourceDir, "sub", "b.txt"), "beta");
        var expectedHash = await HashAsync(sourceDir);

        var result = await CreateProvisioner().EnsureAsync(CreateDescriptor("dir", expectedHash), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(Directory.Exists(result.LocalPath));
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Join(result.LocalPath, "a.txt")));
        Assert.Equal("beta", await File.ReadAllTextAsync(Path.Join(result.LocalPath, "sub", "b.txt")));
    }

    [Fact]
    public async Task EnsureAsync_FileArtifact_RoundTrip()
    {
        var sourcePath = WriteSourceFile("file.txt", "file-content");
        var expectedHash = await HashAsync(sourcePath);

        var result = await CreateProvisioner().EnsureAsync(CreateDescriptor("file.txt", expectedHash), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(result.LocalPath));
        Assert.Equal("file-content", await File.ReadAllTextAsync(result.LocalPath));
    }

    [Fact]
    public async Task EnsureAsync_DesiredLocalPathEscapesRoot_ThrowsInvalidOperationException()
    {
        var descriptor = CreateDescriptor("file.txt", null, desiredLocalPath: "../escape");

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvisioner().EnsureAsync(descriptor, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAsync_CorruptRenamePathIsUnique()
    {
        var sourcePath = WriteSourceFile("file.txt", "right");
        var expectedHash = await HashAsync(sourcePath);
        var localPath = Path.Join(_localRoot, "pkg", "target", "1.0");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllTextAsync(localPath, "wrong1");

        await CreateProvisioner().EnsureAsync(CreateDescriptor("file.txt", expectedHash), CancellationToken.None);

        await File.WriteAllTextAsync(localPath, "wrong2");
        await Task.Delay(1100); // Ensure the corrupt timestamp differs.
        await CreateProvisioner().EnsureAsync(CreateDescriptor("file.txt", expectedHash), CancellationToken.None);

        var corruptPaths = Directory.GetFiles(Path.GetDirectoryName(localPath)!, "*.corrupt-*");
        Assert.Equal(2, corruptPaths.Length);
        Assert.Equal(2, corruptPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task EnsureAsync_NoExpectedHash_AcceptsAnyExistingFile()
    {
        var localPath = Path.Join(_localRoot, "pkg", "target", "1.0");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllTextAsync(localPath, "anything");
        var localHash = await HashAsync(localPath);

        var result = await CreateProvisioner().EnsureAsync(CreateDescriptor("file.txt", null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(localHash, result.ContentHash);
    }

    [Fact]
    public async Task EnsureAsync_NoExpectedHashAndLocalMissing_CopiesSource()
    {
        var sourcePath = WriteSourceFile("file.txt", "source-content");
        var sourceHash = await HashAsync(sourcePath);

        var result = await CreateProvisioner().EnsureAsync(CreateDescriptor("file.txt", null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(sourceHash, result.ContentHash);
        Assert.Equal("source-content", await File.ReadAllTextAsync(result.LocalPath));
    }

    private ArtifactProvisioner CreateProvisioner()
    {
        var settings = new HostAgentSettings
        {
            CentralArtifactRoot = _centralRoot,
            LocalArtifactCacheRoot = _localRoot
        };
        return new ArtifactProvisioner(new TestOptionsMonitor<HostAgentSettings>(settings), NullLogger<ArtifactProvisioner>.Instance);
    }

    private ArtifactDescriptor CreateDescriptor(string relativePath, string? sha256, string? desiredLocalPath = null)
    {
        return new ArtifactDescriptor
        {
            HostId = Guid.NewGuid(),
            ArtifactId = 1,
            Version = "1.0",
            PackageType = "pkg",
            TargetName = "target",
            RelativePath = relativePath,
            Sha256 = sha256,
            RequirementKey = "req",
            DesiredLocalPath = desiredLocalPath
        };
    }

    private string WriteSourceFile(string relativePath, string content)
    {
        var path = Path.Join(_centralRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static Task<string> HashAsync(string path)
        => ArtifactHash.ComputeSha256Async(path, CancellationToken.None);

    private sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions> where TOptions : class
    {
        public TestOptionsMonitor(TOptions currentValue)
        {
            CurrentValue = currentValue;
        }

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<TOptions, string?> listener) => new NullChangeToken();

        private sealed class NullChangeToken : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
