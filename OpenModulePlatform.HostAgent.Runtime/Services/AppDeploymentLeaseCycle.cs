using Microsoft.Extensions.Logging;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>Cross-host coordination for one combined web/service deployment sweep.</summary>
public sealed class AppDeploymentLeaseCycle : IAsyncDisposable
{
    private readonly IOmpHostArtifactRepository _repository;
    private readonly ILogger _logger;
    private readonly DeploymentLockSettings _settings;
    private AppDeploymentLeaseResult? _lease;
    private string? _scopeKey;
    private bool _hostSweepAborted;
    private bool _appInProgress;
    public bool IsCoordinated => _settings.Scope != "off";
    public string? WaitingMessage { get; private set; }

    private AppDeploymentLeaseCycle(IOmpHostArtifactRepository repository, ILogger logger, DeploymentLockSettings settings)
    {
        _repository = repository;
        _logger = logger;
        _settings = settings;
    }

    public static async Task<AppDeploymentLeaseCycle> CreateAsync(
        IOmpHostArtifactRepository repository, HostAgentSettings settings, ILogger logger, CancellationToken ct)
    {
        var scope = settings.DeploymentLockScope is "app" or "host" or "off" ? settings.DeploymentLockScope : "app";
        var seconds = settings.DeploymentLeaseSeconds is >= 1 and <= 86400 ? settings.DeploymentLeaseSeconds : 600;
        IReadOnlyDictionary<string, string?> values = new Dictionary<string, string?>();
        try
        {
            values = await repository.ReadDeploymentLockSettingsAsync(ct);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            logger.LogInformation(ex, "Deployment lock settings unavailable; using HostAgentSettings fallback.");
        }
        values.TryGetValue("DeploymentLockScope", out var dbScope);
        values.TryGetValue("DeploymentLeaseSeconds", out var dbSeconds);
        var validScope = dbScope is "app" or "host" or "off";
        var validSeconds = int.TryParse(dbSeconds, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedSeconds) && parsedSeconds is >= 1 and <= 86400;
        if (validScope) scope = dbScope!;
        if (validSeconds) seconds = parsedSeconds;
        logger.LogInformation("DeploymentLockScope={Scope} ({ScopeSource}), DeploymentLeaseSeconds={Seconds} ({SecondsSource}).",
            scope, validScope ? "database" : "HostAgentSettings fallback", seconds,
            validSeconds ? "database" : "HostAgentSettings fallback");
        if (await repository.GetEnabledHostCountAsync(ct) <= 1) scope = "off";
        return new(repository, logger, new(scope, seconds));
    }

    public async Task<bool> EnterAsync(Guid hostId, string appKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCoordinated) return true;
        if (_hostSweepAborted) return false;
        if (_settings.Scope == "host" && WaitingMessage is not null) return false;
        if (_settings.Scope == "host" && _lease is not null)
        {
            _appInProgress = true;
            return true;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        _scopeKey = _settings.Scope == "host" ? "host:*" : "app:" + appKey.Trim().ToLowerInvariant();
        var lease = await _repository.AcquireAppDeploymentLeaseAsync(_scopeKey, hostId,
            "HostAgent deployment sweep", _settings.LeaseSeconds, ct);
        if (!lease.Acquired)
        {
            WaitingMessage = $"Waiting for lease {_scopeKey} held by host {lease.HostName} until {lease.LeaseUntilUtc:yyyy-MM-dd HH:mm:ss.fff} UTC.";
            _logger.LogInformation("{DeploymentLeaseStatus}", WaitingMessage);
            return false;
        }
        WaitingMessage = null;
        _lease = lease;
        _appInProgress = true;
        _logger.LogInformation("Acquired deployment lease {ScopeKey} until {LeaseUntilUtc:O}; expired lease takeover={ExpiredTakeover}.",
            _scopeKey, lease.LeaseUntilUtc, lease.TookOverExpiredLease);
        return true;
    }

    public async Task CompleteAppAsync(string? failureReason = null)
    {
        if (failureReason is not null && _settings.Scope == "host")
        {
            _hostSweepAborted = true;
            WaitingMessage = "Deployment sweep stopped after failure: " + failureReason;
        }
        _appInProgress = false;
        if (_lease is not null && (_settings.Scope == "app" || failureReason is not null))
            await ReleaseAsync(failureReason);
    }

    private async Task ReleaseAsync(string? reason)
    {
        var lease = _lease;
        _lease = null;
        if (lease is null) return;
        using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _repository.ReleaseAppDeploymentLeaseAsync(_scopeKey!, lease.LeaseToken, reason, releaseTimeout.Token);
            _logger.LogInformation("Released deployment lease {ScopeKey}. Reason={Reason}", _scopeKey, reason ?? "Healthy deployment completed");
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not release deployment lease {ScopeKey}; it expires at {LeaseUntilUtc:O}.", _scopeKey, lease.LeaseUntilUtc);
        }
    }

    public async ValueTask DisposeAsync()
        => await ReleaseAsync(_appInProgress ? "Deployment interrupted before health confirmation." : null);
}
