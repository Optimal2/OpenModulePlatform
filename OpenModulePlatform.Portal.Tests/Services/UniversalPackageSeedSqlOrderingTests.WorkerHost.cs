using System.IO.Compression;
using System.Text.Json.Nodes;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// A universal package carrying a new WorkerProcessHost together with worker plugins that
/// require it must import the plugins on the FIRST import, whatever the package composition.
/// </summary>
/// <remarks>
/// Lives in this class only to share its database fixture (the fixture's database name is
/// per process, so a second fixture class would share and drop the same database).
///
/// The shape under test: the host artifact's module definition is NOT in the package, the
/// plugin's is. The host therefore used to be imported by the fall-through loop that runs
/// after every module batch -- after the plugin had already been checked against a host
/// that was not yet selected, so the plugin failed with "&lt;not selected&gt;" and only a
/// re-import succeeded. Reordering inside a batch could not help, because host and plugin
/// never share a batch.
/// </remarks>
public sealed partial class UniversalPackageSeedSqlOrderingTests
{
    private const string WorkerHostAppKey = "omp_workerprocesshost";
    private const string WorkerHostTargetName = "omp-workerprocesshost";
    private const string WorkerHostVersion = "0.3.71";
    private const string PluginVersion = "1.0.0";
    private const string PluginTargetName = "whplugin";

