using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.Portal.Services;

public sealed class PortalDeploymentLockService
{
    private static readonly TimeSpan LockLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(30);

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

    public async Task<PortalDeploymentLockLease> AcquireUniversalImportLockAsync(
        string? owner,
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
                now.Add(LockLease));

            await DeploymentLockFile.WriteAsync(root, document, ct);
            return new PortalDeploymentLockLease(
                root,
                document,
                LockLease,
                RenewalInterval,
                _logger);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class PortalDeploymentLockLease : IAsyncDisposable
{
    private readonly string _applicationRoot;
    private readonly string _lockId;
    private readonly TimeSpan _lockLease;
    private readonly TimeSpan _renewalInterval;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _renewalTask;

    private DeploymentLockDocument _document;

    public PortalDeploymentLockLease(
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
        while (await timer.WaitForNextTickAsync(ct))
        {
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
