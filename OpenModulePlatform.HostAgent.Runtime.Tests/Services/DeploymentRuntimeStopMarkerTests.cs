using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class DeploymentRuntimeStopMarkerTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-stop-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Write_ThenTryRead_ReturnsMatchingProperties()
    {
        var appInstanceId = Guid.NewGuid();
        DeploymentRuntimeStopMarker.Write(
            _root,
            runtimeKind: "WebApp",
            runtimeName: "MyApp",
            appInstanceId: appInstanceId,
            appInstanceKey: "instance-key",
            hostKey: "host-key");

        var marker = DeploymentRuntimeStopMarker.TryRead(_root);

        Assert.NotNull(marker);
        Assert.Equal("WebApp", marker.RuntimeKind);
        Assert.Equal("MyApp", marker.RuntimeName);
        Assert.Equal(appInstanceId, marker.AppInstanceId);
        Assert.Equal("instance-key", marker.AppInstanceKey);
        Assert.Equal("host-key", marker.HostKey);
    }

    [Fact]
    public void Exists_ReturnsTrueAfterWrite_AndFalseAfterDelete()
    {
        DeploymentRuntimeStopMarker.Write(_root, "kind", "name", Guid.NewGuid(), "key", "host");

        Assert.True(DeploymentRuntimeStopMarker.Exists(_root));

        DeploymentRuntimeStopMarker.Delete(_root);

        Assert.False(DeploymentRuntimeStopMarker.Exists(_root));
    }

    [Fact]
    public void IsExpired_ReturnsTrueAtBoundary()
    {
        DeploymentRuntimeStopMarker.Write(
            _root,
            "kind",
            "name",
            Guid.NewGuid(),
            "key",
            "host",
            expiry: TimeSpan.Zero);

        var marker = DeploymentRuntimeStopMarker.TryRead(_root);

        Assert.NotNull(marker);
        Assert.True(marker.IsExpired(marker.ExpiresUtc));
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        var marker = DeploymentRuntimeStopMarker.TryRead(_root);

        Assert.Null(marker);
    }

    [Fact]
    public void TryRead_MalformedJson_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Join(_root, "App_Data"));
        File.WriteAllText(DeploymentRuntimeStopMarker.GetPath(_root), "not-json");

        var marker = DeploymentRuntimeStopMarker.TryRead(_root);

        Assert.Null(marker);
    }

    [Fact]
    public void GetPath_ProducesCorrectAppDataSubfolderPath()
    {
        var expected = Path.Join(_root, "App_Data", "omp-runtime-stopped-for-deployment.json");

        Assert.Equal(expected, DeploymentRuntimeStopMarker.GetPath(_root));
    }
}
