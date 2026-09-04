using System.Text.Json;
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
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
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
    public async Task TryAcquireAsync_WhenExistingLockExpired_ClearsItAndClaims()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = DeploymentLockFile.Create(
            "stale-lock-id",
            "app-key",
            "owner",
            "crashed deployment",
            now.AddMinutes(-30),
            now.AddMinutes(-10));
        await DeploymentLockFile.WriteAsync(_root, stale, CancellationToken.None);

        var result = await HostAgentDeploymentLockLease.TryAcquireAsync(
            _root,
            "app-key",
            "owner",
            "reason",
            NullLogger.Instance,
            CancellationToken.None);

        Assert.NotNull(result.Lease);
        var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);
        Assert.True(status.IsLocked);
        Assert.NotEqual("stale-lock-id", status.Document!.LockId);

        await result.Lease!.DisposeAsync();
    }

    /// <summary>
    /// The expired-lock cleanup never removes a claim that is no longer the expired one.
    /// </summary>
    /// <remarks>
    /// R12-A2. R7-D6 made the claim atomic with FileMode.CreateNew and then left an
    /// unconditional TryDelete standing immediately in front of it, which moved the
    /// check-then-act from the write to the delete rather than removing it: whatever the
    /// read had concluded microseconds earlier, the delete ran, so a competing HostAgent's
    /// fresh and perfectly valid lock file was destroyed and both agents ended up believing
    /// they owned the deployment. The state below is exactly what that competitor leaves
    /// behind -- a different LockId with a future expiry -- and it must survive.
    /// </remarks>
    [Fact]
    public async Task TryClearExpiredLock_LeavesAFreshCompetitorClaimAlone()
    {
        var now = DateTimeOffset.UtcNow;
        var competitor = DeploymentLockFile.Create(
            "competitor-lock-id",
            "app-key",
            "other-owner",
            "reason",
            now,
            now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, competitor, CancellationToken.None);

        // "stale-lock-id" is the expired document this caller observed a moment ago; the
        // file has since been replaced by the winner of the race.
        HostAgentDeploymentLockLease.TryClearExpiredLock(_root, "stale-lock-id", NullLogger.Instance);

        Assert.True(File.Exists(DeploymentLockFile.GetPath(_root)));
        var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);
        Assert.Equal("competitor-lock-id", status.Document!.LockId);
    }

    [Fact]
    public async Task TryClearExpiredLock_RemovesTheExpiredDocumentItObserved()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = DeploymentLockFile.Create(
            "stale-lock-id",
            "app-key",
            "owner",
            "reason",
            now.AddMinutes(-30),
            now.AddMinutes(-10));
        await DeploymentLockFile.WriteAsync(_root, stale, CancellationToken.None);

        HostAgentDeploymentLockLease.TryClearExpiredLock(_root, "stale-lock-id", NullLogger.Instance);

        Assert.False(File.Exists(DeploymentLockFile.GetPath(_root)));
    }

    // The expected verdict travels as a string because the enum is internal and this test
    // method has to stay public for xunit to discover it.
    [Theory]
    [InlineData("mine", false, "Held")]
    [InlineData("theirs", false, "Lost")]
    // An expired document that still names us is our own lease running late, not a takeover.
    [InlineData("mine", true, "Held")]
    [InlineData("theirs", true, "Lost")]
    public void ClassifyRenewalOwnership_WithReadableDocument_IsAuthoritative(
        string lockId,
        bool expired,
        string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var document = DeploymentLockFile.Create(
            lockId,
            "app-key",
            "owner",
            "reason",
            now,
            expired ? now.AddMinutes(-1) : now.AddMinutes(5));
        var status = expired
            ? DeploymentLockStatus.Expired("path", document)
            : DeploymentLockStatus.Locked("path", document, null);

        Assert.Equal(
            expected,
            HostAgentDeploymentLockLease.ClassifyRenewalOwnership(status, "mine").ToString());
    }

    /// <summary>
    /// A read that failed proves nothing about who owns the lock.
    /// </summary>
    /// <remarks>
    /// R12-A2/A4: ReadStatus fails CLOSED. An IOException, a denied read, a truncated file
    /// or a planted reparse point all come back as Locked with a null Document, and the old
    /// renewal loop compared that null against its own LockId, concluded "somebody else has
    /// it", logged that as fact and returned -- ending renewal permanently on one transient
    /// fault while the deployment it guarded ran on.
    /// </remarks>
    [Fact]
    public void ClassifyRenewalOwnership_WithUnreadableLockFile_IsIndeterminate()
    {
        var status = DeploymentLockStatus.Locked("path", null, "Deployment lock file could not be read: boom.");

        Assert.Equal(
            HostAgentDeploymentLockLease.RenewalOwnership.Indeterminate,
            HostAgentDeploymentLockLease.ClassifyRenewalOwnership(status, "mine"));
    }

    [Fact]
    public void ClassifyRenewalOwnership_WithNoLockFile_IsHeldSoTheLeaseIsReasserted()
    {
        Assert.Equal(
            HostAgentDeploymentLockLease.RenewalOwnership.Held,
            HostAgentDeploymentLockLease.ClassifyRenewalOwnership(DeploymentLockStatus.NotLocked("path"), "mine"));
    }

    /// <summary>
    /// Renewal survives an unreadable lock file and resumes once it can be read again.
    /// </summary>
    /// <remarks>
    /// R12-A4 end to end. Before the fix the corrupt read below ended the loop for good, so
    /// UpdatedUtc never moved again however healthy the file became -- and the lease expired
    /// silently under a running deployment five minutes later.
    /// </remarks>
    [Fact]
    public async Task Renewal_SurvivesATransientUnreadableLockFile()
    {
        var result = await HostAgentDeploymentLockLease.TryAcquireAsync(
            _root,
            "app-key",
            "owner",
            "reason",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(100),
            maxConsecutiveIndeterminateReads: 100,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.NotNull(result.Lease);
        var path = DeploymentLockFile.GetPath(_root);
        var originalJson = ReadWithRetry(path);
        var originalUpdatedUtc = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow).Document!.UpdatedUtc;

        WriteWithRetry(path, "{ this is not valid json");
        await Task.Delay(350);
        WriteWithRetry(path, originalJson);

        var renewed = await WaitUntilAsync(() =>
        {
            var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);
            return status.Document is not null && status.Document.UpdatedUtc > originalUpdatedUtc;
        });

        await result.Lease!.DisposeAsync();

        Assert.True(renewed, "Renewal did not resume after the lock file became readable again.");
    }

    /// <summary>
    /// Renewal still stops, and stops writing, when another lease demonstrably owns the lock.
    /// </summary>
    /// <remarks>
    /// The tolerance added for R12-A4 must not turn into "renew regardless"; the intended
    /// pass-through of the guard is tested here alongside the case it was blocking (metod
    /// section 4.5).
    /// </remarks>
    [Fact]
    public async Task Renewal_StopsAndDoesNotOverwrite_WhenAnotherLeaseOwnsTheLock()
    {
        var result = await HostAgentDeploymentLockLease.TryAcquireAsync(
            _root,
            "app-key",
            "owner",
            "reason",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(100),
            maxConsecutiveIndeterminateReads: 100,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.NotNull(result.Lease);

        var now = DateTimeOffset.UtcNow;
        var other = DeploymentLockFile.Create(
            "other-lock-id",
            "app-key",
            "other-owner",
            "reason",
            now,
            now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, other, CancellationToken.None);

        await Task.Delay(500);

        var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);
        Assert.Equal("other-lock-id", status.Document!.LockId);
        Assert.Equal(other.UpdatedUtc, status.Document.UpdatedUtc);

        await result.Lease!.DisposeAsync();

        // Dispose must not remove a lock file this lease no longer owns either.
        Assert.True(File.Exists(DeploymentLockFile.GetPath(_root)));
    }

    /// <summary>
    /// A foreign claim injected mid-tick -- after the renewal's ownership read but before
    /// its overwrite lands -- is never overwritten: the renewal's read-verify-write is one
    /// atomic step inside an exclusive handle, so the injected claim either is seen (ending
    /// renewal as Lost) or blocks the renewal's open until it is on disk.
    /// </summary>
    /// <remarks>
    /// This is the exact interleaving the old two-step renewal (ReadStatus, classify, then
    /// an unconditional WriteAsync) lost. The test holds the lock file open with a handle
    /// that still lets the old loop's ReadStatus succeed -- FileAccess.Read with
    /// FileShare.ReadWrite, deliberately WITHOUT FileShare.Delete -- writes the foreign
    /// claim through a second handle while the tick's File.Move retry loop is blocked
    /// behind the first, and only then lets go. Against the old pattern the stuck tick's
    /// Move retry waited this handle out and overwrote the foreign claim, because nothing
    /// re-checked ownership after the read: this test was RED there. TryRenewExclusiveAsync
    /// holds the file exclusively across the read, the LockId comparison and the write, so
    /// its open retries behind the test handle and the first successful open reads the
    /// foreign claim and stops the lease.
    /// </remarks>
    [Fact]
    public async Task Renewal_DoesNotOverwrite_AForeignClaimInjectedBetweenOwnershipCheckAndWrite()
    {
        var result = await HostAgentDeploymentLockLease.TryAcquireAsync(
            _root,
            "app-key",
            "owner",
            "reason",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(50),
            maxConsecutiveIndeterminateReads: 100,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.NotNull(result.Lease);

        var now = DateTimeOffset.UtcNow;
        var foreign = DeploymentLockFile.Create(
            "foreign-lock-id",
            "app-key",
            "other-owner",
            "deploying",
            now,
            now.AddMinutes(5));
        var path = DeploymentLockFile.GetPath(_root);

        // Hold the file so a status READ still succeeds but no temp+Move overwrite can
        // land, across several renewal ticks. While this handle is held, a tick of the old
        // loop is stuck in WriteAsync's Move retry loop; the foreign claim is written into
        // that window. The atomic renewal's exclusive open simply retries behind it. The
        // open itself is retried for the same reason ReadWithRetry/WriteWithRetry exist: a
        // renewal tick holding its own exclusive handle in this microsecond window is a
        // collision the test is not about.
        using (var handle = RetryFileOperation(() =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        {
            await Task.Delay(150);

            var json = JsonSerializer.Serialize(
                foreign,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            WriteWithRetry(path, json);

            await Task.Delay(250);
        }

        // Give the renewal loop room for several more ticks; the first tick that can open
        // the file must read the foreign document and stop as Lost, never replace it.
        await Task.Delay(600);

        // Poll past a read that collides with a renewal hold (ReadStatus fails closed with
        // a null Document there) instead of dereferencing through it. The ownership
        // assertion itself is unchanged: against the old two-step loop the settled content
        // is the lease's own document, because the stuck tick's Move retry waited the test
        // handle out and overwrote the foreign claim.
        var status = await WaitUntilAsync(
            () => DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow).Document is not null)
            ? DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow)
            : throw new InvalidOperationException("Deployment lock file stayed unreadable past the test deadline.");
        Assert.Equal("foreign-lock-id", status.Document!.LockId);
        Assert.Equal(foreign.UpdatedUtc, status.Document.UpdatedUtc);

        await result.Lease!.DisposeAsync();

        // Dispose must not remove a lock file this lease no longer owns either.
        Assert.True(File.Exists(path));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    /// <summary>
    /// The renewal loop reads the same file the test writes, and the sharing modes collide,
    /// so both directions retry rather than fail the test for a reason it is not about.
    /// </summary>
    private static string ReadWithRetry(string path)
        => RetryFileOperation(() => File.ReadAllText(path));

    private static void WriteWithRetry(string path, string content)
        => RetryFileOperation<object?>(() =>
        {
            File.WriteAllText(path, content);
            return null;
        });

    private static T RetryFileOperation<T>(Func<T> operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return operation();
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
        }
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
