using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.Portal.Services;

public sealed class PortalDeploymentLockService
{
    private static readonly TimeSpan LockLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many consecutive renewal ticks may fail to establish who owns the lock before
    /// renewal gives up (R12-A4). Three ticks is 90 seconds at the production interval, well
    /// inside the five-minute lease, so a moment of I/O pressure cannot end the lease while
    /// the import it protects is still running.
    /// </summary>
    internal const int MaxConsecutiveIndeterminateReads = 3;

    private readonly IHostEnvironment _environment;
    private readonly ILogger<PortalDeploymentLockService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PortalDeploymentLockService(
        IHostEnvironment environment,
        ILogger<PortalDeploymentLockService> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public Task<PortalDeploymentLockLease> AcquireUniversalImportLockAsync(
        string? owner,
        CancellationToken ct)
        => AcquireUniversalImportLockAsync(
            owner,
            LockLease,
            RenewalInterval,
            MaxConsecutiveIndeterminateReads,
            ct);

    /// <summary>
    /// Timing-parameterised overload so the renewal loop can be exercised in tests without
    /// waiting 30 seconds per tick. Production always goes through the two-argument overload
    /// above, which is the single place the real values live.
    /// </summary>
    internal async Task<PortalDeploymentLockLease> AcquireUniversalImportLockAsync(
        string? owner,
        TimeSpan lockLease,
        TimeSpan renewalInterval,
        int maxConsecutiveIndeterminateReads,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var root = _environment.ContentRootPath;
            var existing = DeploymentLockFile.ReadStatus(root, DateTimeOffset.UtcNow);
            if (existing.IsLocked)
            {
                throw new InvalidOperationException(existing.ToDeploymentSkippedMessage("Portal"));
            }

            var now = DateTimeOffset.UtcNow;
            var document = DeploymentLockFile.Create(
                Guid.NewGuid().ToString("N"),
                "omp_portal",
                string.IsNullOrWhiteSpace(owner) ? "OMP Portal" : owner.Trim(),
                "Universal module package import is running.",
                now,
                now.Add(lockLease));

            // The claim itself has to be the atomic step (R12-A2, applied here as the sibling
            // §4.1 requires). This method used to read the status and then call WriteAsync,
            // which ends in File.Move(overwrite: true) -- the right primitive for renewing a
            // lock you already hold and the wrong one for taking it. The SemaphoreSlim above
            // only serialises the Portal against itself, and the lock file that matters is
            // shared with a different process: HostAgent writes it at this very content root
            // whenever it deploys omp_portal. So the sequence "Portal reads not-locked,
            // HostAgent claims, Portal overwrites" made the Portal the recorded owner of a
            // deployment HostAgent was already running, and each of them then replaced the
            // Portal's files from underneath the other. FileMode.CreateNew lets exactly one
            // of them win, and the loser is told so.
            //
            // Only an EXPIRED lock is cleared first, and only after a second read confirms it
            // is still the same expired document -- an unconditional TryDelete in front of
            // CreateNew is what reopened the race in the HostAgent copy, because
            // `IsLocked == false` is also true for "no file at all".
            if (existing.IsExpired)
            {
                TryClearExpiredLock(root, existing.Document?.LockId, _logger);
            }

            if (!await DeploymentLockFile.TryCreateExclusiveAsync(root, document, ct))
            {
                var winner = DeploymentLockFile.ReadStatus(root, DateTimeOffset.UtcNow);
                var lockStatus = winner.IsLocked
                    ? winner
                    : DeploymentLockStatus.Locked(
                        DeploymentLockFile.GetPath(root),
                        null,
                        "Another deployment claimed the lock first.");
                throw new InvalidOperationException(lockStatus.ToDeploymentSkippedMessage("Portal"));
            }

            return new PortalDeploymentLockLease(
                root,
                document,
                lockLease,
                renewalInterval,
                maxConsecutiveIndeterminateReads,
                _logger);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes the lock file only while it is still provably the expired document the caller
    /// observed (R12-A2).
    /// </summary>
    /// <remarks>
    /// The re-read is what makes this safe in the case that matters: a competing HostAgent
    /// that cleared the same stale file and claimed it writes a document with a new LockId
    /// and a future expiry, so the confirmation fails, this caller leaves it alone, and then
    /// loses the CreateNew race and is told so. A residual interleaving remains -- the file
    /// could in principle be replaced between the confirmation and the delete -- but the
    /// window went from "every acquisition" to "two claimants inside the same few
    /// microseconds of an expiry", and closing it completely needs a compare-and-delete
    /// primitive the file API does not offer.
    /// </remarks>
    internal static void TryClearExpiredLock(string applicationRoot, string? expectedLockId, ILogger logger)
    {
        var confirmation = DeploymentLockFile.ReadStatus(applicationRoot, DateTimeOffset.UtcNow);
        if (!confirmation.IsExpired
            || !string.Equals(confirmation.Document?.LockId, expectedLockId, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Portal deployment lock was no longer the expired document that was observed; it was left in place. ExpectedLockId={ExpectedLockId}, LockPath={LockPath}",
                expectedLockId ?? "(unknown)",
                DeploymentLockFile.GetPath(applicationRoot));
            return;
        }

        DeploymentLockFile.TryDelete(DeploymentLockFile.GetPath(applicationRoot));
    }
}

public sealed class PortalDeploymentLockLease : IAsyncDisposable
{
    private readonly string _applicationRoot;
    private readonly string _lockId;
    private readonly TimeSpan _lockLease;
    private readonly TimeSpan _renewalInterval;
    private readonly int _maxConsecutiveIndeterminateReads;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _renewalTask;

    private DeploymentLockDocument _document;

    public PortalDeploymentLockLease(
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

    public async ValueTask DisposeAsync()
    {
        await _disposeCts.CancelAsync();
        try
        {
            await _renewalTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when the lease is disposed after a completed import.
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
            // Renewal overwrites the lock file, so it must first establish that the file is
            // still ours. This loop had no ownership check at all: it wrote every 30 seconds
            // regardless of what the file said. If a long import outlived its five-minute
            // lease and HostAgent legitimately claimed the expired lock, the very next
            // renewal tick handed ownership back to the Portal while HostAgent was mid-flight
            // -- and HostAgent's own renewal then read a foreign LockId and stopped, so the
            // deployment that was actually running lost its protection to the one that had
            // let its lease lapse. Same invariant as the HostAgent lease, opposite failure
            // direction (R12-A4 sibling).
            var current = DeploymentLockFile.ReadStatus(_applicationRoot, DateTimeOffset.UtcNow);
            var verdict = ClassifyRenewalOwnership(current, _lockId);
            if (verdict == RenewalOwnership.Lost)
            {
                _logger.LogWarning(
                    "Portal deployment lock is no longer held by this lease; renewal stopped. LockId={LockId}, OwnerLockId={OwnerLockId}, LockPath={LockPath}",
                    _lockId,
                    current.Document?.LockId,
                    DeploymentLockFile.GetPath(_applicationRoot));
                return;
            }

            if (verdict == RenewalOwnership.Indeterminate)
            {
                // ReadStatus fails CLOSED: an IOException, a denied read, a half-written file
                // or a planted reparse point all come back as Locked with a null Document.
                // Comparing that null against _lockId would make a momentary read failure
                // indistinguishable from a real change of owner and end renewal for good
                // while the import it protects kept running. Tolerate a bounded run of them,
                // and never claim the lease was taken when all that is known is that the file
                // could not be read (R12-A4).
                consecutiveIndeterminateReads++;
                if (consecutiveIndeterminateReads >= _maxConsecutiveIndeterminateReads)
                {
                    _logger.LogError(
                        "Portal deployment lock ownership could not be verified on {FailedReads} consecutive renewal attempts; renewal stopped and the lock will expire. LockId={LockId}, LockPath={LockPath}, Diagnostic={Diagnostic}",
                        consecutiveIndeterminateReads,
                        _lockId,
                        DeploymentLockFile.GetPath(_applicationRoot),
                        current.Diagnostic);
                    return;
                }

                _logger.LogWarning(
                    "Portal deployment lock could not be read on this renewal attempt; retrying on the next tick. Attempt={FailedReads}, LockId={LockId}, LockPath={LockPath}, Diagnostic={Diagnostic}",
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
                    "Failed to renew Portal deployment lock. LockId={LockId}",
                    _lockId);
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
    /// A readable document is authoritative in both directions, including an expired one that
    /// still carries our LockId -- that is our own lease running late, and extending it is
    /// exactly right while the import is still in flight. A missing file means nobody holds
    /// the lock at all; the import this lease protects is still running, so the renewal write
    /// re-asserts it rather than falling silent. Everything else is ReadStatus's fail-closed
    /// branch and proves nothing.
    ///
    /// This is a deliberate second copy of the HostAgent lease's classifier rather than a
    /// shared helper: the original is internal to OpenModulePlatform.HostAgent.Runtime, which
    /// the Portal does not and should not reference, and the natural shared home
    /// (OpenModulePlatform.Artifacts.DeploymentLockFile) is outside the scope this change was
    /// allowed to touch. Both copies are covered by their own tests; if a third claimant ever
    /// appears, promote it into DeploymentLockFile instead of copying again.
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
                "Failed to remove Portal deployment lock. LockId={LockId}, LockPath={LockPath}",
                _lockId,
                path);
        }
    }
}
