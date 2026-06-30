using System.Text.Json;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ConfigOverlayPackageReaderTests : IDisposable
{
    private readonly string _tempRoot;

    public ConfigOverlayPackageReaderTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), $"omp-configoverlay-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup for temporary test files.
        }
    }

    [Fact]
    public async Task ReadConfigOverlayAsync_WithInlineSqlScripts_SetsSqlScriptCount()
    {
        var path = WriteOverlayJson("""
        {
          "overlayKey": "test-overlay",
          "overlayVersion": "1.0.0",
          "hostKey": "test-host",
          "sqlScripts": [
            { "inlineSql": "SELECT 1;" },
            { "inlineSql": "SELECT 2;" }
          ]
        }
        """);

        var overlay = await new ConfigOverlayPackageReader().ReadConfigOverlayAsync(path, "test.json", CancellationToken.None);

        Assert.Equal(2, overlay.SqlScriptCount);
        Assert.Contains("SELECT 1", overlay.OverlayJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadConfigOverlayAsync_WithExternalSqlScripts_SetsSqlScriptCount()
    {
        File.WriteAllText(GetTempFilePath("script.sql"), "SELECT 3;");
        var path = WriteOverlayJson("""
        {
          "overlayKey": "test-overlay",
          "overlayVersion": "1.0.0",
          "hostKey": "test-host",
          "sqlScripts": [
            { "source": "script.sql" }
          ]
        }
        """);

        var overlay = await new ConfigOverlayPackageReader().ReadConfigOverlayAsync(path, "test.json", CancellationToken.None);

        Assert.Equal(1, overlay.SqlScriptCount);
        Assert.Contains("SELECT 3", overlay.OverlayJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadConfigOverlayAsync_WithoutSqlScripts_HasZeroSqlScriptCount()
    {
        var path = WriteOverlayJson("""
        {
          "overlayKey": "test-overlay",
          "overlayVersion": "1.0.0",
          "hostKey": "test-host"
        }
        """);

        var overlay = await new ConfigOverlayPackageReader().ReadConfigOverlayAsync(path, "test.json", CancellationToken.None);

        Assert.Equal(0, overlay.SqlScriptCount);
    }

    [Fact]
    public async Task ReadConfigOverlayAsync_WithEmptySqlScriptsArray_HasZeroSqlScriptCount()
    {
        var path = WriteOverlayJson("""
        {
          "overlayKey": "test-overlay",
          "overlayVersion": "1.0.0",
          "hostKey": "test-host",
          "sqlScripts": []
        }
        """);

        var overlay = await new ConfigOverlayPackageReader().ReadConfigOverlayAsync(path, "test.json", CancellationToken.None);

        Assert.Equal(0, overlay.SqlScriptCount);
    }

    private string WriteOverlayJson(string json)
    {
        var path = GetTempFilePath($"overlay-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string GetTempFilePath(string fileName)
    {
        var fullPath = Path.GetFullPath(Path.Join(_tempRoot, fileName));
        var normalizedRoot = Path.GetFullPath(_tempRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Temporary test file path escaped the test root.");
        }

        return fullPath;
    }
}
