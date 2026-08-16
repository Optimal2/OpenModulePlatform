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

        // Only an EXPIRED lock file is cleared here, and only after a second read confirms
        // it is still the same expired document. R7-D6 made the claim atomic with
        // FileMode.CreateNew and then left an unconditional TryDelete standing in front of
        // it -- which moved the check-then-act from the write to the delete instead of
        // removing it. `existing.IsLocked == false` is also true for "no file at all", so
        // the common path deleted whatever had appeared in the microseconds since the read:
        // another agent's fresh, valid claim. With nothing to clear, CreateNew alone decides
        // the winner and the loser is told it lost (R12-A2).
        if (existing.IsExpired)
        {
            TryClearExpiredLock(applicationRoot, existing.Document?.LockId, logger);
        }

        if (!await DeploymentLockFile.TryCreateExclusiveAsync(applicationRoot, document, ct))
        {
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
    /// caller observed (R12-A2).
    /// </summary>
    /// <remarks>
    /// The re-read is what makes this safe in the case that matters: a competing agent that
    /// cleared the same stale file and claimed it writes a document with a new LockId and a
    /// future expiry, so the confirmation fails and this caller leaves it alone, then loses
    /// the CreateNew race and is told so. A residual interleaving remains -- the file could
    /// in principle be replaced between the confirmation and the delete -- but the window
    /// went from "every acquisition" to "two agents inside the same few microseconds of an
    /// expiry", and closing it completely needs a compare-and-delete primitive the file API
    /// does not offer.
    /// </remarks>
    internal static void TryClearExpiredLock(string applicationRoot, string? expectedLockId, ILogger logger)
    {
        var confirmation = DeploymentLockFile.ReadStatus(applicationRoot, DateTimeOffset.UtcNow);
        if (!confirmation.IsExpired
            || !string.Equals(confirmation.Document?.LockId, expectedLockId, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "HostAgent deployment lock was no longer the expired document that was observed; it was left in place. ExpectedLockId={ExpectedLockId}, LockPath={LockPath}",
                expectedLockId ?? "(unknown)",
                DeploymentLockFile.GetPath(applicationRoot));
            return;
        }

        DeploymentLockFile.TryDelete(DeploymentLockFile.GetPath(applicationRoot));
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

        _disposeCts.Dispose();
        DeleteIfOwned();
    }

    private async Task RenewUntilDisposedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_renewalInterval);
        var consecutiveIndeterminateReads = 0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            // Renewal overwrites the lock file, so it must first establish that the file
            // is still ours. If this lease expired and another deployment claimed it, an
            // unconditional write would hand ownership back to us while that deployment
            // was mid-flight.
            var current = DeploymentLockFile.ReadStatus(_applicationRoot, DateTimeOffset.UtcNow);
            var verdict = ClassifyRenewalOwnership(current, _lockId);
            if (verdict == RenewalOwnership.Lost)
            {
                _logger.LogWarning(
                    "HostAgent deployment lock is no longer held by this lease; renewal stopped. LockId={LockId}, OwnerLockId={OwnerLockId}, LockPath={LockPath}",
                    _lockId,
                    current.Document?.LockId,
                    DeploymentLockFile.GetPath(_applicationRoot));
                return;
            }

            if (verdict == RenewalOwnership.Indeterminate)
            {
                // ReadStatus fails CLOSED: an IOException, a denied read, a half-written
                // file or a planted reparse point all come back as Locked with a null
                // Document. Comparing that null against _lockId made a momentary read
                // failure indistinguishable from a real change of owner, so a single
                // transient fault ended the renewal loop for good -- while the deployment
                // it was protecting kept running, its lock quietly expiring underneath it.
                // Tolerate a bounded run of them, and never claim the lease was taken when
                // all that is known is that the file could not be read (R12-A4).
                consecutiveIndeterminateReads++;
                if (consecutiveIndeterminateReads >= _maxConsecutiveIndeterminateReads)
                {
                    _logger.LogError(
                        "HostAgent deployment lock ownership could not be verified on {FailedReads} consecutive renewal attempts; renewal stopped and the lock will expire. LockId={LockId}, LockPath={LockPath}, Diagnostic={Diagnostic}",
                        consecutiveIndeterminateReads,
                        _lockId,
                        DeploymentLockFile.GetPath(_applicationRoot),
                        current.Diagnostic);
                    return;
                }

                _logger.LogWarning(
                    "HostAgent deployment lock could not be read on this renewal attempt; retrying on the next tick. Attempt={FailedReads}, LockId={LockId}, LockPath={LockPath}, Diagnostic={Diagnostic}",
                    consecutiveIndeterminateReads,
                    _lockId,
                    DeploymentLockFile.GetPath(_applicationRoot),
                    current.Diagnostic);
                continue;
            }

            consecutiveIndeterminateReads = 0;

            var now = DateTimeOffset.UtcNow;
            _document = _document with
            {
                UpdatedUtc = now,
                ExpiresUtc = now.Add(_lockLease)
            };

            try
            {
                await DeploymentLockFile.WriteAsync(_applicationRoot, _document, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to renew HostAgent deployment lock. LockId={LockId}, LockPath={LockPath}",
                    _lockId,
                    DeploymentLockFile.GetPath(_applicationRoot));
            }
        }
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

    private void DeleteIfOwned()
    {
        var path = DeploymentLockFile.GetPath(_applicationRoot);
        try
        {
            var status = DeploymentLockFile.ReadStatus(_applicationRoot, DateTimeOffset.UtcNow);
            if (string.Equals(status.Document?.LockId, _lockId, StringComparison.Ordinal))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to remove HostAgent deployment lock. LockId={LockId}, LockPath={LockPath}",
                _lockId,
                path);
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
