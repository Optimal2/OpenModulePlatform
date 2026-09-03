using System.Globalization;
using System.Text;
using System.Text.Json;
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
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
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

    [Fact]
    public async Task TryRenewExclusiveAsync_WhenOwned_RenewsInPlace()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);

        var outcome = await DeploymentLockFile.TryRenewExclusiveAsync(
            _root,
            "lock-id",
            current => current with
            {
                UpdatedUtc = now.AddSeconds(30),
                ExpiresUtc = now.AddMinutes(10)
            },
            CancellationToken.None);

        Assert.Equal(DeploymentLockRenewalResult.Renewed, outcome.Result);
        Assert.Equal(now.AddSeconds(30), outcome.Document!.UpdatedUtc);

        var status = DeploymentLockFile.ReadStatus(_root, now);
        Assert.Equal("lock-id", status.Document!.LockId);
        Assert.Equal(now.AddSeconds(30), status.Document.UpdatedUtc);
        Assert.Equal(now.AddMinutes(10), status.Document.ExpiresUtc);
    }

    [Fact]
    public async Task TryRenewExclusiveAsync_WhenOwnedByAnotherClaimant_ReturnsLostAndDoesNotOverwrite()
    {
        var now = DateTimeOffset.UtcNow;
        var foreign = DeploymentLockFile.Create("foreign-lock-id", "app-key", "HostAgent", "deploying", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, foreign, CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(DeploymentLockFile.GetPath(_root));

        var outcome = await DeploymentLockFile.TryRenewExclusiveAsync(
            _root,
            "our-lock-id",
            current => current with { UpdatedUtc = now.AddSeconds(30) },
            CancellationToken.None);

        Assert.Equal(DeploymentLockRenewalResult.Lost, outcome.Result);
        Assert.Equal("foreign-lock-id", outcome.Document!.LockId);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(DeploymentLockFile.GetPath(_root)));
    }

    [Fact]
    public async Task TryRenewExclusiveAsync_MissingFile_ReturnsNotFound()
    {
        var outcome = await DeploymentLockFile.TryRenewExclusiveAsync(
            _root,
            "lock-id",
            current => current,
            CancellationToken.None);

        Assert.Equal(DeploymentLockRenewalResult.NotFound, outcome.Result);
        Assert.Null(outcome.Document);
    }

    /// <summary>
    /// A foreign exclusive handle blocks the renewal's open, but only for as long as it is
    /// held: the renewal retries and completes once the handle is released, and the foreign
    /// document written through that handle is never overwritten mid-renewal.
    /// </summary>
    [Fact]
    public async Task TryRenewExclusiveAsync_WhileHeldExclusively_WaitsForTheHandleAndThenSeesTheForeignClaim()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);

        var path = DeploymentLockFile.GetPath(_root);
        var foreign = DeploymentLockFile.Create("foreign-lock-id", "app-key", "HostAgent", "deploying", now, now.AddMinutes(5));

        var renewalTask = Task.Run(async () =>
        {
            // Give the exclusive handle below time to land first, then attempt the renewal.
            await Task.Delay(100);
            return await DeploymentLockFile.TryRenewExclusiveAsync(
                _root,
                "lock-id",
                current => current with { UpdatedUtc = now.AddSeconds(30) },
                CancellationToken.None);
        });

        // Hold the file exclusively and write the foreign claim through the same handle,
        // the one interleaving a non-atomic read-then-write could never survive.
        await using (var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var json = JsonSerializer.Serialize(foreign, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            handle.SetLength(0);
            handle.Position = 0;
            await handle.WriteAsync(Encoding.UTF8.GetBytes(json));
            await handle.FlushAsync();

            await Task.Delay(300);
        }

        var outcome = await renewalTask;

        Assert.Equal(DeploymentLockRenewalResult.Lost, outcome.Result);
        Assert.Equal("foreign-lock-id", outcome.Document!.LockId);
        Assert.Equal("foreign-lock-id", DeploymentLockFile.ReadStatus(_root, now).Document!.LockId);
    }

    /// <summary>
    /// File.Move(overwrite: true) onto an exclusively locked target fails with a sharing
    /// violation; WriteAsync retries it for a bounded moment instead of failing the write.
    /// </summary>
    [Fact]
    public async Task WriteAsync_WhileTargetIsExclusivelyLocked_RetriesUntilTheHandleIsReleased()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);

        var path = DeploymentLockFile.GetPath(_root);
        var replacement = doc with { Owner = "replacement-owner", UpdatedUtc = now.AddSeconds(30) };

        var writeTask = Task.Run(async () =>
        {
            await Task.Delay(100);
            await DeploymentLockFile.WriteAsync(_root, replacement, CancellationToken.None);
        });

        await using (var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Task.Delay(300);
        }

        await writeTask;

        var status = DeploymentLockFile.ReadStatus(_root, now);
        Assert.Equal("replacement-owner", status.Document!.Owner);
    }
}
