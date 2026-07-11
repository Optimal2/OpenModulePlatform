using System.Globalization;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class DeploymentLockFileTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-lock-{Guid.NewGuid():N}"));

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
    public async Task WriteAsync_ThenReadStatus_IsLocked()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create(
            Guid.NewGuid().ToString("N"),
            "app-key",
            "owner",
            "reason",
            now,
            now.Add(TimeSpan.FromMinutes(5)));

        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);

        Assert.True(status.IsLocked);
        Assert.False(status.IsExpired);
        Assert.NotNull(status.Document);
        Assert.Null(status.Diagnostic);
    }

    [Fact]
    public void ReadStatus_MissingFile_IsNotLocked()
    {
        var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);

        Assert.False(status.IsLocked);
        Assert.False(status.IsExpired);
        Assert.Null(status.Diagnostic);
    }

    [Fact]
    public async Task ReadStatus_ExpiredLock_IsExpiredAndNotLocked()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create(
            Guid.NewGuid().ToString("N"),
            "app-key",
            "owner",
            "reason",
            now.Subtract(TimeSpan.FromMinutes(10)),
            now.Subtract(TimeSpan.FromMinutes(5)));

        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var status = DeploymentLockFile.ReadStatus(_root, now);

        Assert.False(status.IsLocked);
        Assert.True(status.IsExpired);
        Assert.NotNull(status.Document);
    }

    [Fact]
    public async Task ReadStatus_SchemaMismatch_IsLockedWithDiagnostic()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create(
            Guid.NewGuid().ToString("N"),
            "app-key",
            "owner",
            "reason",
            now,
            now.Add(TimeSpan.FromMinutes(5)));
        doc = doc with { Schema = "unsupported" };

        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var status = DeploymentLockFile.ReadStatus(_root, now);

        Assert.True(status.IsLocked);
        Assert.NotNull(status.Diagnostic);
        Assert.Contains("unsupported schema", status.Diagnostic);
    }

    [Fact]
    public async Task TryDelete_ExistingFile_RemovesFile()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create(
            Guid.NewGuid().ToString("N"),
            "app-key",
            "owner",
            "reason",
            now,
            now.Add(TimeSpan.FromMinutes(5)));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);

        DeploymentLockFile.TryDelete(DeploymentLockFile.GetPath(_root));

        Assert.False(File.Exists(DeploymentLockFile.GetPath(_root)));
    }

    [Fact]
    public void TryDelete_MissingFile_IsNoOp()
    {
        var path = DeploymentLockFile.GetPath(_root);

        DeploymentLockFile.TryDelete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ToDeploymentSkippedMessage_ProducesReadableMessageWithAllFields()
    {
        var now = DateTimeOffset.UtcNow;
        var document = DeploymentLockFile.Create(
            "lock-id",
            "app-key",
            "owner",
            "reason",
            now,
            now.Add(TimeSpan.FromMinutes(5)));
        var status = DeploymentLockStatus.Locked("/path/to/lock", document, "diagnostic detail");

        var message = status.ToDeploymentSkippedMessage("WebApp");

        Assert.Contains("WebApp deployment is skipped", message);
        Assert.Contains("lock-id", message);
        Assert.Contains("app-key", message);
        Assert.Contains("owner", message);
        Assert.Contains("reason", message);
        Assert.Contains(document.ExpiresUtc.UtcDateTime.ToString("u", CultureInfo.InvariantCulture), message);
        Assert.Contains("diagnostic detail", message);
        Assert.Contains("/path/to/lock", message);
    }
}