    [Fact]
    public async Task HostAgentImport_WhenHostDefinitionIsNotInThePackage_WorkerPluginImportsOnFirstAttempt()
    {
        const string hostModuleKey = "whsamepkghost";
        const string pluginModuleKey = "whsamepkgplugin";
        var hostKey = "whsamepkg-" + Guid.NewGuid().ToString("N")[..8];

        var workRoot = CreateWorkRoot();
        try
        {
            var importRoot = Directory.CreateDirectory(Path.Join(workRoot, "import")).FullName;
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;

            // Previous state: the host module's definition is applied and this host runs an
            // enabled WorkerProcessHost app instance that has no artifact selected yet.
            var arrangePackage = Path.Join(workRoot, $"omp-universal__{hostModuleKey}__arrange.zip");
            using (var archive = ZipFile.Open(arrangePackage, ZipArchiveMode.Create))
            {
                WriteTextEntry(
                    archive,
                    UniversalModulePackageReaderManifestName,
                    """{"formatVersion":1,"packageKey":"worker-host-arrange","packageVersion":"1.0.0"}""");
                WriteTextEntry(
                    archive,
                    $"module-definitions/{hostModuleKey}.module-definition.json",
                    BuildMinimalDefinitionJson(hostModuleKey, WorkerHostAppKey, "WorkerHost", "worker-host", WorkerHostTargetName));
            }

            File.Copy(arrangePackage, Path.Join(importRoot, Path.GetFileName(arrangePackage)));
            await RunHostAgentImportAsync(importRoot, storeRoot, hostKey);
            Assert.True(
                !Directory.Exists(Path.Join(importRoot, "failed")) || Directory.GetFiles(Path.Join(importRoot, "failed")).Length == 0,
                "The arrange package landed in failed\\: " + ReadFailedImportReasons(importRoot));
            await ArrangeWorkerHostAppInstanceAsync(hostModuleKey, hostKey);

            // The package under test: the plugin's definition, the plugin, and the new host,
            // but not the host's definition.
            var packagePath = Path.Join(workRoot, $"omp-universal__{pluginModuleKey}__{PluginVersion}.zip");
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                WriteTextEntry(
                    archive,
                    UniversalModulePackageReaderManifestName,
                    """{"formatVersion":1,"packageKey":"worker-host-same-package","packageVersion":"1.0.0"}""");
                WriteTextEntry(
                    archive,
                    $"module-definitions/{pluginModuleKey}.module-definition.json",
                    BuildMinimalDefinitionJson(pluginModuleKey, AppKey, "Worker", "worker-plugin", PluginTargetName));
                archive.CreateEntryFromFile(
                    BuildWorkerPluginArtifact(workRoot, minWorkerHostVersion: "0.3.21"),
                    $"artifacts/{pluginModuleKey}__{AppKey}__worker-plugin__{PluginTargetName}__{PluginVersion}.zip");
                WriteArtifactZipEntry(
                    archive,
                    $"artifacts/{hostModuleKey}__{WorkerHostAppKey}__worker-host__{WorkerHostTargetName}__{WorkerHostVersion}.zip",
                    "worker host payload " + WorkerHostVersion);
            }

            File.Copy(packagePath, Path.Join(importRoot, Path.GetFileName(packagePath)));
            await RunHostAgentImportAsync(importRoot, storeRoot, hostKey);

            Assert.True(
                Directory.GetFiles(Path.Join(importRoot, "failed")).Length == 0,
                "The package landed in failed\\: " + ReadFailedImportReasons(importRoot));
            var repository = _fixture.CreateHostAgentRepository();
            var pluginApp = await repository.ResolveArtifactZipImportAppAsync(pluginModuleKey, AppKey, CancellationToken.None);
            Assert.NotNull(pluginApp);
            Assert.True(
                await _fixture.ArtifactExistsAsync(pluginApp.AppId, PluginVersion, "worker-plugin", PluginTargetName),
                "The worker plugin was not registered on the first import of the package that carried its host.");
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    private async Task ArrangeWorkerHostAppInstanceAsync(string hostModuleKey, string hostKey)
    {
        var instanceId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var moduleInstanceId = Guid.NewGuid();
        var appInstanceId = Guid.NewGuid();
        await _fixture.ExecuteAsync(
            $"INSERT INTO omp.Instances (InstanceId, InstanceKey, DisplayName) VALUES ('{instanceId}', N'{hostKey}', N'{hostKey}');",
            $"INSERT INTO omp.Hosts (HostId, InstanceId, HostKey, IsEnabled) VALUES ('{hostId}', '{instanceId}', N'{hostKey}', 1);",
            $"""
            INSERT INTO omp.ModuleInstances (ModuleInstanceId, InstanceId, ModuleId, ModuleInstanceKey, DisplayName)
            SELECT '{moduleInstanceId}', '{instanceId}', m.ModuleId, N'{hostModuleKey}', N'{hostModuleKey}'
            FROM omp.Modules m WHERE m.ModuleKey = N'{hostModuleKey}';
            """,
            $"""
            INSERT INTO omp.AppInstances (AppInstanceId, ModuleInstanceId, HostId, AppId, AppInstanceKey, DisplayName, IsEnabled, IsAllowed, DesiredState)
            SELECT '{appInstanceId}', '{moduleInstanceId}', '{hostId}', a.AppId, N'{hostKey}-workerhost', N'Worker host', 1, 1, 1
            FROM omp.Apps a
            INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
            WHERE m.ModuleKey = N'{hostModuleKey}' AND a.AppKey = N'{WorkerHostAppKey}';
            """);
    }

    private async Task RunHostAgentImportAsync(string importRoot, string storeRoot, string hostKey)
    {
        var settings = new HostAgentSettings
        {
            HostKey = hostKey,
            CentralArtifactRoot = storeRoot,
            ArtifactZipImport = new HostAgentArtifactZipImportSettings
            {
                IsEnabled = true,
                ImportPath = importRoot
            }
        };
        var service = new ArtifactZipImportService(
            new StaticOptionsMonitor<HostAgentSettings>(settings),
            _fixture.CreateHostAgentRepository(),
            NullLogger<ArtifactZipImportService>.Instance);
        await service.ImportPendingAsync(CancellationToken.None);
    }

    private static string BuildWorkerPluginArtifact(string workRoot, string minWorkerHostVersion)
    {
        var payloadRoot = Directory.CreateDirectory(Path.Join(workRoot, "plugin-payload")).FullName;
        File.WriteAllText(Path.Join(payloadRoot, "Plugin.dll"), "fixture");
        var artifactPath = Path.Join(workRoot, "plugin-artifact.zip");
        new ArtifactPackageWriter().CreateFromPayloadDirectory(
            payloadRoot,
            artifactPath,
            [],
            minWorkerHostVersion: minWorkerHostVersion);
        return artifactPath;
    }

    private static string BuildMinimalDefinitionJson(
        string moduleKey,
        string appKey,
        string appType,
        string packageType,
        string targetName)
    {
        var definition = new JsonObject
        {
            ["moduleKey"] = moduleKey,
            ["definitionVersion"] = "1.0.0",
            ["formatVersion"] = 1,
            ["module"] = new JsonObject
            {
                ["displayName"] = moduleKey,
                ["moduleType"] = "WorkerModule",
                ["schemaName"] = "omp_" + moduleKey
            },
            ["apps"] = new JsonArray(new JsonObject
            {
                ["appKey"] = appKey,
                ["displayName"] = appKey,
                ["appType"] = appType
            }),
            ["compatibleArtifacts"] = new JsonArray(new JsonObject
            {
                ["appKey"] = appKey,
                ["packageType"] = packageType,
                ["targetName"] = targetName
            }),
            ["sqlScripts"] = new JsonArray(),
            ["integrity"] = new JsonObject()
        };
        return definition.ToJsonString();
    }
}
