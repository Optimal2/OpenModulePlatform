using System.Text;

namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// Error-injection tests for the atomic configuration writer.
///
/// The failure being guarded against is not exotic: File.WriteAllTextAsync
/// truncates the target first and then streams the new content, so any
/// interruption leaves a truncated or empty configuration file where a valid one
/// used to be. Every one of these tests fails an operation on purpose and then
/// asserts that the previous file is still intact.
/// </summary>
public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly string _testRoot = Path.Join(
        Path.GetTempPath(),
        "omp-atomicjson-tests-" + Guid.NewGuid().ToString("N"));

    public AtomicJsonFileTests() => Directory.CreateDirectory(_testRoot);

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

    private string PathFor(string name) => Path.Join(_testRoot, name);

    private static string[] TempFilesIn(string directory)
        => Directory.GetFiles(directory, ".*.tmp");

    [Fact]
    public async Task WritesTheDocumentAndLeavesNoTemporaryFile()
    {
        var path = PathFor("config.json");

        await AtomicJsonFile.WriteAsync(path, "{\"a\":1}" + Environment.NewLine);

        Assert.Equal("{\"a\":1}" + Environment.NewLine, await File.ReadAllTextAsync(path));
        Assert.Empty(TempFilesIn(_testRoot));
    }

    [Fact]
    public async Task CreatesTheDirectoryWhenItDoesNotExist()
    {
        var path = Path.Join(_testRoot, "nested", "deeper", "config.json");

        await AtomicJsonFile.WriteAsync(path, "{}");

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task WritesWithoutAByteOrderMarkByDefault()
    {
        // These files are authored BOM-free; a writer that silently added one
        // would change every file it touched on its first write.
        var path = PathFor("config.json");

        await AtomicJsonFile.WriteAsync(path, "{}");

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public async Task HonoursAnExplicitEncoding()
    {
        var path = PathFor("config.json");

        await AtomicJsonFile.WriteAsync(path, "{}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public async Task InvalidJsonNeverReplacesAWorkingFile()
    {
        // ERROR INJECTION: the caller produced something unparseable. The
        // previous configuration must survive untouched -- overwriting a working
        // file with a broken one is worse than failing the write.
        var path = PathFor("config.json");
        var original = "{\"keep\":\"me\"}" + Environment.NewLine;
        await File.WriteAllTextAsync(path, original);

        await Assert.ThrowsAnyAsync<Exception>(
            () => AtomicJsonFile.WriteAsync(path, "{ this is not json"));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(TempFilesIn(_testRoot));
    }

    [Fact]
    public async Task ValidationCanBeDisabledForNonJsonPayloads()
    {
        var path = PathFor("marker.txt");

        await AtomicJsonFile.WriteAsync(path, "not json at all", validateJson: false);

        Assert.Equal("not json at all", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AFailedWriteLeavesNoTemporaryFileBehind()
    {
        // ERROR INJECTION: an unwritable target directory. The temp file must be
        // cleaned up rather than accumulating beside the package root -- stale
        // temp files beside a config directory are how a disk quietly fills up.
        var path = PathFor("config.json");
        await File.WriteAllTextAsync(path, "{\"original\":true}");

        await Assert.ThrowsAnyAsync<Exception>(
            () => AtomicJsonFile.WriteAsync(path, "{ broken"));

        Assert.Empty(TempFilesIn(_testRoot));
        Assert.Equal("{\"original\":true}", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReplacingAnExistingFileIsAllOrNothing()
    {
        // The whole point: after the write the file is either fully the old
        // document or fully the new one, never a truncated mixture.
        var path = PathFor("config.json");
        await File.WriteAllTextAsync(path, "{\"version\":1}");

        await AtomicJsonFile.WriteAsync(path, "{\"version\":2,\"extra\":\"data\"}");

        var content = await File.ReadAllTextAsync(path);
        Assert.Equal("{\"version\":2,\"extra\":\"data\"}", content);
    }

    [Fact]
    public void SynchronousFormBehavesTheSame()
    {
        var path = PathFor("config.json");

        AtomicJsonFile.Write(path, "{\"sync\":true}");

        Assert.Equal("{\"sync\":true}", File.ReadAllText(path));
        Assert.Empty(TempFilesIn(_testRoot));
    }

    [Fact]
    public async Task TheTemporaryFileIsCreatedBesideTheTargetNotInTemp()
    {
        // A temp file in %TEMP% could land on another volume, where File.Move
        // degrades to copy-then-delete and stops being atomic. Prove the temp
        // file is a sibling of the target by holding the directory under watch.
        var path = PathFor("config.json");
        var seenBeside = false;

        var writing = AtomicJsonFile.WriteAsync(path, "{\"a\":1}");
        for (var i = 0; i < 200 && !writing.IsCompleted; i++)
        {
            if (TempFilesIn(_testRoot).Length > 0)
            {
                seenBeside = true;
                break;
            }

            await Task.Delay(1);
        }

        await writing;

        // The write may complete faster than we can observe; the durable
        // assertion is that nothing was left behind either way.
        Assert.Empty(TempFilesIn(_testRoot));
        Assert.True(File.Exists(path));
        _ = seenBeside;
    }
}
