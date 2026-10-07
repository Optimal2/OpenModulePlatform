using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installation.Tests;

public sealed class BootstrapConfigLoaderTests : IDisposable
{
    private readonly string _testRoot = Path.Join(
        Path.GetTempPath(),
        "OpenModulePlatform.Installation.Tests",
        Guid.NewGuid().ToString("N"));

    public BootstrapConfigLoaderTests() => Directory.CreateDirectory(_testRoot);

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ReadsBootstrapJsonIntoConfigurationModel()
    {
        var path = Path.Join(_testRoot, "bootstrap.json");
        await File.WriteAllTextAsync(path, """
            {
              // comments and trailing commas are allowed in host profiles
              "profile": {
                "displayName": "Test host",
                "machineNames": [ "TESTHOST01", ],
              },
              "sql": {
                "server": "sql01",
                "database": "OpenModulePlatform",
                "createDatabase": true
              },
              "hostAgent": {
                "serviceName": "OMP.HostAgent.Test",
                "hostKey": "testhost01"
              },
              "futureField": { "kept": true }
            }
            """);

        var config = await BootstrapConfigLoader.LoadAsync(path);

        Assert.Equal("Test host", config.Profile.DisplayName);
        Assert.Equal(["TESTHOST01"], config.Profile.MachineNames);
        Assert.Equal("sql01", config.Sql.Server);
        Assert.True(config.Sql.CreateDatabase);
        Assert.Equal("OMP.HostAgent.Test", config.HostAgent.ServiceName);
        Assert.Equal("testhost01", config.HostAgent.HostKey);
        // Unknown properties survive the typed round-trip (schema evolution guard).
        Assert.True(config.ExtensionData.ContainsKey("futureField"));
    }

    [Fact]
    public async Task LoadAsync_AppliesDefaultsForMissingSections()
    {
        var path = Path.Join(_testRoot, "bootstrap.json");
        await File.WriteAllTextAsync(path, "{ }");

        var config = await BootstrapConfigLoader.LoadAsync(path);

        Assert.True(config.Sql.Enabled);
        Assert.Equal("localhost", config.Sql.Server);
        Assert.Equal("OpenModulePlatform", config.Sql.Database);
        Assert.True(config.HostAgent.Enabled);
        Assert.Equal("OMP.HostAgent", config.HostAgent.ServiceName);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => BootstrapConfigLoader.LoadAsync(Path.Join(_testRoot, "does-not-exist.json")));
    }

    [Fact]
    public void SerializerOptions_IsSharedWithTheEngine()
    {
        Assert.Same(InstallationEngine.JsonOptions, BootstrapConfigLoader.SerializerOptions);
    }
}
