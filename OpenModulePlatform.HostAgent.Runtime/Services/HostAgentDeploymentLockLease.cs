using Microsoft.Extensions.Logging;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

internal sealed class HostAgentDeploymentLockLease : IAsyncDisposable
{
    private static readonly TimeSpan LockLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(30);

    private readonly string _applicationRoot;
    private readonly string _lockId;
    private readonly TimeSpan _lockLease;
    private readonly TimeSpan _renewalInterval;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _renewalTask;

    private DeploymentLockDocument _document;

    private HostAgentDeploymentLockLease(
        string applicationRoot,
        DeploymentLockDocument document,
        TimeSpan lockLease,
        TimeSpan renewalInterval,
        ILogger logger)
    {
        _applicationRoot = applicationRoot;
        _document = document;
        _lockId = document.LockId;
        _lockLease = lockLease;
        _renewalInterval = renewalInterval;
        _logger = logger;
        _renewalTask = RenewUntilDisposedAsync(_disposeCts.Token);
    }

    public static async Task<HostAgentDeploymentLockAcquireResult> TryAcquireAsync(
        string applicationRoot,
        string applicationKey,
        string owner,
        string reason,
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
            now.Add(LockLease));

        // Clear an expired lock left behind by a crashed deployment, then claim. The read
        // above only established that nobody holds the lock *now*; between that read and
        // this write another HostAgent could reach the same conclusion, and an overwriting
        // write would let both of them believe they own the deployment (R7-D6). The claim
        // below is the atomic step, so the loser is told it lost.
        DeploymentLockFile.TryDelete(DeploymentLockFile.GetPath(applicationRoot));

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
            LockLease,
            RenewalInterval,
            logger));
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
        while (await timer.WaitForNextTickAsync(ct))
        {
            // Renewal overwrites the lock file, so it must first establish that the file
            // is still ours. If this lease expired and another deployment claimed it, an
            // unconditional write would hand ownership back to us while that deployment
            // was mid-flight.
            var current = DeploymentLockFile.ReadStatus(_applicationRoot, DateTimeOffset.UtcNow);
            if (!string.Equals(current.Document?.LockId, _lockId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "HostAgent deployment lock is no longer held by this lease; renewal stopped. LockId={LockId}, LockPath={LockPath}",
                    _lockId,
                    DeploymentLockFile.GetPath(_applicationRoot));
                return;
            }

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
