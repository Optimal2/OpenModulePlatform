using Microsoft.Extensions.Logging;
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
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
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
    public async Task EnsureAsync_RepeatedCorruption_KeepsAtMostOneQuarantineCopy()
    {
        // Earlier quarantined copies are removed before a new one is made, so
        // repeated mismatches cannot pile up full copies and fill the cache
        // volume (R3-D8); at most one .corrupt-* survives.
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
        Assert.Single(corruptPaths);
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

    // The three tests below cover an artifact whose omp.Artifacts.Sha256 was registered
    // before the directory hash ordered relative paths with the ordinal comparer. The
    // registered value comes from PreOrdinalDirectoryHash, an independent copy of the old
    // definition, and the tree is one for which the two orderings really differ.

    [Fact]
    public async Task EnsureAsync_LocalCopyRegisteredWithTheOldPathOrdering_IsAcceptedAndNotQuarantined()
    {
        var localPath = Path.Join(_localRoot, "pkg", "target", "1.0");
        PreOrdinalDirectoryHash.WriteOrderSensitiveTree(localPath);
        var registeredHash = PreOrdinalDirectoryHash.Compute(localPath);
        Assert.NotEqual(registeredHash, await HashAsync(localPath));
        var logger = new RecordingLogger();
        var provisioner = CreateProvisioner(logger);

        var first = await provisioner.EnsureAsync(CreateDescriptor("dir", registeredHash), CancellationToken.None);
        var second = await provisioner.EnsureAsync(CreateDescriptor("dir", registeredHash), CancellationToken.None);

        Assert.True(first.IsSuccess, first.ErrorMessage);
        Assert.True(second.IsSuccess, second.ErrorMessage);
        Assert.Equal(localPath, first.LocalPath);
        // Downstream comparisons (HostArtifactStates.ContentSha256 against the deployed
        // hash) must keep seeing the registered value, or everything redeploys once.
        Assert.Equal(registeredHash, first.ContentHash);
        Assert.Equal(registeredHash, second.ContentHash);
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(localPath)!, "*.corrupt-*"));
        Assert.Equal("bravo", await File.ReadAllTextAsync(Path.Join(localPath, "B.txt")));
        // Visible in operation, but once per artifact and process, not once per cycle.
        var accepted = Assert.Single(logger.Entries, entry => entry.Message.Contains("legacy", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LogLevel.Information, accepted.Level);
        Assert.Contains("ArtifactId=1", accepted.Message, StringComparison.Ordinal);
        Assert.Contains("Version=1.0", accepted.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task EnsureAsync_DownloadRegisteredWithTheOldPathOrdering_IsAccepted()
    {
        var sourceDir = Path.Join(_centralRoot, "dir");
        PreOrdinalDirectoryHash.WriteOrderSensitiveTree(sourceDir);
        var registeredHash = PreOrdinalDirectoryHash.Compute(sourceDir);
        Assert.NotEqual(registeredHash, await HashAsync(sourceDir));
        var provisioner = CreateProvisioner();

        var downloaded = await provisioner.EnsureAsync(CreateDescriptor("dir", registeredHash), CancellationToken.None);
        var verifiedFromCache = await provisioner.EnsureAsync(CreateDescriptor("dir", registeredHash), CancellationToken.None);

        Assert.True(downloaded.IsSuccess, downloaded.ErrorMessage);
        Assert.Equal(registeredHash, downloaded.ContentHash);
        Assert.Equal("alpha-y", await File.ReadAllTextAsync(Path.Join(downloaded.LocalPath, "alpha", "y")));
        Assert.True(verifiedFromCache.IsSuccess, verifiedFromCache.ErrorMessage);
        Assert.Equal(registeredHash, verifiedFromCache.ContentHash);
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(downloaded.LocalPath)!, "*.corrupt-*"));
    }

    [Fact]
    public async Task EnsureAsync_ChangedContentUnderAHashRegisteredWithTheOldPathOrdering_IsStillRefused()
    {
        var sourceDir = Path.Join(_centralRoot, "dir");
        PreOrdinalDirectoryHash.WriteOrderSensitiveTree(sourceDir);
        var registeredHash = PreOrdinalDirectoryHash.Compute(sourceDir);
        var localPath = Path.Join(_localRoot, "pkg", "target", "1.0");
        PreOrdinalDirectoryHash.WriteOrderSensitiveTree(localPath);
        // Same file set and sizes, different bytes, in both the cached copy and the source.
        await File.WriteAllTextAsync(Path.Join(localPath, "a.txt"), "ALPHA");
        await File.WriteAllTextAsync(Path.Join(sourceDir, "B.txt"), "BRAVO");

        var result = await CreateProvisioner().EnsureAsync(CreateDescriptor("dir", registeredHash), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ArtifactProvisioningState.HashMismatch, result.State);
        Assert.Contains("Downloaded artifact hash mismatch", result.ErrorMessage, StringComparison.Ordinal);
        Assert.False(Directory.Exists(localPath));
        var quarantined = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(localPath)!, "*.corrupt-*"));
        Assert.Equal("ALPHA", await File.ReadAllTextAsync(Path.Join(quarantined, "a.txt")));
    }

    private ArtifactProvisioner CreateProvisioner(ILogger<ArtifactProvisioner>? logger = null)
    {
        var settings = new HostAgentSettings
        {
            CentralArtifactRoot = _centralRoot,
            LocalArtifactCacheRoot = _localRoot
        };
        return new ArtifactProvisioner(new TestOptionsMonitor<HostAgentSettings>(settings), logger ?? NullLogger<ArtifactProvisioner>.Instance);
    }

    private sealed class RecordingLogger : ILogger<ArtifactProvisioner>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
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
