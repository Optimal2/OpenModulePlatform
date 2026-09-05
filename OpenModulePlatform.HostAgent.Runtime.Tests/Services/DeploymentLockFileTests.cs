using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
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
    /// A dispose-time CancelAsync lands mid-renewal with the token already cancelled when
    /// the write phase begins. The write phase must not take the token past the truncation
    /// point: cancelling between SetLength(0) and the flush would leave an empty lock file
    /// that every reader then fails closed on until someone deletes it by hand.
    /// </summary>
    [Fact]
    public async Task TryRenewExclusiveAsync_CancellationAtWritePhase_LeavesTheLockFileValidAndParseable()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(DeploymentLockFile.GetPath(_root));

        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeploymentLockFile.TryRenewExclusiveAsync(
                _root,
                "lock-id",
                current =>
                {
                    // Fires between the ownership check and the write phase -- exactly where
                    // DisposeAsync's CancelAsync lands when it interrupts a renewal there.
                    cts.Cancel();
                    return current with { UpdatedUtc = now.AddSeconds(30) };
                },
                cts.Token));

        // Whatever the write phase did, the file afterwards is still a valid, parseable
        // lock document -- never an empty or half-written one.
        var status = DeploymentLockFile.ReadStatus(_root, now);
        Assert.Equal("lock-id", status.Document?.LockId);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(DeploymentLockFile.GetPath(_root)));
    }

    /// <summary>
    /// The renewal's write phase fails closed too: an I/O error while the renewed document
    /// is written comes back as a diagnostic the caller reports as Indeterminate, never as
    /// an exception -- an unhandled fault there killed the renewal loop on the first
    /// transient blow, with the deployment it protected still running.
    /// </summary>
    [Fact]
    public async Task TryRewriteHeldContentAsync_WhenTheWriteFails_ReturnsADiagnosticInsteadOfThrowing()
    {
        await using var stream = new ThrowingWriteStream();

        var diagnostic = await DeploymentLockFile.TryRewriteHeldContentAsync(stream, "{}");

        Assert.NotNull(diagnostic);
        Assert.Contains("could not be written", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryRewriteHeldContentAsync_WhenTheWriteSucceeds_ReturnsNullAndLeavesTheNewContent()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("old content that is longer than the new"));

        var diagnostic = await DeploymentLockFile.TryRewriteHeldContentAsync(stream, "new");

        Assert.Null(diagnostic);
        Assert.Equal("new", Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>
    /// A stream whose write phase fails the way a full disk or a vanished share fails the
    /// real handle: the truncation itself already throws.
    /// </summary>
    private sealed class ThrowingWriteStream : MemoryStream
    {
        public override void SetLength(long value) => throw new IOException("simulated write failure");
    }

    [Fact]
    public async Task TryDeleteIfOwnedExclusiveAsync_WhenOwned_DeletesTheFile()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);

        var outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
            _root, "lock-id", deletionRequirement: null, CancellationToken.None);

        Assert.Equal(DeploymentLockDeleteResult.Deleted, outcome.Result);
        Assert.Equal("lock-id", outcome.Document!.LockId);
        Assert.False(File.Exists(DeploymentLockFile.GetPath(_root)));
    }

    [Fact]
    public async Task TryDeleteIfOwnedExclusiveAsync_WhenOwnedByAnotherClaimant_ReturnsNotOwnedAndKeepsTheFile()
    {
        var now = DateTimeOffset.UtcNow;
        var foreign = DeploymentLockFile.Create("foreign-lock-id", "app-key", "HostAgent", "deploying", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, foreign, CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(DeploymentLockFile.GetPath(_root));

        var outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
            _root, "our-lock-id", deletionRequirement: null, CancellationToken.None);

        Assert.Equal(DeploymentLockDeleteResult.NotOwned, outcome.Result);
        Assert.Equal("foreign-lock-id", outcome.Document!.LockId);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(DeploymentLockFile.GetPath(_root)));
    }

    [Fact]
    public async Task TryDeleteIfOwnedExclusiveAsync_MissingFile_ReturnsNotFound()
    {
        var outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
            _root, "lock-id", deletionRequirement: null, CancellationToken.None);

        Assert.Equal(DeploymentLockDeleteResult.NotFound, outcome.Result);
        Assert.Null(outcome.Document);
    }

    [Fact]
    public async Task TryDeleteIfOwnedExclusiveAsync_ZeroByteFile_IsTreatedAsAbsentAndLeftAlone()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());

        var outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
            _root, "lock-id", deletionRequirement: null, CancellationToken.None);

        Assert.Equal(DeploymentLockDeleteResult.NotFound, outcome.Result);
        // The residue is left for the zero-byte takeover path, not deleted here.
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// The expiry requirement runs inside the exclusive handle: a lock its owner renewed
    /// meanwhile (same LockId, future expiry) is a live lock again and is left alone.
    /// </summary>
    [Fact]
    public async Task TryDeleteIfOwnedExclusiveAsync_WhenTheRequirementNoLongerHolds_LeavesTheFile()
    {
        var now = DateTimeOffset.UtcNow;
        var renewed = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, renewed, CancellationToken.None);

        var outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
            _root,
            "lock-id",
            document => document.ExpiresUtc <= DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(DeploymentLockDeleteResult.NotOwned, outcome.Result);
        Assert.True(File.Exists(DeploymentLockFile.GetPath(_root)));
    }

    [Fact]
    public async Task TryDeleteIfOwnedExclusiveAsync_WhenTheRequirementHolds_DeletesTheFile()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now.AddMinutes(-30), now.AddMinutes(-10));
        await DeploymentLockFile.WriteAsync(_root, expired, CancellationToken.None);

        var outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
            _root,
            "lock-id",
            document => document.ExpiresUtc <= DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(DeploymentLockDeleteResult.Deleted, outcome.Result);
        Assert.False(File.Exists(DeploymentLockFile.GetPath(_root)));
    }

    /// <summary>
    /// The atomicity guarantee, pinned at its sharpest point: the deletion requirement
    /// runs inside the exclusive handle immediately after the ownership read -- the exact
    /// instant the old read-then-delete-by-path pattern let a foreign claim in -- and an
    /// actor that KNOWS the read already happened still cannot get a claim onto the file
    /// before the deletion. Against the two-step pattern this write succeeds and the
    /// subsequent path-based delete removes the foreign claim: this test is RED there.
    /// </summary>
    [Fact]
    public async Task TryDeleteIfOwnedExclusiveAsync_AClaimAttemptedAtTheMomentOfVerification_CannotInterpose()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now.AddMinutes(-30), now.AddMinutes(-10));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var path = DeploymentLockFile.GetPath(_root);

        var foreign = DeploymentLockFile.Create("foreign-lock-id", "app-key", "HostAgent", "deploying", now, now.AddMinutes(5));
        var foreignJson = JsonSerializer.Serialize(foreign, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

        var requirementRan = false;
        var interpositionBlocked = false;
        var outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
            _root,
            "lock-id",
            document =>
            {
                requirementRan = true;
                try
                {
                    File.WriteAllText(path, foreignJson);
                }
                catch (IOException)
                {
                    // The exclusive handle is held: no write can interpose between the
                    // verification and the deletion it armed.
                    interpositionBlocked = true;
                }

                return true;
            },
            CancellationToken.None);

        Assert.True(requirementRan, "the deletion requirement never ran -- the test proves nothing");
        Assert.True(interpositionBlocked, "a foreign claim could be written between verification and deletion");
        Assert.Equal(DeploymentLockDeleteResult.Deleted, outcome.Result);
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// A foreign exclusive handle blocks the compare-and-delete's open for as long as it
    /// is held: the attempt reports Indeterminate and deletes nothing.
    /// </summary>
    [Fact]
    public async Task TryDeleteIfOwnedExclusiveAsync_WhileHeldExclusively_DeletesNothingAndReportsIndeterminate()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now.AddMinutes(-30), now.AddMinutes(-10));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var path = DeploymentLockFile.GetPath(_root);

        DeploymentLockDeleteOutcome outcome;
        await using (var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
                _root, "lock-id", deletionRequirement: null, CancellationToken.None);
        }

        Assert.Equal(DeploymentLockDeleteResult.Indeterminate, outcome.Result);
        Assert.True(File.Exists(path));
        Assert.Equal("lock-id", DeploymentLockFile.ReadStatus(_root, now).Document!.LockId);
    }

    /// <summary>
    /// ReadStatus must NEVER throw: eight call sites (the HostAgent lease loop, both deployment
    /// services, the health monitor) call it with no try/catch and treat the returned status as
    /// the whole answer. An exception there kills a renewal loop mid-deployment.
    ///
    /// The zero-byte probe sits OUTSIDE the try block, so the pattern it uses decides whether
    /// ReadStatus can throw. Measured under an identical churn loop: File.Exists followed by a
    /// FRESH FileInfo threw in 373 of 1348 iterations, while a single FileInfo whose Exists is
    /// read before Length threw 0 times -- FileInfo caches its stat on first property access, so
    /// the pair becomes one filesystem query and the race is removed rather than caught.
    ///
    /// This test is the guard for that. It churns the lock file in and out of existence while
    /// reading it, and requires BOTH that the race was actually exercised (the reader saw the
    /// file present and absent) and that nothing was thrown -- a green result where the race
    /// never happened would prove nothing, so that case fails loudly instead. The reader waits
    /// for the churn thread's first write before it starts counting, so "exercised" is a
    /// synchronisation guarantee rather than a bet on the scheduler.
    /// </summary>
    [Fact]
    public void ReadStatus_FileVanishingDuringTheProbe_DoesNotThrow()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var now = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(
            DeploymentLockFile.Create("id", "app", "owner", "reason", now, now.AddMinutes(5)));

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // The churn thread signals that it has actually written once. Without it, "the race was
        // exercised" is a probability: 2000 iterations of the absent-file path take ~94 ms, so a
        // loaded runner that does not schedule the churn thread inside that window would fail a
        // correct tree. Waiting on the signal makes the guarantee deterministic.
        using var churning = new ManualResetEventSlim(false);
        var churn = Task.Run(() =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    File.WriteAllText(path, json, Encoding.UTF8);
                    churning.Set();
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The reader may hold the file for an instant; not what is being measured.
                    // Catching BOTH matters: an unhandled exception here faults the task, and
                    // churn.Wait() in the finally below would then throw an AggregateException
                    // that masks the assertions this test exists to report.
                }
            }
        });
        Assert.True(churning.Wait(TimeSpan.FromSeconds(10)),
            "the churn thread never wrote the file -- the race could not be exercised");

        Exception? observed = null;
        var sawLocked = false;
        var sawUnlocked = false;
        var iterations = 0;
        try
        {
            // Enough iterations that a ~28 % per-iteration hit rate makes a miss impossible in
            // practice, but bounded so a fixed build finishes in well under a second.
            while (iterations < 2000 && !stopping.IsCancellationRequested)
            {
                iterations++;
                try
                {
                    var status = DeploymentLockFile.ReadStatus(_root, now);
                    if (status.IsLocked) { sawLocked = true; } else { sawUnlocked = true; }
                }
                catch (Exception ex)
                {
                    observed = ex;
                    break;
                }
            }
        }
        finally
        {
            stopping.Cancel();
            churn.Wait(TimeSpan.FromSeconds(5));
        }

        Assert.True(observed is null,
            $"ReadStatus threw {observed?.GetType().Name} after {iterations} iterations: " +
            $"{observed?.Message}");
        Assert.True(sawLocked && sawUnlocked,
            $"the race was never exercised in {iterations} iterations " +
            $"(locked={sawLocked}, unlocked={sawUnlocked}) -- this green proves nothing");
    }

    /// <summary>
    /// A zero-byte lock file is the residue of an interrupted claim or renewal (R12-A4
    /// follow-up). It can never be a valid claim, so it reads as "no lock" and a new
    /// claimant can take the file over instead of being permanently locked out.
    /// </summary>
    [Fact]
    public async Task ReadStatus_ZeroByteFile_IsNotLockedAndANewClaimantCanTakeOver()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());

        var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);

        Assert.False(status.IsLocked);
        Assert.False(status.IsExpired);
        Assert.Null(status.Document);

        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("new-lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        Assert.True(await DeploymentLockFile.TryCreateExclusiveAsync(_root, doc, CancellationToken.None));
        Assert.Equal("new-lock-id", DeploymentLockFile.ReadStatus(_root, now).Document?.LockId);
    }

    /// <summary>
    /// A NON-EMPTY but unparseable lock file still fails closed (R12-A4's net): it reports
    /// locked, and the zero-byte takeover must not touch it.
    /// </summary>
    [Fact]
    public async Task ReadStatus_NonEmptyUnparseableFile_StillFailsClosedAndBlocksTakeover()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ this is not a lock document");

        var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);

        Assert.True(status.IsLocked);
        Assert.Null(status.Document);
        Assert.NotNull(status.Diagnostic);

        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("new-lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        Assert.False(await DeploymentLockFile.TryCreateExclusiveAsync(_root, doc, CancellationToken.None));
    }

    [Fact]
    public async Task TryRenewExclusiveAsync_ZeroByteFile_IsTreatedAsAbsent()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());

        var outcome = await DeploymentLockFile.TryRenewExclusiveAsync(
            _root,
            "lock-id",
            current => current,
            CancellationToken.None);

        Assert.Equal(DeploymentLockRenewalResult.NotFound, outcome.Result);
        Assert.Null(outcome.Document);
    }

    /// <summary>
    /// A1: when the truncation itself fails, the file still holds the intact document the
    /// renewal just proved is ours -- it must NOT be deleted. Deleting it turned one
    /// transient write fault into a lock-less gap of up to a renewal interval (30 s in
    /// production): the lease's next tick would have retried against an intact file, but
    /// with the file gone a competitor could claim in between and two deployments ran.
    /// </summary>
    [Fact]
    public async Task TryRenewExclusiveAsync_WhenTruncationItselfFails_LeavesTheIntactDocumentInPlace()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var path = DeploymentLockFile.GetPath(_root);
        var originalBytes = await File.ReadAllBytesAsync(path);

        var outcome = await DeploymentLockFile.TryRenewExclusiveAsync(
            _root,
            "lock-id",
            current => current with { UpdatedUtc = now.AddSeconds(30) },
            (p, ct) => Task.FromResult<FileStream?>(new TruncationThrowingFileStream(p)),
            CancellationToken.None);

        Assert.Equal(DeploymentLockRenewalResult.Indeterminate, outcome.Result);
        Assert.True(File.Exists(path));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.Equal("lock-id", DeploymentLockFile.ReadStatus(_root, now).Document!.LockId);
    }

    /// <summary>
    /// A2 (mechanism): when the write fails AFTER the truncation, what is on disk is
    /// residue and must be deleted -- but the deletion rides the handle that proved
    /// ownership: armed with SetFileInformationByHandle while the handle is still held,
    /// completed by its close. A path-based delete after the handle is released can land
    /// on a claim that arrived in between, and is gone from the design.
    /// </summary>
    [Fact]
    public async Task TryRewriteHeldContentAsync_WhenTheWriteFailsAfterTruncation_TheResidueDeletionRidesTheHeldHandle()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var path = DeploymentLockFile.GetPath(_root);

        var stream = new WriteThrowingFileStream(DeploymentLockFile.OpenExclusiveHandleWithDeleteAccess(path));
        var diagnostic = await DeploymentLockFile.TryRewriteHeldContentAsync(stream, "{}");

        Assert.NotNull(diagnostic);

        await stream.DisposeAsync();
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// A2 (end to end): a failed renewal write deletes its residue through the handle that
    /// proved ownership, and a claim that lands at the path afterwards is a new file that
    /// nothing deletes.
    /// </summary>
    [Fact]
    public async Task TryRenewExclusiveAsync_WhenTheWriteFailsAfterTruncation_DeletesTheResidueAndNeverTouchesLaterClaims()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var path = DeploymentLockFile.GetPath(_root);

        var outcome = await DeploymentLockFile.TryRenewExclusiveAsync(
            _root,
            "lock-id",
            current => current with { UpdatedUtc = now.AddSeconds(30) },
            (p, ct) => Task.FromResult<FileStream?>(
                new WriteThrowingFileStream(DeploymentLockFile.OpenExclusiveHandleWithDeleteAccess(p))),
            CancellationToken.None);

        Assert.Equal(DeploymentLockRenewalResult.Indeterminate, outcome.Result);
        Assert.False(File.Exists(path));

        var claim = DeploymentLockFile.Create("new-claim-id", "app-key", "other-owner", "deploying", now, now.AddMinutes(5));
        Assert.True(await DeploymentLockFile.TryCreateExclusiveAsync(_root, claim, CancellationToken.None));
        Assert.Equal("new-claim-id", DeploymentLockFile.ReadStatus(_root, now).Document!.LockId);
    }

    /// <summary>
    /// A stream whose truncation fails the way a genuinely broken handle fails it: the
    /// write phase never starts, and the content on disk is untouched.
    /// </summary>
    private sealed class TruncationThrowingFileStream : FileStream
    {
        public TruncationThrowingFileStream(string path)
            : base(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
        {
        }

        public override void SetLength(long value) => throw new IOException("simulated truncation failure");
    }

    /// <summary>
    /// A stream whose truncation succeeds but whose writes then fail: what is on disk is
    /// truncated residue. The handle comes from the production exclusive open so it
    /// carries the DELETE access the delete-on-close arming needs.
    /// </summary>
    private sealed class WriteThrowingFileStream : FileStream
    {
        public WriteThrowingFileStream(SafeFileHandle handle)
            : base(handle, FileAccess.ReadWrite)
        {
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException(new IOException("simulated write failure"));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("simulated write failure"));
    }

    /// <summary>
    /// The ~500-byte renewal write sits in the FileStream buffer until the flush, so the
    /// real "write fails after truncation" shape is: SetLength succeeds, WriteAsync
    /// succeeds (buffered), FlushAsync throws -- and the disposal then retries the write
    /// and throws the same error AGAIN. The renewal contract says I/O failures are
    /// reported as Indeterminate, never thrown, so the disposal's failure must be
    /// swallowed by the method that already reported it.
    /// </summary>
    [Fact]
    public async Task TryRenewExclusiveAsync_WhenTheFlushFailsAfterABufferedWrite_ReportsIndeterminateWithoutThrowing()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var path = DeploymentLockFile.GetPath(_root);

        var outcome = await DeploymentLockFile.TryRenewExclusiveAsync(
            _root,
            "lock-id",
            current => current with { UpdatedUtc = now.AddSeconds(30) },
            (p, ct) => Task.FromResult<FileStream?>(
                new FlushThrowingFileStream(DeploymentLockFile.OpenExclusiveHandleWithDeleteAccess(p))),
            CancellationToken.None);

        Assert.Equal(DeploymentLockRenewalResult.Indeterminate, outcome.Result);
        Assert.Contains("could not be written", outcome.Diagnostic);
        // The truncation ran, so what the flush failure left was residue: it was armed
        // for deletion on the close of the handle that proved ownership.
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// A claim whose write fails must clean up its own residue through the handle that
    /// created it: armed for deletion while still held, completed by its close. The
    /// original error propagates -- the claim failed -- but no half-written lock file
    /// is left behind to block every later deployment until handled by hand.
    /// </summary>
    [Fact]
    public async Task TryCreateExclusiveAsync_WhenTheClaimWriteFails_TheResidueIsDeletedByTheCreatingHandle()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("claim-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        var path = DeploymentLockFile.GetPath(_root);

        await Assert.ThrowsAsync<IOException>(() => DeploymentLockFile.TryCreateExclusiveAsync(
            _root,
            doc,
            (p, disposition) => new WriteThrowingFileStream(
                DeploymentLockFile.OpenClaimHandleWithDeleteAccess(p, disposition)),
            CancellationToken.None));

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// When the delete-on-close arming itself fails, the cleanup falls back to the
    /// compare-and-delete primitive with the claim's own LockId -- still no path-based
    /// delete without proven ownership. The write failed before a byte landed, so what
    /// remains is a zero-byte residue the primitive deliberately leaves: every reader
    /// treats it as absent and the takeover path claims it on the next attempt.
    /// </summary>
    [Fact]
    public async Task TryCreateExclusiveAsync_WhenTheArmingFails_TheFallbackLeavesTheZeroByteResidueForTheTakeover()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("claim-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        var path = DeploymentLockFile.GetPath(_root);

        await Assert.ThrowsAsync<IOException>(() => DeploymentLockFile.TryCreateExclusiveAsync(
            _root,
            doc,
            (p, disposition) => new UnarmableWriteThrowingFileStream(
                DeploymentLockFile.OpenClaimHandleWithDeleteAccess(p, disposition)),
            CancellationToken.None));

        Assert.True(File.Exists(path));
        Assert.Equal(0, new FileInfo(path).Length);

        var retry = DeploymentLockFile.Create("retry-claim-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        Assert.True(await DeploymentLockFile.TryCreateExclusiveAsync(_root, retry, CancellationToken.None));
        Assert.Equal("retry-claim-id", DeploymentLockFile.ReadStatus(_root, now).Document!.LockId);
    }

    /// <summary>
    /// A file marked for deletion on close answers CreateFile with ERROR_ACCESS_DENIED,
    /// not a sharing violation, until the last handle closes -- a window with no fixed
    /// size (UNC round-trips, antivirus and filter drivers can stretch it well beyond
    /// microseconds). A claim that lands in that window has lost the race: it must
    /// return false, never throw UnauthorizedAccessException out of the acquire path.
    /// </summary>
    [Fact]
    public async Task TryCreateExclusiveAsync_WhenTheFileIsDeletePending_LosesTheRaceInsteadOfThrowing()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var path = DeploymentLockFile.GetPath(_root);

        await using (var holder = new FileStream(
            DeploymentLockFile.OpenExclusiveHandleWithDeleteAccess(path), FileAccess.ReadWrite))
        {
            Assert.True(DeploymentLockFile.TryArmDeleteOnClose(holder, out _));

            var claim = DeploymentLockFile.Create("new-claim-id", "app-key", "other", "deploying", now, now.AddMinutes(5));
            Assert.False(await DeploymentLockFile.TryCreateExclusiveAsync(_root, claim, CancellationToken.None));
        }

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// A REAL access denial is not a lost race. A lock file the process may not open --
    /// here a read-only file, standing in for a wrong service account or a missing
    /// DELETE right; all three surface as the same ERROR_ACCESS_DENIED from CreateFile --
    /// must be reported as the permission problem it is. Before the fix this attempt
    /// returned false, and both acquire callers translated that into "Another deployment
    /// claimed the lock first", sending the operator hunting for a competing deployment
    /// that does not exist (commit bfe824ee folded every ACCESS_DENIED into the lost-race
    /// branch because a delete-pending file answers CreateFile the same way).
    /// </summary>
    [Fact]
    public async Task TryCreateExclusiveAsync_WhenTheExistingFileDeniesAccess_ReportsTheDenialInsteadOfALostRace()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var path = DeploymentLockFile.GetPath(_root);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var claim = DeploymentLockFile.Create("new-claim-id", "app-key", "other", "deploying", now, now.AddMinutes(5));
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => DeploymentLockFile.TryCreateExclusiveAsync(_root, claim, CancellationToken.None));
            Assert.Contains("denied", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        // Fail-closed: nothing was claimed and the existing lock file is untouched.
        Assert.Equal("lock-id", DeploymentLockFile.ReadStatus(_root, now).Document!.LockId);
    }

    /// <summary>
    /// Same distinction on the renewal's exclusive open: a real denial must surface as
    /// "access denied", not as "stayed exclusively locked by another process" after the
    /// retry budget -- a permission problem does not clear inside 500 ms, and the wrong
    /// message starts the diagnosis at a competing process that does not exist.
    /// </summary>
    [Fact]
    public async Task TryRenewExclusiveAsync_WhenTheFileDeniesAccess_ReportsTheDenialInsteadOfLockedByAnotherProcess()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var path = DeploymentLockFile.GetPath(_root);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var outcome = await DeploymentLockFile.TryRenewExclusiveAsync(
                _root,
                "lock-id",
                current => current with { UpdatedUtc = now.AddSeconds(30) },
                CancellationToken.None);

            // Still fail-closed (Indeterminate, not Lost and not Renewed) -- only the
            // diagnosis changes.
            Assert.Equal(DeploymentLockRenewalResult.Indeterminate, outcome.Result);
            Assert.NotNull(outcome.Diagnostic);
            Assert.Contains("denied", outcome.Diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("another process", outcome.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Equal("lock-id", DeploymentLockFile.ReadStatus(_root, now).Document!.LockId);
    }

    /// <summary>
    /// Same distinction on the compare-and-delete's exclusive open, which shares the
    /// retrying opener with the renewal.
    /// </summary>
    [Fact]
    public async Task TryDeleteIfOwnedExclusiveAsync_WhenTheFileDeniesAccess_ReportsTheDenialInsteadOfLockedByAnotherProcess()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now.AddMinutes(-30), now.AddMinutes(-10));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var path = DeploymentLockFile.GetPath(_root);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
                _root, "lock-id", deletionRequirement: null, CancellationToken.None);

            Assert.Equal(DeploymentLockDeleteResult.Indeterminate, outcome.Result);
            Assert.NotNull(outcome.Diagnostic);
            Assert.Contains("denied", outcome.Diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("another process", outcome.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        // Fail-closed: the file could not be proven ours, so it was left in place.
        Assert.True(File.Exists(path));
        Assert.Equal("lock-id", DeploymentLockFile.ReadStatus(_root, now).Document!.LockId);
    }

    /// <summary>
    /// The regression guard for the other half of the distinction: a delete-pending
    /// file (marked for deletion on close, not yet unlinked) answers CreateFile with
    /// the same ERROR_ACCESS_DENIED as a real ACL denial, and it must STILL be treated
    /// as a lost race -- retried within the bounded budget, then reported as a lock
    /// held by another process -- otherwise the fix has only swapped one wrong message
    /// for another. Pairs with the claim-path guard
    /// <see cref="TryCreateExclusiveAsync_WhenTheFileIsDeletePending_LosesTheRaceInsteadOfThrowing"/>.
    /// </summary>
    [Fact]
    public async Task TryDeleteIfOwnedExclusiveAsync_WhenTheFileIsDeletePending_StillReportsTheLostRace()
    {
        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("lock-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, doc, CancellationToken.None);
        var path = DeploymentLockFile.GetPath(_root);

        DeploymentLockDeleteOutcome outcome;
        await using (var holder = new FileStream(
            DeploymentLockFile.OpenExclusiveHandleWithDeleteAccess(path), FileAccess.ReadWrite))
        {
            Assert.True(DeploymentLockFile.TryArmDeleteOnClose(holder, out _));

            outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
                _root, "lock-id", deletionRequirement: null, CancellationToken.None);
        }

        Assert.Equal(DeploymentLockDeleteResult.Indeterminate, outcome.Result);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains("stayed exclusively locked by another process", outcome.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// A stream whose buffered write succeeds but whose flush fails -- the real shape of
    /// "write fails after truncation" against a FileStream with a buffer -- and whose
    /// disposal then fails the retried write again, the way a genuinely broken device
    /// fails it. The handle still closes, so an armed deletion completes.
    /// </summary>
    private sealed class FlushThrowingFileStream : FileStream
    {
        public FlushThrowingFileStream(SafeFileHandle handle)
            : base(handle, FileAccess.ReadWrite)
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.FromException(new IOException("simulated flush failure"));

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            catch (IOException)
            {
                // The dirty buffer's flush hits the same failing device; close anyway so
                // an armed deletion completes, the way a real handle close would.
                Dispose();
            }

            throw new IOException("simulated dispose flush failure");
        }
    }

    /// <summary>
    /// A stream whose write fails and whose reported safe handle is unusable, so the
    /// delete-on-close arming fails too and the claim cleanup has to take its fallback.
    /// </summary>
    private sealed class UnarmableWriteThrowingFileStream : FileStream
    {
        public UnarmableWriteThrowingFileStream(SafeFileHandle handle)
            : base(handle, FileAccess.ReadWrite)
        {
        }

        public override SafeFileHandle SafeFileHandle => new(IntPtr.Zero, ownsHandle: false);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException(new IOException("simulated write failure"));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("simulated write failure"));
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

    /// <summary>
    /// The lock file must be reachable through its UNC form too: an app whose root sits on
    /// a share takes the same lock path as a local one (InstallPath is operator-configured,
    /// and a rooted UNC path passes straight through Path.GetFullPath, so nothing stops it).
    /// The naive "\\?\" + path concatenation turns \\server\share\... into
    /// \\?\\\server\share\..., which is not a valid Win32 path, so every native open fails.
    /// This test was RED before the extended-path mapping existed: the exclusive open threw
    /// an IOException (Win32 error 123, ERROR_INVALID_NAME) instead of opening the file.
    /// </summary>
    [Fact]
    public void OpenExclusiveHandleWithDeleteAccess_UncPath_OpensTheFile()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}", Encoding.UTF8);
        var uncPath = ToLocalhostUncPath(path);
        Assert.True(File.Exists(uncPath),
            $"the localhost admin share form of the temp path is not reachable ({uncPath}) -- this test needs it");

        using var handle = DeploymentLockFile.OpenExclusiveHandleWithDeleteAccess(uncPath);

        Assert.False(handle.IsInvalid);
    }

    /// <summary>
    /// Same UNC requirement end to end on the claim path: a deployment lock under a UNC
    /// application root must be claimable and readable back.
    /// </summary>
    [Fact]
    public async Task TryCreateExclusiveAsync_UncApplicationRoot_ClaimsTheLock()
    {
        var uncRoot = ToLocalhostUncPath(_root);
        Directory.CreateDirectory(uncRoot);
        Assert.True(Directory.Exists(uncRoot),
            $"the localhost admin share form of the temp root is not reachable ({uncRoot}) -- this test needs it");

        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("unc-claim-id", "app-key", "owner", "reason", now, now.AddMinutes(5));

        Assert.True(await DeploymentLockFile.TryCreateExclusiveAsync(uncRoot, doc, CancellationToken.None));
        Assert.Equal("unc-claim-id", DeploymentLockFile.ReadStatus(uncRoot, now).Document!.LockId);
    }

    /// <summary>
    /// A path that already carries the extended-length prefix must not be prefixed a second
    /// time: "\\?\" + "\\?\C:\..." is as invalid as the doubled UNC form. This test was RED
    /// before the mapping existed (Win32 error 123, ERROR_INVALID_NAME).
    /// </summary>
    [Fact]
    public void OpenExclusiveHandleWithDeleteAccess_AlreadyExtendedPath_OpensTheFile()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}", Encoding.UTF8);
        var extendedPath = @"\\?\" + path;

        using var handle = DeploymentLockFile.OpenExclusiveHandleWithDeleteAccess(extendedPath);

        Assert.False(handle.IsInvalid);
    }

    /// <summary>
    /// A local drive-rooted path gets the plain extended-length prefix.
    /// </summary>
    [Fact]
    public void ToExtendedPath_LocalPath_PrependsTheExtendedPrefix()
    {
        Assert.Equal(
            @"\\?\C:\apps\webapp\App_Data\omp-deployment.lock.json",
            DeploymentLockFile.ToExtendedPath(@"C:\apps\webapp\App_Data\omp-deployment.lock.json"));
    }

    /// <summary>
    /// A UNC path must become \\?\UNC\server\share\... -- the naive "\\?\" + path form
    /// (\\?\\\server\share\...) is not a valid Win32 path and fails every native open
    /// with ERROR_INVALID_NAME.
    /// </summary>
    [Fact]
    public void ToExtendedPath_UncPath_UsesTheUncPrefixForm()
    {
        Assert.Equal(
            @"\\?\UNC\server\share\app\App_Data\omp-deployment.lock.json",
            DeploymentLockFile.ToExtendedPath(@"\\server\share\app\App_Data\omp-deployment.lock.json"));
    }

    /// <summary>
    /// A path that already carries the extended-length prefix must not be prefixed again.
    /// </summary>
    [Fact]
    public void ToExtendedPath_AlreadyPrefixedPath_IsLeftAlone()
    {
        Assert.Equal(@"\\?\C:\apps\webapp", DeploymentLockFile.ToExtendedPath(@"\\?\C:\apps\webapp"));
        Assert.Equal(@"\\?\UNC\server\share\app", DeploymentLockFile.ToExtendedPath(@"\\?\UNC\server\share\app"));
    }

    /// <summary>
    /// A device-namespace path (\\.\...) is not a filesystem path for these purposes and
    /// must pass through untouched.
    /// </summary>
    [Fact]
    public void ToExtendedPath_DevicePath_IsLeftAlone()
    {
        Assert.Equal(@"\\.\PhysicalDrive0", DeploymentLockFile.ToExtendedPath(@"\\.\PhysicalDrive0"));
    }

    /// <summary>
    /// Non-normalised input is normalised before prefixing: the extended-length prefix
    /// disables Win32 path normalisation, so a forward-slash or dot-segment path must be
    /// resolved to its full normalised form first.
    /// </summary>
    [Fact]
    public void ToExtendedPath_NonNormalisedLocalPath_IsNormalisedBeforePrefixing()
    {
        Assert.Equal(
            @"\\?\C:\apps\webapp",
            DeploymentLockFile.ToExtendedPath("C:/apps/./webapp"));
    }

    /// <summary>
    /// Platform fact the claim filter's Denied branch relies on: whether File.Exists and
    /// Directory.Exists can still see a delete-pending file (marked for deletion on close,
    /// not yet unlinked). Pinned so a platform change in either answer is caught loudly --
    /// the branch treats "denied probe, file still visible" as a lost race and "denied
    /// probe, nothing visible" as a persistent obstruction.
    /// </summary>
    [Fact]
    public void FileExists_DeletePendingFile_PinsWhatTheFilesystemStillShows()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}", Encoding.UTF8);

        using (var holder = new FileStream(
            DeploymentLockFile.OpenExclusiveHandleWithDeleteAccess(path), FileAccess.ReadWrite))
        {
            Assert.True(DeploymentLockFile.TryArmDeleteOnClose(holder, out _));

            // Pinned observation: on NTFS the name still exists until the last handle
            // closes, so File.Exists stays true for a delete-pending file.
            Assert.True(File.Exists(path), "File.Exists no longer sees a delete-pending file -- review IsLostRaceOpenFailure's Denied branch");
            Assert.False(Directory.Exists(path));
        }

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// Probe state Opened: a file that answers the zero-access probe proves that a denial
    /// from the real open was a REAL permission failure, never a lost race.
    /// </summary>
    [Fact]
    public void ProbeDeletePending_ExistingFile_ReportsOpened()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}", Encoding.UTF8);

        Assert.Equal(DeploymentLockFile.DeletePendingProbeResult.Opened, DeploymentLockFile.ProbeDeletePending(path));
    }

    /// <summary>
    /// Probe state Denied: a file marked for deletion on close refuses even the
    /// zero-access probe with ERROR_ACCESS_DENIED.
    /// </summary>
    [Fact]
    public void ProbeDeletePending_DeletePendingFile_ReportsDenied()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}", Encoding.UTF8);

        using (var holder = new FileStream(
            DeploymentLockFile.OpenExclusiveHandleWithDeleteAccess(path), FileAccess.ReadWrite))
        {
            Assert.True(DeploymentLockFile.TryArmDeleteOnClose(holder, out _));

            Assert.Equal(DeploymentLockFile.DeletePendingProbeResult.Denied, DeploymentLockFile.ProbeDeletePending(path));
        }

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// Probe state Vanished: the file is gone at probe time (ERROR_FILE_NOT_FOUND) -- the
    /// TOCTOU outcome, which callers must treat as a lost race, never as "access denied".
    /// </summary>
    [Fact]
    public void ProbeDeletePending_MissingFile_ReportsVanished()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Assert.Equal(DeploymentLockFile.DeletePendingProbeResult.Vanished, DeploymentLockFile.ProbeDeletePending(path));
    }

    /// <summary>
    /// Probe state Vanished via the directory half: a missing directory above the lock
    /// file answers ERROR_PATH_NOT_FOUND, the same lost-race outcome.
    /// </summary>
    [Fact]
    public void ProbeDeletePending_MissingDirectory_ReportsVanished()
    {
        var path = Path.Join(_root, "no-such-dir", "omp-deployment.lock.json");

        Assert.Equal(DeploymentLockFile.DeletePendingProbeResult.Vanished, DeploymentLockFile.ProbeDeletePending(path));
    }

    /// <summary>
    /// The sharpest form of a real denial: the DELETE right is denied but metadata reads
    /// are allowed. The exclusive open (which needs DELETE) fails while the zero-access
    /// probe still opens the file, so the probe must answer Opened -- the caller reports
    /// a real permission failure, not a lost race.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void ProbeDeletePending_WhenDeleteIsDeniedButMetadataIsReadable_ReportsOpened()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}", Encoding.UTF8);

        // A file-level deny-Delete alone does NOT block a DELETE-access open: the delete
        // right can also come from the parent directory's FILE_DELETE_CHILD, which the
        // temp directory's inherited FullControl grants. Denying the parent's
        // DeleteSubdirectoriesAndFiles as well is what makes the denial real.
        WithDeleteDenied(path, () =>
        {
            Assert.Equal(DeploymentLockFile.DeletePendingProbeResult.Opened, DeploymentLockFile.ProbeDeletePending(path));
            Assert.Throws<UnauthorizedAccessException>(
                () => DeploymentLockFile.OpenExclusiveHandleWithDeleteAccess(path).Dispose());
        });
    }

    /// <summary>
    /// The other named ACL shape: an ACL that refuses even attribute reads makes the
    /// probe itself fail with ERROR_ACCESS_DENIED -- indistinguishable from
    /// delete-pending, so the probe answers Denied and the fail-closed handling is the
    /// same as for a lost race.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void ProbeDeletePending_WhenEvenAttributeReadsAreDenied_ReportsDenied()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}", Encoding.UTF8);

        var file = new FileInfo(path);
        var originalAcl = file.GetAccessControl();
        var modifiedAcl = file.GetAccessControl();
        modifiedAcl.AddAccessRule(DenyRule(FileSystemRights.FullControl));
        file.SetAccessControl(modifiedAcl);
        try
        {
            Assert.Equal(DeploymentLockFile.DeletePendingProbeResult.Denied, DeploymentLockFile.ProbeDeletePending(path));
        }
        finally
        {
            file.SetAccessControl(originalAcl);
        }
    }

    /// <summary>
    /// A directory sitting where the lock file should be is a persistent obstruction, not
    /// a lost race: ReadStatus reports no lock (File.Exists is false for a directory), and
    /// the claim's CreateNew fails with ERROR_ACCESS_DENIED while the zero-access probe is
    /// denied with NOTHING at the path -- so the denial propagates as a fault (in the
    /// lease loop that is what counts against the re-assert bound and stops the renewal)
    /// instead of being misreported as "another deployment claimed the lock first" once
    /// per tick forever. The directory is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task TryCreateExclusiveAsync_WhenADirectorySitsAtTheLockPath_ThrowsAndLeavesItAlone()
    {
        var path = DeploymentLockFile.GetPath(_root);
        Directory.CreateDirectory(path);

        Assert.False(DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow).IsLocked);

        var now = DateTimeOffset.UtcNow;
        var doc = DeploymentLockFile.Create("claim-id", "app-key", "owner", "reason", now, now.AddMinutes(5));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => DeploymentLockFile.TryCreateExclusiveAsync(_root, doc, CancellationToken.None));

        Assert.True(Directory.Exists(path));
    }

    /// <summary>
    /// Denies <paramref name="rights"/> on the file for the current user for the duration
    /// of <paramref name="body"/>, then restores the original ACL so the fixture cleanup
    /// can delete the file. Deny ACEs hold for the file's owner too, which is what makes
    /// the denial deterministic in this test.
    /// </summary>
    /// <summary>
    /// Denies the delete right on the file for the duration of <paramref name="body"/> --
    /// deny-Delete on the file itself AND deny-DeleteSubdirectoriesAndFiles (FILE_DELETE_CHILD)
    /// on its parent directory, because the delete right can come from either -- then
    /// restores both ACLs so the fixture cleanup can delete the tree. Deny ACEs hold for
    /// the file's owner too, which is what makes the denial deterministic in this test.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void WithDeleteDenied(string filePath, Action body)
    {
        var file = new FileInfo(filePath);
        var directory = file.Directory!;
        var originalFileAcl = file.GetAccessControl();
        var originalDirectoryAcl = directory.GetAccessControl();

        var fileAcl = file.GetAccessControl();
        fileAcl.AddAccessRule(DenyRule(FileSystemRights.Delete));
        file.SetAccessControl(fileAcl);
        var directoryAcl = directory.GetAccessControl();
        directoryAcl.AddAccessRule(DenyRule(FileSystemRights.DeleteSubdirectoriesAndFiles));
        directory.SetAccessControl(directoryAcl);
        try
        {
            body();
        }
        finally
        {
            file.SetAccessControl(originalFileAcl);
            directory.SetAccessControl(originalDirectoryAcl);
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule DenyRule(FileSystemRights rights)
        => new(
            new SecurityIdentifier(WindowsIdentity.GetCurrent().User!.Value),
            rights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);

    /// <summary>
    /// Maps a local absolute path onto the localhost administrative share
    /// (C:\foo\bar -> \\localhost\C$\foo\bar), giving the tests a real UNC path without
    /// creating a share. The callers assert the mapped path is actually reachable so an
    /// environment without administrative shares fails loudly instead of silently proving
    /// nothing.
    /// </summary>
    private static string ToLocalhostUncPath(string localPath)
    {
        var fullPath = Path.GetFullPath(localPath);
        Assert.True(
            fullPath.Length >= 3 && fullPath[1] == ':' && fullPath[2] == Path.DirectorySeparatorChar,
            $"test path '{fullPath}' is not a drive-rooted local path and cannot be mapped onto a localhost share");
        return $@"\\localhost\{fullPath[0]}$\{fullPath.Substring(3)}";
    }
}
