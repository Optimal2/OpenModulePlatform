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

    [Fact]
    public async Task ReadConfigOverlayAsync_WithMergeMode_ParsesAndNormalizesPerFile()
    {
        var path = WriteOverlayJson("""
        {
          "overlayKey": "test-overlay",
          "overlayVersion": "1.0.0",
          "hostKey": "test-host",
          "configurationFiles": [
            { "relativePath": "appsettings.json", "fileContent": "{ \"a\": 1 }", "mergeMode": "Merge" },
            { "relativePath": "site.config.js", "fileContent": "x = 1;", "mergeMode": "replace" },
            { "relativePath": "extra.json", "fileContent": "{ \"b\": 2 }" }
          ]
        }
        """);

        var overlay = await new ConfigOverlayPackageReader().ReadConfigOverlayAsync(path, "test.json", CancellationToken.None);

        Assert.Equal(3, overlay.ConfigurationFiles.Count);
        Assert.Equal("merge", overlay.ConfigurationFiles[0].MergeMode);
        Assert.Equal("replace", overlay.ConfigurationFiles[1].MergeMode);
        Assert.Null(overlay.ConfigurationFiles[2].MergeMode);
        // The stored document JSON round-trips the field for later re-export.
        Assert.Contains("mergeMode", overlay.OverlayJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadConfigOverlayAsync_WithInvalidMergeMode_Throws()
    {
        var path = WriteOverlayJson("""
        {
          "overlayKey": "test-overlay",
          "overlayVersion": "1.0.0",
          "hostKey": "test-host",
          "configurationFiles": [
            { "relativePath": "appsettings.json", "fileContent": "{ \"a\": 1 }", "mergeMode": "append" }
          ]
        }
        """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ConfigOverlayPackageReader().ReadConfigOverlayAsync(path, "test.json", CancellationToken.None));
        Assert.Contains("mergeMode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadConfigOverlayAsync_WithAllHostsScope_NormalizesSentinelAndScope()
    {
        var path = WriteOverlayJson("""
        {
          "overlayKey": "test-overlay",
          "overlayVersion": "1.0.0",
          "hostScope": "all-hosts",
          "configurationFiles": [
            { "relativePath": "appsettings.json", "fileContent": "{ \"a\": 1 }" }
          ]
        }
        """);

        var overlay = await new ConfigOverlayPackageReader().ReadConfigOverlayAsync(path, "test.json", CancellationToken.None);

        Assert.Equal(ConfigOverlayHostScope.AllHosts, overlay.HostScope);
        Assert.Equal("*", overlay.HostKey);
        Assert.Contains("\"hostScope\": \"AllHosts\"", overlay.OverlayJson, StringComparison.Ordinal);
        Assert.Contains("\"hostKey\": \"*\"", overlay.OverlayJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadConfigOverlayAsync_WithoutScope_DefaultsToHost()
    {
        var path = WriteOverlayJson("""
        { "overlayKey": "test-overlay", "overlayVersion": "1.0.0", "hostKey": "test-host" }
        """);

        var overlay = await new ConfigOverlayPackageReader().ReadConfigOverlayAsync(path, "test.json", CancellationToken.None);

        Assert.Equal(ConfigOverlayHostScope.Host, overlay.HostScope);
        Assert.Equal("test-host", overlay.HostKey);
    }

    [Fact]
    public async Task ReadConfigOverlayAsync_WithWildcardHostKeyAndNoScope_Throws()
    {
        var path = WriteOverlayJson("""
        { "overlayKey": "test-overlay", "overlayVersion": "1.0.0", "hostKey": "*" }
        """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ConfigOverlayPackageReader().ReadConfigOverlayAsync(path, "test.json", CancellationToken.None));
        Assert.Contains("hostScope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadConfigOverlayAsync_WithUnknownScope_Throws()
    {
        var path = WriteOverlayJson("""
        { "overlayKey": "test-overlay", "overlayVersion": "1.0.0", "hostKey": "test-host", "hostScope": "Everywhere" }
        """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ConfigOverlayPackageReader().ReadConfigOverlayAsync(path, "test.json", CancellationToken.None));
        Assert.Contains("hostScope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadConfigOverlayAsync_WithAllHostsAndRealHostKey_Throws()
    {
        var path = WriteOverlayJson("""
        { "overlayKey": "test-overlay", "overlayVersion": "1.0.0", "hostScope": "AllHosts", "hostKey": "test-host" }
        """);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ConfigOverlayPackageReader().ReadConfigOverlayAsync(path, "test.json", CancellationToken.None));
    }

    [Fact]
    public void ExportJson_LegacyMetadataOnly_ReconstructsFilesFromRows()
    {
        var json = ConfigOverlayExportJson.Build(
            """{ "overlayKey": "k", "overlayVersion": "1.0.0", "hostKey": "h1" }""",
            "Host",
            "h1",
            [new PortableConfigOverlayConfigurationFile("appsettings.json", "{ \"a\": 1 }", "merge")]);

        Assert.Contains("appsettings.json", json, StringComparison.Ordinal);
        Assert.Contains("merge", json, StringComparison.Ordinal);
        Assert.Contains("\"hostScope\": \"Host\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportJson_WithExistingFiles_DoesNotReplaceThem()
    {
        var json = ConfigOverlayExportJson.Build(
            """{ "overlayKey": "k", "overlayVersion": "1.0.0", "hostKey": "*", "hostScope": "AllHosts", "configurationFiles": [{ "relativePath": "a.json", "fileContent": "x" }] }""",
            "AllHosts",
            "*",
            [new PortableConfigOverlayConfigurationFile("b.json", "y")]);

        Assert.Contains("a.json", json, StringComparison.Ordinal);
        Assert.DoesNotContain("b.json", json, StringComparison.Ordinal);
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
