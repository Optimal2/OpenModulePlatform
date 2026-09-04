using Microsoft.Extensions.Logging;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

internal sealed class HostAgentDeploymentLockLease : IAsyncDisposable
{
    private static readonly TimeSpan LockLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many consecutive renewal ticks may fail to establish who owns the lock before
    /// renewal gives up (R12-A4). Three ticks is 90 seconds at the production interval,
    /// well inside the five-minute lease, so a stuck virus scanner or a moment of I/O
    /// pressure cannot end the lease while the deployment it protects is still running.
    /// </summary>
    private const int MaxConsecutiveIndeterminateReads = 3;

    private readonly string _applicationRoot;
    private readonly string _lockId;
    private readonly TimeSpan _lockLease;
    private readonly TimeSpan _renewalInterval;
    private readonly int _maxConsecutiveIndeterminateReads;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _renewalTask;

    private DeploymentLockDocument _document;

    private HostAgentDeploymentLockLease(
        string applicationRoot,
        DeploymentLockDocument document,
        TimeSpan lockLease,
        TimeSpan renewalInterval,
        int maxConsecutiveIndeterminateReads,
        ILogger logger)
    {
        _applicationRoot = applicationRoot;
        _document = document;
        _lockId = document.LockId;
        _lockLease = lockLease;
        _renewalInterval = renewalInterval;
        _maxConsecutiveIndeterminateReads = maxConsecutiveIndeterminateReads;
        _logger = logger;
        _renewalTask = RenewUntilDisposedAsync(_disposeCts.Token);
    }

    public static Task<HostAgentDeploymentLockAcquireResult> TryAcquireAsync(
        string applicationRoot,
        string applicationKey,
        string owner,
        string reason,
        ILogger logger,
        CancellationToken ct)
        => TryAcquireAsync(
            applicationRoot,
            applicationKey,
            owner,
            reason,
            LockLease,
            RenewalInterval,
            MaxConsecutiveIndeterminateReads,
            logger,
            ct);

    /// <summary>
    /// Timing-parameterised overload so the renewal loop can be exercised in tests without
    /// waiting 30 seconds per tick. Production always goes through the five-argument
    /// overload above, which is the single place the real values live.
    /// </summary>
    internal static async Task<HostAgentDeploymentLockAcquireResult> TryAcquireAsync(
        string applicationRoot,
        string applicationKey,
        string owner,
        string reason,
        TimeSpan lockLease,
        TimeSpan renewalInterval,
        int maxConsecutiveIndeterminateReads,
        ILogger logger,
        CancellationToken ct)
    {
        // Advisory read, not check-then-act: the answer decides only whether a stale file
        // is worth clearing and which status to report back. The claim itself is the
        // atomic TryCreateExclusiveAsync below, so anything this read gets wrong costs a
        // wrong skip-reason at worst, never ownership of the lock.
        var existing = DeploymentLockFile.ReadStatus(applicationRoot, DateTimeOffset.UtcNow);
        if (existing.IsLocked)
        {
            return HostAgentDeploymentLockAcquireResult.Locked(existing);
        }

        var now = DateTimeOffset.UtcNow;
        var document = DeploymentLockFile.Create(
            Guid.NewGuid().ToString("N"),
            applicationKey,
            owner,
            reason,
            now,
            now.Add(lockLease));

        // Only an EXPIRED lock file is cleared here, and the clear is itself atomic:
        // TryClearExpiredLockAsync re-verifies the LockId and the expiry inside one
        // exclusive handle and deletes through it, so it cannot remove a claim that
        // landed after this caller's advisory read. `existing.IsLocked == false` is also
        // true for "no file at all", and with nothing to clear, CreateNew alone decides
        // the winner and the loser is told it lost (R7-D6, R12-A2).
        if (existing.IsExpired)
        {
            await TryClearExpiredLockAsync(applicationRoot, existing.Document?.LockId, logger, ct);
        }

        if (!await DeploymentLockFile.TryCreateExclusiveAsync(applicationRoot, document, ct))
        {
            // Advisory read for reporting only: the race was already decided atomically by
            // CreateNew; this just names the winner for the operator and falls back to a
            // generic message when the file cannot be read. No action is taken on it.
            var winner = DeploymentLockFile.ReadStatus(applicationRoot, DateTimeOffset.UtcNow);
            return HostAgentDeploymentLockAcquireResult.Locked(
                winner.IsLocked
                    ? winner
                    : DeploymentLockStatus.Locked(
                        DeploymentLockFile.GetPath(applicationRoot),
                        null,
                        "Another deployment claimed the lock first."));
        }

        return HostAgentDeploymentLockAcquireResult.Acquired(new HostAgentDeploymentLockLease(
            applicationRoot,
            document,
            lockLease,
            renewalInterval,
            maxConsecutiveIndeterminateReads,
            logger));
    }

