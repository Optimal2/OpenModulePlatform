using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
        await HostAgentDeploymentLockLease.TryClearExpiredLockAsync(
            _root, "stale-lock-id", NullLogger.Instance, CancellationToken.None);

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

        await HostAgentDeploymentLockLease.TryClearExpiredLockAsync(
            _root, "stale-lock-id", NullLogger.Instance, CancellationToken.None);

        Assert.False(File.Exists(DeploymentLockFile.GetPath(_root)));
    }

    /// <summary>
    /// A claim that replaces the expired document while the clear is still in flight must
    /// survive: only the expired document whose ownership was verified may be deleted,
    /// and the verification and the deletion must be one atomic step.
    /// </summary>
    /// <remarks>
    /// The test handle shares the file for READS and DELETES but not for writes. A
    /// status read through it succeeds, and a path-based File.Delete also succeeds --
    /// the file goes delete-pending and is unlinked when this handle closes, so the
    /// competitor claim written through the handle AFTER the clear returns is destroyed
    /// by a delete that was issued against the expired document. That is exactly the
    /// double-deployment interleaving: two agents verify the same expired lock, one
    /// clears and claims, the other's already-issued delete removes the fresh claim.
    /// An atomic compare-and-delete cannot do this: its exclusive open is blocked
    /// behind this handle (writes are not shared), it reports the file as busy, and
    /// nothing is deleted.
    ///
    /// RED against the read-then-delete-by-path pattern by construction -- the sharing
    /// modes force the ordering, no timing is involved.
    /// </remarks>
    [Fact]
    public async Task TryClearExpiredLock_AClaimThatReplacedTheExpiredDocumentBeforeTheDeleteLanded_Survives()
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
        var path = DeploymentLockFile.GetPath(_root);

        using (var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete))
        {
            await HostAgentDeploymentLockLease.TryClearExpiredLockAsync(
                _root, "stale-lock-id", NullLogger.Instance, CancellationToken.None);

            // The competitor's claim lands while the clearer still believes the expired
            // document is gone.
            var competitor = DeploymentLockFile.Create(
                "competitor-lock-id",
                "app-key",
                "other-owner",
                "deploying",
                now,
                now.AddMinutes(5));
            var json = JsonSerializer.Serialize(
                competitor,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            handle.SetLength(0);
            handle.Position = 0;
            handle.Write(Encoding.UTF8.GetBytes(json));
            handle.Flush();
        }

        Assert.True(File.Exists(path));
        Assert.Equal(
            "competitor-lock-id",
            DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow).Document!.LockId);
    }

    /// <summary>
    /// A claim that lands while DisposeAsync is deciding to delete must survive: the
    /// ownership check and the delete must be one atomic step, or disposal destroys a
    /// lock somebody else took over in between.
    /// </summary>
    /// <remarks>
    /// Same construction as the expired-clear test above: the held handle lets the
    /// disposal's ownership read and a path-based delete both succeed, and the foreign
    /// claim written through it afterwards is then unlinked by the delete that was
    /// issued against this lease's own document. An atomic compare-and-delete instead
    /// finds the file busy, deletes nothing, and the foreign claim survives.
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_AClaimThatLandedBetweenTheOwnershipCheckAndTheDelete_Survives()
    {
        var result = await HostAgentDeploymentLockLease.TryAcquireAsync(
            _root,
            "app-key",
            "owner",
            "reason",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            maxConsecutiveIndeterminateReads: 100,
            NullLogger.Instance,
            CancellationToken.None);
        Assert.NotNull(result.Lease);
        var path = DeploymentLockFile.GetPath(_root);

        using (var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete))
        {
            await result.Lease!.DisposeAsync();

            var foreign = DeploymentLockFile.Create(
                "foreign-lock-id",
                "app-key",
                "other-owner",
                "deploying",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5));
            var json = JsonSerializer.Serialize(
                foreign,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            handle.SetLength(0);
            handle.Position = 0;
            handle.Write(Encoding.UTF8.GetBytes(json));
            handle.Flush();
        }

        Assert.True(File.Exists(path));
        Assert.Equal(
            "foreign-lock-id",
            DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow).Document!.LockId);
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
    /// FileShare.ReadWrite, deliberately WITHOUT FileShare.Delete -- and injects the foreign
    /// claim into the window where the old loop's File.Move retry is blocked behind it.
    ///
    /// The injection is gated on a deterministic barrier, not on a number of milliseconds.
    /// The old two-step loop writes through WriteAsync's temp+Move, so a tick that has
    /// passed its ownership read and entered the write phase leaves its temp file
    /// (".*.tmp") in App_Data for as long as the Move retries behind the test handle --
    /// and observing that file PROVES the ownership read already happened, because the
    /// write phase is only entered after it. On the old code the foreign claim below then
    /// necessarily lands between the read and the overwrite, the stuck Move waits the test
    /// handle out and replaces the claim, and the final assertion fails: this test is RED
    /// against the two-step pattern by construction, not by scheduling luck (measured: the
    /// pre-4f45aee1 loop fails the ownership assertion). The atomic renewal writes in
    /// place and never creates a temp file, so the barrier simply expires; its exclusive
    /// open retries behind the test handle and reads the foreign claim on the first open
    /// after release.
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
        var path = DeploymentLockFile.GetPath(_root);
        var directory = Path.GetDirectoryName(path)!;

        // Barrier one: the renewal loop proves it is alive and ticking by completing a
        // renewal before the test interferes. Without this, everything below rests on the
        // assumption that a loop is running at all. A renewal holds its exclusive handle
        // for a moment, which fails a colliding ReadStatus closed with a null Document --
        // the condition simply waits that out.
        var acquiredUpdatedUtc = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow).Document!.UpdatedUtc;
        var loopAlive = await WaitUntilAsync(() =>
            DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow).Document is { } document
            && document.UpdatedUtc > acquiredUpdatedUtc);
        Assert.True(
            loopAlive,
            "Renewal never completed a first tick; the test cannot prove anything about the interleaving.");

        var now = DateTimeOffset.UtcNow;
        var foreign = DeploymentLockFile.Create(
            "foreign-lock-id",
            "app-key",
            "other-owner",
            "deploying",
            now,
            now.AddMinutes(5));

        // Hold the file so a status READ still succeeds but no temp+Move overwrite can
        // land. The open itself is retried for the same reason ReadWithRetry/WriteWithRetry
        // exist: a renewal tick holding its own exclusive handle in this microsecond window
        // is a collision the test is not about.
        using (var handle = RetryFileOperation(() =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        {
            // Barrier two (the deterministic one): wait for the old pattern's write-phase
            // temp file. On the two-step code it appears within one tick (~50 ms) of this
            // handle landing, so a three-second wait cannot miss it; on the atomic code it
            // can never appear, and the expired wait costs the test nothing but time --
            // the renewal's exclusive open is blocked behind this handle either way, so it
            // cannot have read anything before the injection below.
            await WaitUntilAsync(
                () => Directory.EnumerateFiles(directory, ".*.tmp").Any(),
                TimeSpan.FromSeconds(3));

            var json = JsonSerializer.Serialize(
                foreign,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            WriteWithRetry(path, json);

            // On the old pattern the handle must go back well inside WriteAsync's ~500 ms
            // Move-retry budget so the stuck tick can complete its overwrite: the temp file
            // is detected within one 25 ms poll of being created, and the injection plus
            // the release take microseconds, so several retry attempts always remain.
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

    /// <summary>
    /// A re-assert that fails with an I/O error counts against the same bound as an
    /// unreadable lock file: renewal stops instead of looping forever with the deployment
    /// left unlocked.
    /// </summary>
    /// <remarks>
    /// A directory at the lock file's path makes the fault deterministic and persistent:
    /// File.Exists is false for a directory, so every tick sees NotFound and tries to
    /// re-assert, and FileMode.CreateNew on a directory path throws
    /// UnauthorizedAccessException every single time. No timing decides WHAT the loop
    /// sees, and the barrier below -- the bound-hit error in the captured log -- decides
    /// WHEN the test proceeds, so a slow scheduler can only make the test slower, never
    /// greener. Against the old behaviour (a failed re-assert logged and forgotten, the
    /// counter untouched) the loop never stops, the barrier never fires, and the test is
    /// RED.
    /// </remarks>
    [Fact]
    public async Task Renewal_CountsFailedReassertsAgainstTheBoundAndStops()
    {
        var logger = new ListLogger();
        var result = await HostAgentDeploymentLockLease.TryAcquireAsync(
            _root,
            "app-key",
            "owner",
            "reason",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(50),
            maxConsecutiveIndeterminateReads: 2,
            logger,
            CancellationToken.None);

        Assert.NotNull(result.Lease);
        var path = DeploymentLockFile.GetPath(_root);

        // Replace the lock file with a directory at its path. A tick can re-create the
        // file in the microseconds between the delete and the CreateDirectory, so the pair
        // runs as one retried operation that converges on the directory.
        RetryFileOperation<object?>(() =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return null;
        });

        // Deterministic barrier: this error is logged exactly when the bounded run of
        // failed re-asserts stops the loop.
        var stopped = await WaitUntilAsync(() =>
            logger.Messages.Any(m => m.Contains("could not be re-asserted", StringComparison.Ordinal)));
        Assert.True(
            stopped,
            "Renewal never stopped: failed re-asserts were not counted against the bound.");

        // A loop that was still alive would re-assert the lock on a later tick once the
        // fault is gone; the stopped loop never touches the path again.
        Directory.Delete(path);
        var resurrected = await WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(2));
        Assert.False(resurrected, "Renewal resumed after the bounded run of failed re-asserts.");

        await result.Lease!.DisposeAsync();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
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
    /// Captures formatted log messages so a test can wait for a specific entry -- a
    /// synchronisation guarantee -- instead of for a number of milliseconds.
    /// </summary>
    private sealed class ListLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _messages.Enqueue(formatter(state, exception));
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
