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
    public void WorkerManagerOverlay_WithAppSettingsJson_IsLeftUnchanged()
    {
        var deployment = new ServiceAppDeploymentDescriptor
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
        const string content = """{ "WorkerManager": { "HostKey": "overlay-host" } }""";
        var overlay = CreateOverlay(content);

        var result = ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
            [overlay],
            deployment,
            "Server=localhost;Database=OpenModulePlatform;",
            new HostAgentSettings());

        var appsettings = Assert.Single(result);
        Assert.Same(overlay, appsettings);
        Assert.Equal(content, appsettings.FileContent);
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

    private static ArtifactConfigurationFileDescriptor CreateOverlay(string content)
        => new()
        {
            ArtifactConfigurationFileId = 1,
            ArtifactId = 42,
            RelativePath = "appsettings.json",
            FileContent = content
        };
}
