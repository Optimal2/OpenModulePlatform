using System.Text;

namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// The package writers must never replace an existing file, also not when
/// another writer creates the same name while they run, and a refusal must
/// arrive as <see cref="OutputFileExistsException"/> with an explanation
/// instead of a bare <see cref="IOException"/>.
/// </summary>
public sealed class NoOverwriteFileTests : IDisposable
{
    private readonly string _testRoot = Path.Join(
        Path.GetTempPath(),
        "omp-nooverwrite-tests-" + Guid.NewGuid().ToString("N"));

    public NoOverwriteFileTests() => Directory.CreateDirectory(_testRoot);

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

    [Fact]
    public void WriteCreatesTheFileAndLeavesNoTemporaryFile()
    {
        var path = Path.Join(_testRoot, "out", "package.zip");

        NoOverwriteFile.Write(path, stream => stream.Write(Encoding.UTF8.GetBytes("first")));

        Assert.Equal("first", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void WriteRefusesAnExistingFileWithoutTouchingIt()
    {
        var path = Path.Join(_testRoot, "package.zip");
        File.WriteAllText(path, "existing");
        var called = false;

        var ex = Assert.Throws<OutputFileExistsException>(
            () => NoOverwriteFile.Write(path, _ => called = true));

        Assert.False(called);
        Assert.Equal(Path.GetFullPath(path), ex.Path);
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
        Assert.Equal("existing", File.ReadAllText(path));
    }

    [Fact]
    public void ASecondWriterDuringTheFirstWriteIsRefused()
    {
        // The second writer starts while the first is still producing content:
        // the name is already claimed, so the second fails before writing and
        // the first finishes with its own content.
        var path = Path.Join(_testRoot, "package.zip");
        OutputFileExistsException? second = null;

        NoOverwriteFile.Write(path, stream =>
        {
            second = Assert.Throws<OutputFileExistsException>(
                () => NoOverwriteFile.Write(path, inner => inner.Write(Encoding.UTF8.GetBytes("second"))));
            stream.Write(Encoding.UTF8.GetBytes("first"));
        });

        Assert.NotNull(second);
        Assert.Equal(Path.GetFullPath(path), second.Path);
        Assert.Equal("first", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_testRoot));
    }

    [Fact]
    public async Task ConcurrentWritersProduceExactlyOneFile()
    {
        var path = Path.Join(_testRoot, "package.zip");
        const int writers = 8;
        using var start = new Barrier(writers);
        var writes = Enumerable.Range(0, writers)
            .Select(index => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    NoOverwriteFile.Write(path, stream => stream.Write(Encoding.UTF8.GetBytes("writer-" + index)));
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        await Task.WhenAll(writes.Select(static write => write.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default)));

        var winners = Enumerable.Range(0, writers).Where(index => writes[index].Exception is null).ToArray();
        var winner = Assert.Single(winners);
        Assert.All(
            writes.Where(static write => write.Exception is not null),
            write => Assert.IsType<OutputFileExistsException>(write.Exception!.GetBaseException()));
        Assert.Equal("writer-" + winner, File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_testRoot));
    }

    [Fact]
    public void WriteFailureLeavesNeitherTargetNorTemporaryFile()
    {
        var path = Path.Join(_testRoot, "package.zip");

        Assert.Throws<InvalidOperationException>(() => NoOverwriteFile.Write(path, stream =>
        {
            stream.Write(Encoding.UTF8.GetBytes("partial"));
            throw new InvalidOperationException("build failed");
        }));

        Assert.Empty(Directory.GetFiles(_testRoot));
    }

    [Fact]
    public void CopyRefusesAnExistingDestinationWithoutTouchingIt()
    {
        var source = Path.Join(_testRoot, "source.zip");
        var destination = Path.Join(_testRoot, "staged.zip");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "already staged");

        var ex = Assert.Throws<OutputFileExistsException>(() => NoOverwriteFile.Copy(source, destination));

        Assert.Equal(Path.GetFullPath(destination), ex.Path);
        Assert.Equal("already staged", File.ReadAllText(destination));
    }

    [Fact]
    public void CopyCreatesANewDestination()
    {
        var source = Path.Join(_testRoot, "source.zip");
        var destination = Path.Join(_testRoot, "staged.zip");
        File.WriteAllText(source, "new");

        NoOverwriteFile.Copy(source, destination);

        Assert.Equal("new", File.ReadAllText(destination));
    }
}
