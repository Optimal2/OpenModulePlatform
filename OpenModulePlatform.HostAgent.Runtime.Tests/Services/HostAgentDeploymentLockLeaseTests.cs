using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class HostAgentDeploymentLockLeaseTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-lease-{Guid.NewGuid():N}"));

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
    public async Task TryAcquireAsync_WhenUnlocked_ReturnsAcquiredWithLease()
    {
        var result = await HostAgentDeploymentLockLease.TryAcquireAsync(
            _root,
            "app-key",
            "owner",
            "reason",
            NullLogger.Instance,
            CancellationToken.None);

        Assert.NotNull(result.Lease);
        Assert.Null(result.ExistingLockStatus);
        Assert.True(File.Exists(DeploymentLockFile.GetPath(_root)));
        var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);
        Assert.True(status.IsLocked);

        await result.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenLocked_ReturnsLockedWithExistingStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var existing = DeploymentLockFile.Create(
            "existing-lock-id",
            "app-key",
            "owner",
            "reason",
            now,
            now.Add(TimeSpan.FromMinutes(5)));
        await DeploymentLockFile.WriteAsync(_root, existing, CancellationToken.None);

        var result = await HostAgentDeploymentLockLease.TryAcquireAsync(
            _root,
            "app-key",
            "owner",
            "reason",
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Null(result.Lease);
        Assert.NotNull(result.ExistingLockStatus);
        Assert.Equal("existing-lock-id", result.ExistingLockStatus!.Document!.LockId);
    }

    [Fact]
    public async Task DisposeAsync_DeletesLockFile_WhenStillOwned()
    {
        var result = await HostAgentDeploymentLockLease.TryAcquireAsync(
            _root,
            "app-key",
            "owner",
            "reason",
            NullLogger.Instance,
            CancellationToken.None);

        await result.Lease!.DisposeAsync();

        Assert.False(File.Exists(DeploymentLockFile.GetPath(_root)));
    }

    [Fact]
    public async Task DisposeAsync_DoesNotDeleteLockFile_WhenLockIdChanged()
    {
        var result = await HostAgentDeploymentLockLease.TryAcquireAsync(
            _root,
            "app-key",
            "owner",
            "reason",
            NullLogger.Instance,
            CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        var other = DeploymentLockFile.Create(
            "other-lock-id",
            "app-key",
            "owner",
            "reason",
            now,
            now.Add(TimeSpan.FromMinutes(5)));
        await DeploymentLockFile.WriteAsync(_root, other, CancellationToken.None);

        await result.Lease!.DisposeAsync();

        Assert.True(File.Exists(DeploymentLockFile.GetPath(_root)));
        var status = DeploymentLockFile.ReadStatus(_root, now);
        Assert.Equal("other-lock-id", status.Document!.LockId);
    }

    [Fact]
    public async Task TryAcquireAsync_WritesLockFileToCorrectAppDataPath()
    {
        await HostAgentDeploymentLockLease.TryAcquireAsync(
            _root,
            "app-key",
            "owner",
            "reason",
            NullLogger.Instance,
            CancellationToken.None);

        var expected = Path.Join(_root, "App_Data", "omp-deployment.lock.json");

        Assert.Equal(expected, DeploymentLockFile.GetPath(_root));
        Assert.True(File.Exists(expected));
    }
}
