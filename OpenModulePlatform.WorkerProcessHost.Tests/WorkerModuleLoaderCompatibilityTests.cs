using System.Text.Json;
using OpenModulePlatform.WorkerProcessHost.Plugins;
using OpenModulePlatform.WorkerProcessHost.TestPlugin;

namespace OpenModulePlatform.WorkerProcessHost.Tests;

public sealed class WorkerModuleLoaderCompatibilityTests : IDisposable
{
    private const string HostComponentKey = "omp-workerprocesshost";

    private readonly string _artifactRoot = Path.Join(
        Path.GetTempPath(),
        "OpenModulePlatform",
        "WorkerCompatibilityTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadFactory_RejectsPluginThatRequiresNewerWorkerHostBeforeFactoryCreation()
    {
        var pluginPath = StagePlugin("0.3.46");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new WorkerModuleLoader().LoadFactory(
                pluginPath,
                "newer-contract-test-worker",
                _artifactRoot,
                HostComponentKey,
                "0.3.45"));

        Assert.Contains(HostComponentKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains("0.3.46", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0.3.45", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(MissingMethodException), exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Control_WithoutCompatibilityGate_ReachesMissingMethodFailure()
    {
        Directory.CreateDirectory(_artifactRoot);
        var pluginPath = typeof(CompatibilityTestWorkerFactory).Assembly.Location;
        var factory = new WorkerModuleLoader().LoadFactory(pluginPath, "newer-contract-test-worker");
        var module = factory.Create(new EmptyServiceProvider());

        await Assert.ThrowsAsync<MissingMethodException>(() =>
            module.RunAsync(new OpenModulePlatform.Worker.Abstractions.Models.WorkerExecutionContext(), CancellationToken.None));
    }

    [Fact]
    public void LoadFactory_RejectsIncompatiblePluginBeforeOpeningAssemblyFile()
    {
        Directory.CreateDirectory(_artifactRoot);
        WriteManifest("0.3.46");
        var missingPluginPath = Path.Join(_artifactRoot, "newer-contract-plugin.dll");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new WorkerModuleLoader().LoadFactory(
                missingPluginPath,
                "newer-contract-test-worker",
                _artifactRoot,
                HostComponentKey,
                "0.3.45"));

        Assert.Contains(HostComponentKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains("0.3.46", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFactory_AllowsPluginWhenWorkerHostMeetsMinimumVersion()
    {
        var pluginPath = StagePlugin("0.3.45");

        var factory = new WorkerModuleLoader().LoadFactory(
            pluginPath,
            "compatibility-test-worker",
            _artifactRoot,
            HostComponentKey,
            "0.3.45");

        Assert.Equal("compatibility-test-worker", factory.WorkerTypeKey);
    }

    [Fact]
    public void LoadFactory_RejectsManifestWhenOlderManagerOmitsCompatibilityArguments()
    {
        var pluginPath = StagePlugin("0.3.46", stageAssemblyInArtifactRoot: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new WorkerModuleLoader().LoadFactory(
                pluginPath,
                "newer-contract-test-worker",
                pluginArtifactRootPath: null,
                workerHostComponentKey: null,
                workerHostVersion: null));

        Assert.Contains("0.3.46", exception.Message, StringComparison.Ordinal);
        Assert.Contains("<unknown>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadFactory_AllowsLegacyPluginWithoutManifestOrCompatibilityArguments()
    {
        Directory.CreateDirectory(_artifactRoot);
        var pluginPath = typeof(CompatibilityTestWorkerFactory).Assembly.Location;

        var factory = new WorkerModuleLoader().LoadFactory(
            pluginPath,
            "compatibility-test-worker",
            pluginArtifactRootPath: null,
            workerHostComponentKey: null,
            workerHostVersion: null);

        Assert.Equal("compatibility-test-worker", factory.WorkerTypeKey);
    }

    [Fact]
    public void LoadFactory_OlderManagerCannotHidePayloadMarkerBehindNestedAssemblyPath()
    {
        Directory.CreateDirectory(_artifactRoot);
        WriteManifest("0.3.46");
        var nestedRoot = Path.Join(_artifactRoot, "plugins", "current");
        Directory.CreateDirectory(nestedRoot);
        var sourceAssembly = typeof(CompatibilityTestWorkerFactory).Assembly.Location;
        var pluginPath = Path.Join(nestedRoot, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, pluginPath, overwrite: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new WorkerModuleLoader().LoadFactory(
                pluginPath,
                "newer-contract-test-worker",
                pluginArtifactRootPath: null,
                workerHostComponentKey: null,
                workerHostVersion: null));

        Assert.Contains("0.3.46", exception.Message, StringComparison.Ordinal);
        Assert.Contains("<unknown>", exception.Message, StringComparison.Ordinal);
    }

    private string StagePlugin(string minimumWorkerHostVersion, bool stageAssemblyInArtifactRoot = false)
    {
        Directory.CreateDirectory(_artifactRoot);
        var sourceAssembly = typeof(CompatibilityTestWorkerFactory).Assembly.Location;

        WriteManifest(minimumWorkerHostVersion);

        if (!stageAssemblyInArtifactRoot)
        {
            return sourceAssembly;
        }

        var stagedAssembly = Path.Join(_artifactRoot, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, stagedAssembly, overwrite: true);
        return stagedAssembly;
    }

    private void WriteManifest(string minimumWorkerHostVersion)
    {
        File.WriteAllText(
            Path.Join(_artifactRoot, "omp-worker-plugin.json"),
            JsonSerializer.Serialize(new
            {
                formatVersion = 1,
                workerHost = new
                {
                    componentKey = HostComponentKey,
                    minVersion = minimumWorkerHostVersion
                }
            }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_artifactRoot))
        {
            Directory.Delete(_artifactRoot, recursive: true);
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