    /// <summary>
    /// Deletes the lock file only while it is still provably the expired document the
    /// caller observed (R12-A2) -- proven and deleted inside a single exclusive handle.
    /// </summary>
    /// <remarks>
    /// TryDeleteIfOwnedExclusiveAsync is a compare-and-delete: it opens the file with
    /// FileShare.None, re-reads the document, compares the LockId and re-checks the expiry
    /// while the handle is held, and marks the file for deletion on the close of that very
    /// handle. The interleaving the previous read-then-delete-by-path version could not
    /// survive -- two agents verify the same expired lock, one clears and claims, the
    /// other's already-issued delete removes the fresh claim and both believe they own the
    /// deployment -- is closed by construction: a competing claim is either seen by the
    /// re-read (and left alone) or physically unable to land while the handle is held.
    /// </remarks>
    internal static async Task TryClearExpiredLockAsync(
        string applicationRoot,
        string? expectedLockId,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedLockId))
        {
            // Nothing to compare against; the atomic CreateNew alone decides the race.
            return;
        }

        var outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
            applicationRoot,
            expectedLockId,
            // The expiry check runs inside the exclusive handle too: a lock its owner
            // renewed meanwhile (same LockId, future expiry) is a live lock again and
            // must be left alone.
            document =>
                string.Equals(document.Schema, DeploymentLockFile.Schema, StringComparison.Ordinal)
                && document.ExpiresUtc <= DateTimeOffset.UtcNow,
            ct);

        if (outcome.Result != DeploymentLockDeleteResult.Deleted)
        {
            logger.LogInformation(
                "HostAgent deployment lock was no longer the expired document that was observed; it was left in place. ExpectedLockId={ExpectedLockId}, LockPath={LockPath}, Result={Result}, Diagnostic={Diagnostic}",
                expectedLockId,
                DeploymentLockFile.GetPath(applicationRoot),
                outcome.Result,
                outcome.Diagnostic);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeCts.CancelAsync();
        try
        {
            await _renewalTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when the lease is disposed after a completed deployment.
        }
        catch (Exception ex)
        {
            // A faulted renewal loop must not skip the CTS disposal or the lock
            // cleanup below, and must not turn DisposeAsync itself into a throw.
            _logger.LogError(ex, "The HostAgent deployment lock renewal loop faulted.");
        }

        _disposeCts.Dispose();
        await DeleteIfOwnedAsync();
    }

    private async Task RenewUntilDisposedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_renewalInterval);
        var consecutiveIndeterminateReads = 0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            // Renewal overwrites the lock file, so it must first establish that the file is
            // still ours -- and the check and the write must be one atomic step. This loop
            // used to read the status and then call WriteAsync as two separate operations;
            // a foreign claim that landed between them was silently overwritten (the Move
            // retry loop even waited out a sharing violation to do it), and this lease went
            // on renewing a lock it no longer held. TryRenewExclusiveAsync does the read,
            // the LockId comparison and the write inside a single exclusive handle
            // (FileShare.None), so a competing claimant is either seen (Lost) or blocked
            // until the renewed document is on disk.
            var now = DateTimeOffset.UtcNow;
            var candidate = _document with
            {
                UpdatedUtc = now,
                ExpiresUtc = now.Add(_lockLease)
            };

            var outcome = await DeploymentLockFile.TryRenewExclusiveAsync(
                _applicationRoot,
                _lockId,
                _ => candidate,
                ct);

            if (outcome.Result == DeploymentLockRenewalResult.Lost)
            {
                _logger.LogWarning(
                    "HostAgent deployment lock is no longer held by this lease; renewal stopped. LockId={LockId}, OwnerLockId={OwnerLockId}, LockPath={LockPath}",
                    _lockId,
                    outcome.Document?.LockId,
                    DeploymentLockFile.GetPath(_applicationRoot));
                return;
            }

            if (outcome.Result == DeploymentLockRenewalResult.NotFound)
            {
                // Nobody holds the lock at all; the deployment this lease protects is
                // still running, so re-assert it through the atomic claim -- never through
                // a blind overwrite, which would erase a claimant that arrived first.
                var reassert = await TryReassertLockAsync(candidate, ct);
                if (reassert == ReassertOutcome.Reasserted)
                {
                    _document = candidate;
                    consecutiveIndeterminateReads = 0;
                    continue;
                }

                if (reassert == ReassertOutcome.Faulted)
                {
                    // A failed re-assert leaves the deployment unprotected in exactly the
                    // way an unreadable lock file leaves ownership unproven, so it counts
                    // against the same bound. Without this, a persistent I/O fault (a
                    // denied write, a vanished share) would be logged once per tick forever
                    // while the lock stayed unheld, with nothing reacting.
                    consecutiveIndeterminateReads++;
                    if (consecutiveIndeterminateReads >= _maxConsecutiveIndeterminateReads)
                    {
                        _logger.LogError(
                            "HostAgent deployment lock could not be re-asserted on {FailedAttempts} consecutive renewal attempts; renewal stopped and the deployment is left without a held lock. LockId={LockId}, LockPath={LockPath}",
                            consecutiveIndeterminateReads,
                            _lockId,
                            DeploymentLockFile.GetPath(_applicationRoot));
                        return;
                    }
                }

                continue;
            }

            if (outcome.Result == DeploymentLockRenewalResult.Indeterminate)
            {
                // TryRenewExclusiveAsync fails CLOSED the same way ReadStatus does: an
                // IOException, a denied read, a half-written file or a planted reparse
                // point all come back with no document. Treating that as a change of
                // owner would end the renewal loop for good on a single transient fault --
                // while the deployment it was protecting kept running, its lock quietly
                // expiring underneath it. Tolerate a bounded run of them, and never claim
                // the lease was taken when all that is known is that the file could not be
                // read (R12-A4).
                consecutiveIndeterminateReads++;
                if (consecutiveIndeterminateReads >= _maxConsecutiveIndeterminateReads)
                {
                    _logger.LogError(
                        "HostAgent deployment lock ownership could not be verified on {FailedReads} consecutive renewal attempts; renewal stopped and the lock will expire. LockId={LockId}, LockPath={LockPath}, Diagnostic={Diagnostic}",
                        consecutiveIndeterminateReads,
                        _lockId,
                        DeploymentLockFile.GetPath(_applicationRoot),
                        outcome.Diagnostic);
                    return;
                }

                _logger.LogWarning(
                    "HostAgent deployment lock could not be read on this renewal attempt; retrying on the next tick. Attempt={FailedReads}, LockId={LockId}, LockPath={LockPath}, Diagnostic={Diagnostic}",
                    consecutiveIndeterminateReads,
                    _lockId,
                    DeploymentLockFile.GetPath(_applicationRoot),
                    outcome.Diagnostic);
                continue;
            }

            consecutiveIndeterminateReads = 0;
            _document = outcome.Document ?? candidate;
        }
    }

    /// <summary>
    /// Re-asserts this lease's claim when the lock file has vanished, using the same atomic
    /// create-if-absent primitive as acquisition so a simultaneous claimant is never
    /// overwritten (R12-A2).
    /// </summary>
    private async Task<ReassertOutcome> TryReassertLockAsync(DeploymentLockDocument candidate, CancellationToken ct)
    {
        try
        {
            if (await DeploymentLockFile.TryCreateExclusiveAsync(_applicationRoot, candidate, ct))
            {
                return ReassertOutcome.Reasserted;
            }

            _logger.LogInformation(
                "HostAgent deployment lock file was gone, but another claimant took it before it could be re-asserted; the next renewal tick will establish the owner. LockId={LockId}, LockPath={LockPath}",
                _lockId,
                DeploymentLockFile.GetPath(_applicationRoot));
            return ReassertOutcome.ClaimedByOther;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to re-assert HostAgent deployment lock. LockId={LockId}, LockPath={LockPath}",
                _lockId,
                DeploymentLockFile.GetPath(_applicationRoot));
            return ReassertOutcome.Faulted;
        }
    }

    /// <summary>
    /// What one re-assert attempt established. A fault is distinguished from a lost race
    /// because only the fault says something is persistently wrong: it counts against the
    /// same bound as an unreadable lock file, while a lost race just means the next tick
    /// reads the winner.
    /// </summary>
    private enum ReassertOutcome
    {
        /// <summary>The lock file was re-created with this lease's claim.</summary>
        Reasserted,

        /// <summary>Another claimant won the atomic create; the next tick reads the owner.</summary>
        ClaimedByOther,

        /// <summary>The create itself failed with an I/O error; nothing about the lock is known.</summary>
        Faulted
    }

    /// <summary>
    /// What a renewal-time read of the lock file actually established (R12-A4).
    /// </summary>
    internal enum RenewalOwnership
    {
        /// <summary>The lock file names this lease, or is absent and can be re-asserted.</summary>
        Held,

        /// <summary>The lock file could not be read, so nothing about ownership is known.</summary>
        Indeterminate,

        /// <summary>The lock file names a different lease. This is the only real loss.</summary>
        Lost
    }

    /// <summary>
    /// Separates a genuine change of owner from a read that failed (R12-A4).
    /// </summary>
    /// <remarks>
    /// A readable document is authoritative in both directions, including an expired one
    /// that still carries our LockId -- that is our own lease running late, and extending it
    /// is exactly right while the deployment is still in flight. A missing file means nobody
    /// holds the lock at all; the deployment this lease protects is still running, so the
    /// renewal write re-asserts it rather than falling silent. Everything else is
    /// ReadStatus's fail-closed branch and proves nothing.
    ///
    /// The renewal loop no longer consults this classifier: it gets the same verdicts
    /// atomically from TryRenewExclusiveAsync. The classifier is retained because the
    /// Portal lease keeps a deliberate second copy of it, both copies document the
    /// ReadStatus semantics the atomic renewal was built from, and the tests pin them.
    /// </remarks>
    internal static RenewalOwnership ClassifyRenewalOwnership(DeploymentLockStatus status, string lockId)
    {
        if (status.Document is not null)
        {
            return string.Equals(status.Document.LockId, lockId, StringComparison.Ordinal)
                ? RenewalOwnership.Held
                : RenewalOwnership.Lost;
        }

        return status.IsLocked ? RenewalOwnership.Indeterminate : RenewalOwnership.Held;
    }

    /// <summary>
    /// Removes the lock file on disposal only while it is still provably ours -- proven
    /// and deleted inside a single exclusive handle, so a claim that landed while the
    /// deployment was finishing can never be removed by it.
    /// </summary>
    private async Task DeleteIfOwnedAsync()
    {
        try
        {
            var outcome = await DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync(
                _applicationRoot,
                _lockId,
                deletionRequirement: null,
                CancellationToken.None);

            // NotOwned and NotFound are ordinary outcomes (the lock was lost or re-asserted
            // elsewhere); only an unproven read is worth a warning.
            if (outcome.Result == DeploymentLockDeleteResult.Indeterminate)
            {
                _logger.LogWarning(
                    "Failed to remove HostAgent deployment lock. LockId={LockId}, LockPath={LockPath}, Diagnostic={Diagnostic}",
                    _lockId,
                    DeploymentLockFile.GetPath(_applicationRoot),
                    outcome.Diagnostic);
            }
        }
        catch (Exception ex)
        {
            // Disposal must never throw: a leftover lock file expires on its own, a
            // thrown DisposeAsync masks the deployment's real outcome.
            _logger.LogWarning(
                ex,
                "Failed to remove HostAgent deployment lock. LockId={LockId}, LockPath={LockPath}",
                _lockId,
                DeploymentLockFile.GetPath(_applicationRoot));
        }
    }
}

internal sealed record HostAgentDeploymentLockAcquireResult(
    HostAgentDeploymentLockLease? Lease,
    DeploymentLockStatus? ExistingLockStatus)
{
    public static HostAgentDeploymentLockAcquireResult Acquired(HostAgentDeploymentLockLease lease)
        => new(lease, null);

    public static HostAgentDeploymentLockAcquireResult Locked(DeploymentLockStatus lockStatus)
        => new(null, lockStatus);
}
