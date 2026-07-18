using System.Text.Json.Nodes;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class ArtifactConfigurationFileWriterTests
{
    [Fact]
    public void ServiceAppOverlay_WithStaleWorkerAppInstanceId_GetsLiveDeploymentAppInstanceId()
    {
        var deployment = CreateDeployment();
        var staleAppInstanceId = Guid.NewGuid();
        var overlay = CreateOverlay($$"""
            {
              "SqlServer": { "Server": "localhost" },
              "Worker": {
                "AppInstanceId": "{{staleAppInstanceId}}",
                "PollSeconds": 30
              }
            }
            """);

        var result = ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
            [overlay],
            deployment,
            "Server=localhost;Database=OpenModulePlatform;",
            new HostAgentSettings());

        var appsettings = Assert.Single(result);
        var root = JsonNode.Parse(appsettings.FileContent)!.AsObject();
        var worker = root["Worker"]!.AsObject();
        Assert.Equal(deployment.AppInstanceId.ToString("D"), worker["AppInstanceId"]!.GetValue<string>());
        Assert.Equal(30, worker["PollSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void ServiceAppOverlay_WithoutWorkerSection_GetsAppInstanceIdFromDeployment()
    {
        var deployment = CreateDeployment();
        var overlay = CreateOverlay("""
            {
              "SqlServer": { "Server": "localhost" }
            }
            """);

        var result = ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
            [overlay],
            deployment,
            "Server=localhost;Database=OpenModulePlatform;",
            new HostAgentSettings());

        var appsettings = Assert.Single(result);
        var root = JsonNode.Parse(appsettings.FileContent)!.AsObject();
        var worker = root["Worker"]!.AsObject();
        Assert.Equal(deployment.AppInstanceId.ToString("D"), worker["AppInstanceId"]!.GetValue<string>());
    }

    [Fact]
    public void ServiceAppOverlay_OtherConfiguration_IsPreserved()
    {
        var deployment = CreateDeployment();
        var overlay = CreateOverlay("""
            {
              "SqlServer": { "Server": "localhost", "Database": "OtherDb" },
              "WorkOrders": { "Enabled": true },
              "ConnectionStrings": { "OmpDb": "Server=localhost;Database=OpenModulePlatform;" }
            }
            """);

        var result = ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
            [overlay],
            deployment,
            "Server=localhost;Database=OpenModulePlatform;",
            new HostAgentSettings());

        var appsettings = Assert.Single(result);
        var root = JsonNode.Parse(appsettings.FileContent)!.AsObject();
        Assert.Equal("localhost", root["SqlServer"]!["Server"]!.GetValue<string>());
        Assert.Equal("OtherDb", root["SqlServer"]!["Database"]!.GetValue<string>());
        Assert.True(root["WorkOrders"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(
            "Server=localhost;Database=OpenModulePlatform;",
            root["ConnectionStrings"]!["OmpDb"]!.GetValue<string>());
    }

    [Fact]
    public void ServiceAppOverlay_NonAppSettingsFiles_AreLeftUnchanged()
    {
        var deployment = CreateDeployment();
        var overlay = CreateOverlay("""{ "Worker": { "AppInstanceId": "00000000-0000-0000-0000-000000000000" } }""");
        var other = new ArtifactConfigurationFileDescriptor
        {
            ArtifactConfigurationFileId = 2,
            ArtifactId = deployment.ArtifactId,
            RelativePath = "config/nlog.config",
            FileContent = "<nlog />"
        };

        var result = ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
            [overlay, other],
            deployment,
            "Server=localhost;Database=OpenModulePlatform;",
            new HostAgentSettings());

        Assert.Equal(2, result.Count);
        Assert.Same(other, result[1]);
    }

    [Fact]
    public void WorkerManagerOverlay_WithAppSettingsJson_ForcesLiveIdentityAndPreservesRest()
    {
        var deployment = CreateWorkerManagerDeployment();
        var settings = new HostAgentSettings();
        var overlay = CreateOverlay("""
            {
              "ConnectionStrings": { "OmpDb": "Server=stale;Database=StaleDb;" },
              "WorkerManager": {
                "HostKey": "stale-host",
                "HostName": "STALE-MACHINE",
                "RefreshSeconds": 60,
                "MaxRestartsPerWindow": 9,
                "OmpDatabase": { "RuntimeKind": "custom-runtime" },
                "HostAgentRpc": { "Enabled": false, "PipeName": "stale-pipe", "TimeoutSeconds": 99 }
              },
              "Logging": { "LogLevel": { "Default": "Debug" } }
            }
            """);

        var result = ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
            [overlay],
            deployment,
            "Server=localhost;Database=OpenModulePlatform;",
            settings);

        var appsettings = Assert.Single(result);
        var root = JsonNode.Parse(appsettings.FileContent)!.AsObject();
        var workerManager = root["WorkerManager"]!.AsObject();

        // Identity is forced from the live deployment/settings.
        Assert.Equal("test-host", workerManager["HostKey"]!.GetValue<string>());
        Assert.Equal(Environment.MachineName, workerManager["HostName"]!.GetValue<string>());
        Assert.Equal(
            "Server=localhost;Database=OpenModulePlatform;",
            root["ConnectionStrings"]!["OmpDb"]!.GetValue<string>());
        var rpc = workerManager["HostAgentRpc"]!.AsObject();
        Assert.Equal(settings.ResolveRpcPipeName(), rpc["PipeName"]!.GetValue<string>());

        // Everything else in the overlay is preserved.
        Assert.Equal(60, workerManager["RefreshSeconds"]!.GetValue<int>());
        Assert.Equal(9, workerManager["MaxRestartsPerWindow"]!.GetValue<int>());
        Assert.Equal("custom-runtime", workerManager["OmpDatabase"]!["RuntimeKind"]!.GetValue<string>());
        Assert.False(rpc["Enabled"]!.GetValue<bool>());
        Assert.Equal(99, rpc["TimeoutSeconds"]!.GetValue<int>());
        Assert.Equal("Debug", root["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
    }

    [Fact]
    public void WorkerManagerOverlay_WithoutIdentitySections_GetsLiveIdentity()
    {
        var deployment = CreateWorkerManagerDeployment();
        var settings = new HostAgentSettings();
        var overlay = CreateOverlay("""{ "Custom": { "Enabled": true } }""");

        var result = ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
            [overlay],
            deployment,
            "Server=localhost;Database=OpenModulePlatform;",
            settings);

        var appsettings = Assert.Single(result);
        var root = JsonNode.Parse(appsettings.FileContent)!.AsObject();
        var workerManager = root["WorkerManager"]!.AsObject();
        Assert.Equal("test-host", workerManager["HostKey"]!.GetValue<string>());
        Assert.Equal(Environment.MachineName, workerManager["HostName"]!.GetValue<string>());
        Assert.Equal(
            settings.ResolveRpcPipeName(),
            workerManager["HostAgentRpc"]!["PipeName"]!.GetValue<string>());
        Assert.Equal(
            "Server=localhost;Database=OpenModulePlatform;",
            root["ConnectionStrings"]!["OmpDb"]!.GetValue<string>());
        Assert.True(root["Custom"]!["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void WorkerManagerOverlay_NonAppSettingsFiles_AreLeftUnchanged()
    {
        var deployment = CreateWorkerManagerDeployment();
        var overlay = CreateOverlay("""{ "WorkerManager": { "HostKey": "stale-host" } }""");
        var other = new ArtifactConfigurationFileDescriptor
        {
            ArtifactConfigurationFileId = 2,
            ArtifactId = deployment.ArtifactId,
            RelativePath = "config/nlog.config",
            FileContent = "<nlog />"
        };

        var result = ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
            [overlay, other],
            deployment,
            "Server=localhost;Database=OpenModulePlatform;",
            new HostAgentSettings());

        Assert.Equal(2, result.Count);
        Assert.Same(other, result[1]);
    }

    [Fact]
    public void ServiceAppOverlay_InvalidJson_Throws()
    {
        var deployment = CreateDeployment();
        var overlay = CreateOverlay("{ not json");

        Assert.Throws<InvalidOperationException>(() =>
            ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
                [overlay],
                deployment,
                "Server=localhost;Database=OpenModulePlatform;",
                new HostAgentSettings()));
    }

    [Fact]
    public void ServiceAppOverlay_WithoutAppSettingsJson_AddsBuiltInConfiguration()
    {
        var deployment = CreateDeployment();

        var result = ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
            [],
            deployment,
            "Server=localhost;Database=OpenModulePlatform;",
            new HostAgentSettings());

        var appsettings = Assert.Single(result);
        var root = JsonNode.Parse(appsettings.FileContent)!.AsObject();
        Assert.Equal(
            deployment.AppInstanceId.ToString("D"),
            root["Worker"]!["AppInstanceId"]!.GetValue<string>());
    }

    private static ServiceAppDeploymentDescriptor CreateDeployment()
        => new()
        {
            HostId = Guid.NewGuid(),
            HostKey = "test-host",
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "ikrock_backend",
            ModuleInstanceKey = "module-test",
            DisplayName = "Test service app",
            ArtifactId = 42,
            Version = "1.0.0"
        };

    private static ServiceAppDeploymentDescriptor CreateWorkerManagerDeployment()
        => new()
        {
            HostId = Guid.NewGuid(),
            HostKey = "test-host",
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "omp_workermanager",
            ModuleInstanceKey = "module-test",
            DisplayName = "OMP WorkerManager",
            ArtifactId = 42,
            Version = "1.0.0"
        };

    private static ArtifactConfigurationFileDescriptor CreateOverlay(string content)
        => new()
        {
            ArtifactConfigurationFileId = 1,
            ArtifactId = 42,
            RelativePath = "appsettings.json",
            FileContent = content
        };
}
