using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installation.Tests;

/// <summary>
/// An antivirus scanner with real-time protection opens freshly extracted files
/// without FileShare.Delete. While such a handle is open, Windows refuses to
/// rename the parent directory and Directory.Move fails with "Access to the
/// path ... is denied" (an IOException carrying ERROR_ACCESS_DENIED). Artifact
/// installation moved extracted packages into the artifact store with a bare
/// Directory.Move and no retry, so one scanned file failed the install.
///
/// These tests hold such a handle themselves to pin the behaviour:
/// the access-denied rename is retried, and when it never clears, the artifact
/// path copies the tree instead of moving it.
/// </summary>
[Collection(InstallOutputTestCollection.Name)]
public sealed class MoveDirectoryWithRetryTests : IDisposable
{
    private static readonly TimeSpan NoDelay = TimeSpan.Zero;

    private readonly string _testRoot = Path.Join(
        Path.GetTempPath(),
        "omp-movedir-tests-" + Guid.NewGuid().ToString("N"));

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

    private string NewSource()
    {
        var source = Path.Join(_testRoot, "staging", "artifact-content");
        Directory.CreateDirectory(Path.Join(source, "sub"));
        File.WriteAllText(Path.Join(source, "app.dll"), "binary");
        File.WriteAllText(Path.Join(source, "sub", "config.json"), "{}");
        return source;
    }

    /// <summary>Opens a file the way a real-time scanner does: readable by others, not deletable.</summary>
    private static FileStream HoldLikeAScanner(string path, FileShare share = FileShare.Read)
        => new(path, FileMode.Open, FileAccess.Read, share);

    [Fact]
    public void UnlockedDirectoryIsMoved()
    {
        var source = NewSource();
        var destination = Path.Join(_testRoot, "store", "artifact");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        InstallationEngine.MoveDirectoryWithRetry(source, destination, copyAsLastResort: true, retryDelay: NoDelay);

        Assert.False(Directory.Exists(source));
        Assert.Equal("binary", File.ReadAllText(Path.Join(destination, "app.dll")));
        Assert.Equal("{}", File.ReadAllText(Path.Join(destination, "sub", "config.json")));
    }

    [Fact]
    public void ScannerHandleMakesTheRenameFailWithAccessDenied()
    {
        // Guards the premise of the fix: an open handle without FileShare.Delete
        // anywhere below the directory blocks the rename with access denied.
        var source = NewSource();
        var destination = Path.Join(_testRoot, "store", "artifact");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var handle = HoldLikeAScanner(Path.Join(source, "sub", "config.json"));

        var ex = Assert.ThrowsAny<Exception>(() => Directory.Move(source, destination));
        Assert.True(ex is IOException or UnauthorizedAccessException, ex.ToString());
        Assert.Equal(unchecked((int)0x80070005), ex.HResult);
    }

    [Fact]
    public void PersistentScannerHandleFallsBackToCopyingWhenAllowed()
    {
        var source = NewSource();
        var destination = Path.Join(_testRoot, "store", "artifact");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var handle = HoldLikeAScanner(Path.Join(source, "app.dll"));

        InstallationEngine.MoveDirectoryWithRetry(source, destination, copyAsLastResort: true, retryDelay: NoDelay);

        Assert.Equal("binary", File.ReadAllText(Path.Join(destination, "app.dll")));
        Assert.Equal("{}", File.ReadAllText(Path.Join(destination, "sub", "config.json")));
        // The source is left for the caller's staging cleanup.
        Assert.True(File.Exists(Path.Join(source, "app.dll")));
    }

    [Fact]
    public void PersistentScannerHandleStillFailsWithoutCopyFallback()
    {
        // The refresh flow calls MoveDirectoryWithRetry without the copy fallback;
        // it must keep failing (after retries) rather than silently copying.
        var source = NewSource();
        var destination = Path.Join(_testRoot, "store", "artifact");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var handle = HoldLikeAScanner(Path.Join(source, "app.dll"));

        var ex = Assert.ThrowsAny<Exception>(
            () => InstallationEngine.MoveDirectoryWithRetry(source, destination, retryDelay: NoDelay));
        Assert.Equal(unchecked((int)0x80070005), ex.HResult);

        Assert.False(Directory.Exists(destination));
        Assert.True(File.Exists(Path.Join(source, "app.dll")));
    }

    [Fact]
    public void RenameThatClearsDuringRetriesIsMovedNotCopied()
    {
        var source = NewSource();
        var destination = Path.Join(_testRoot, "store", "artifact");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // The handle is released long before the second attempt (100 ms vs 1 s), and the
        // test only fails if the timer is late by more than the whole four-second retry window.
        var handle = HoldLikeAScanner(Path.Join(source, "app.dll"));
        using var release = new Timer(_ => handle.Dispose(), null, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);

        InstallationEngine.MoveDirectoryWithRetry(
            source,
            destination,
            copyAsLastResort: true,
            retryDelay: TimeSpan.FromSeconds(1));

        Assert.False(Directory.Exists(source));
        Assert.Equal("binary", File.ReadAllText(Path.Join(destination, "app.dll")));
    }

    [Fact]
    public void FailedCopyFallbackRemovesItsPartialDestination()
    {
        // A half-copied artifact would be accepted as "already exists" by a later
        // add-missing-only pass, so a copy that cannot finish removes what it wrote.
        // Here the copy fails on an exclusively locked SOURCE file and the partial
        // destination is deletable. When the destination itself cannot be deleted,
        // the engine can only report it (an error line names the directory); that
        // case is not reproducible here because the destination does not exist
        // before the copy starts.
        var source = NewSource();
        var destination = Path.Join(_testRoot, "store", "artifact");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var handle = HoldLikeAScanner(Path.Join(source, "sub", "config.json"), FileShare.None);

        Assert.ThrowsAny<IOException>(
            () => InstallationEngine.MoveDirectoryWithRetry(source, destination, copyAsLastResort: true, retryDelay: NoDelay));

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void CopyFallbackNeverMergesIntoAnExistingDestination()
    {
        var source = NewSource();
        var destination = Path.Join(_testRoot, "store", "artifact");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Join(destination, "app.dll"), "existing");

        Assert.ThrowsAny<IOException>(
            () => InstallationEngine.MoveDirectoryWithRetry(source, destination, copyAsLastResort: true, retryDelay: NoDelay));

        Assert.Equal("existing", File.ReadAllText(Path.Join(destination, "app.dll")));
        Assert.False(File.Exists(Path.Join(destination, "sub", "config.json")));
    }

    [Fact]
    public void DeleteWithRetryWaitsForAScannerHandleToClear()
    {
        var target = NewSource();
        var handle = HoldLikeAScanner(Path.Join(target, "app.dll"));
        using var release = new Timer(_ => handle.Dispose(), null, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);

        InstallationEngine.DeleteFileOrDirectoryWithRetry(target, retryDelay: TimeSpan.FromSeconds(1));

        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void DeleteWithRetryGivesUpOnAPersistentHandle()
    {
        var target = NewSource();
        using var handle = HoldLikeAScanner(Path.Join(target, "app.dll"));

        var ex = Assert.ThrowsAny<Exception>(
            () => InstallationEngine.DeleteFileOrDirectoryWithRetry(target, retryDelay: NoDelay));

        Assert.True(ex is IOException or UnauthorizedAccessException, ex.ToString());
        Assert.True(File.Exists(Path.Join(target, "app.dll")));
    }
}
