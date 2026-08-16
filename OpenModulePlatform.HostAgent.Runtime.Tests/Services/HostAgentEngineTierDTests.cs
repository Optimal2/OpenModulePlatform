using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class HostAgentEngineTierDTests
{
    private const string HostKey = "test-host";
    private const string ServiceName = "OMP.HostAgent.Test";

    private static HostAgentEngine CreateEngine(
        IOmpHostArtifactRepository repository,
        TimeProvider? timeProvider = null,
        HostAgentSettings? settings = null)
    {
        var optionsMonitor = new FakeOptionsMonitor<HostAgentSettings>
        {
            CurrentValue = settings ?? new HostAgentSettings
            {
                HostDeploymentLeaseSeconds = 30,
                HostDeploymentMaxAttempts = 3
            }
        };

        var consistencyService = new DeploySetConsistencyService(
            optionsMonitor,
            repository,
            NullLogger<DeploySetConsistencyService>.Instance);

        return new HostAgentEngine(
            optionsMonitor,
            repository,
            provisioner: null!,
            artifactZipImportService: null!,
            webAppDeploymentService: null!,
            serviceAppDeploymentService: null!,
            selfUpgradeService: null!,
            fileMirrorService: null!,
            webAppHealthMonitor: null!,
            resourceCollector: null!,
            jobProcessor: null!,
            consistencyService,
            new HostAgentProcessContext(ServiceName, "0.0.0", HostAgentRuntimeMode.Normal, null),
            timeProvider ?? TimeProvider.System,
            NullLogger<HostAgentEngine>.Instance);
    }

    [Fact]
    public async Task ProcessNextHostDeploymentAsync_WhenNoDeploymentAvailable_ReturnsImmediately()
    {
        var repository = new FakeOmpHostArtifactRepository { NextDeployment = null };
        var engine = CreateEngine(repository);

        await engine.ProcessNextHostDeploymentAsync(HostKey, CancellationToken.None);

        Assert.Equal(HostKey, repository.LastClaimHostKey);
        Assert.Empty(repository.CompletedDeployments);
    }

    [Fact]
    public async Task ProcessNextHostDeploymentAsync_WhenDeploymentSucceeds_CompletesWithSuccess()
    {
        var leaseToken = Guid.NewGuid();
        var repository = new FakeOmpHostArtifactRepository
        {
            NextDeployment = new HostDeploymentWorkItem(1, 42, "host-template", leaseToken),
            MaterializeResult = new TemplateMaterializationResult(5, 3)
        };
        var engine = CreateEngine(repository);

        await engine.ProcessNextHostDeploymentAsync(HostKey, CancellationToken.None);

        var completed = Assert.Single(repository.CompletedDeployments);
        Assert.True(completed.Succeeded);
        Assert.Equal(leaseToken, completed.LeaseToken);
        Assert.Contains("5", completed.OutcomeMessage);
        Assert.Contains("3", completed.OutcomeMessage);
    }

    [Fact]
    public async Task ProcessNextHostDeploymentAsync_WhenDeploymentFails_CompletesWithFailureMessage()
    {
        var leaseToken = Guid.NewGuid();
        var repository = new FakeOmpHostArtifactRepository
        {
            NextDeployment = new HostDeploymentWorkItem(1, 42, "host-template", leaseToken),
            MaterializeException = new InvalidOperationException("Materialization failed.")
        };
        var engine = CreateEngine(repository);

        await engine.ProcessNextHostDeploymentAsync(HostKey, CancellationToken.None);

        var completed = Assert.Single(repository.CompletedDeployments);
        Assert.False(completed.Succeeded);
        Assert.Equal(leaseToken, completed.LeaseToken);
        Assert.Equal("Materialization failed.", completed.OutcomeMessage);
    }

    [Fact]
    public async Task ProcessNextHostDeploymentAsync_WhenLeaseLostMidProcessing_CancelsWithoutRethrow()
    {
        var leaseToken = Guid.NewGuid();
        var repository = new FakeOmpHostArtifactRepository
        {
            NextDeployment = new HostDeploymentWorkItem(1, 42, "host-template", leaseToken),
            MaterializeResult = new TemplateMaterializationResult(1, 1),
            BlockMaterializeUntilCancelled = true,
            RenewLeaseResult = false
        };
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var engine = CreateEngine(repository, timeProvider);

        var processTask = engine.ProcessNextHostDeploymentAsync(HostKey, CancellationToken.None);

        // The renewal interval for a 30 second lease is clamp(30/3, 10, 120) = 10s.
        timeProvider.Advance(TimeSpan.FromSeconds(10));

        await processTask;

        Assert.Empty(repository.CompletedDeployments);
    }

    [Fact]
    public async Task ProcessNextHostDeploymentAsync_WhenHostAgentShutsDown_RethrowsCancellation()
    {
        var leaseToken = Guid.NewGuid();
        var repository = new FakeOmpHostArtifactRepository
        {
            NextDeployment = new HostDeploymentWorkItem(1, 42, "host-template", leaseToken),
            MaterializeException = new OperationCanceledException("Shutdown.")
        };
        var engine = CreateEngine(repository);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            engine.ProcessNextHostDeploymentAsync(HostKey, cts.Token));

        Assert.Empty(repository.CompletedDeployments);
    }

    [Fact]
    public async Task ProcessNextHostDeploymentAsync_PassesConfiguredMaxAttemptsToClaim()
    {
        var repository = new FakeOmpHostArtifactRepository { NextDeployment = null };
        var engine = CreateEngine(repository, settings: new HostAgentSettings
        {
            HostDeploymentMaxAttempts = 7,
            HostDeploymentLeaseSeconds = 60
        });

        await engine.ProcessNextHostDeploymentAsync(HostKey, CancellationToken.None);

        Assert.Equal(HostKey, repository.LastClaimHostKey);
        Assert.Equal(ServiceName, repository.LastClaimServiceName);
        Assert.Equal(60, repository.LastClaimLeaseSeconds);
        Assert.Equal(7, repository.LastClaimMaxAttempts);
    }

    [Fact]
    public async Task RenewHostDeploymentLeaseUntilProcessingCompletesAsync_WhenRenewedSuccessfully_ContinuesLoop()
    {
        var repository = new FakeOmpHostArtifactRepository { RenewLeaseResult = true };
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var processingCts = new CancellationTokenSource();
        var deployment = new HostDeploymentWorkItem(1, 42, "host-template", Guid.NewGuid());
        var engine = CreateEngine(repository, timeProvider);

        var leaseRenewal = engine.RenewHostDeploymentLeaseUntilProcessingCompletesAsync(
            deployment,
            leaseSeconds: 30,
            processingCts,
            CancellationToken.None);

        // First renewal interval is 10s (clamp(30/3,10,120)).
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await Task.Yield();
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await Task.Yield();

        processingCts.Cancel();
        await leaseRenewal;

        Assert.True(repository.RenewLeaseCallCount >= 2, $"Expected at least 2 renewal calls, got {repository.RenewLeaseCallCount}.");
    }

    [Fact]
    public async Task RenewHostDeploymentLeaseUntilProcessingCompletesAsync_WhenLeaseLost_CancelsProcessing()
    {
        var repository = new FakeOmpHostArtifactRepository { RenewLeaseResult = false };
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var processingCts = new CancellationTokenSource();
        var deployment = new HostDeploymentWorkItem(1, 42, "host-template", Guid.NewGuid());
        var engine = CreateEngine(repository, timeProvider);

        var leaseRenewal = engine.RenewHostDeploymentLeaseUntilProcessingCompletesAsync(
            deployment,
            leaseSeconds: 30,
            processingCts,
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await leaseRenewal;

        Assert.True(processingCts.IsCancellationRequested);
        Assert.Equal(1, repository.RenewLeaseCallCount);
    }

    [Fact]
    public async Task RenewHostDeploymentLeaseUntilProcessingCompletesAsync_WhenCancellationRequested_ReturnsSilently()
    {
        var repository = new FakeOmpHostArtifactRepository();
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var processingCts = new CancellationTokenSource();
        var deployment = new HostDeploymentWorkItem(1, 42, "host-template", Guid.NewGuid());
        var engine = CreateEngine(repository, timeProvider);

        var leaseRenewal = engine.RenewHostDeploymentLeaseUntilProcessingCompletesAsync(
            deployment,
            leaseSeconds: 30,
            processingCts,
            CancellationToken.None);

        processingCts.Cancel();
        await leaseRenewal;

        Assert.Equal(0, repository.RenewLeaseCallCount);
    }

    [Fact]
    public async Task RenewHostDeploymentLeaseUntilProcessingCompletesAsync_WhenTransientSqlError_RetriesOnNextInterval()
    {
        var repository = new FakeOmpHostArtifactRepository
        {
            RenewLeaseException = new TestDbException("Transient SQL error.")
        };
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var processingCts = new CancellationTokenSource();
        var deployment = new HostDeploymentWorkItem(1, 42, "host-template", Guid.NewGuid());
        var engine = CreateEngine(repository, timeProvider);

        var leaseRenewal = engine.RenewHostDeploymentLeaseUntilProcessingCompletesAsync(
            deployment,
            leaseSeconds: 30,
            processingCts,
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await Task.Yield();

        // Remove the exception for the next renewal.
        repository.RenewLeaseException = null;
        repository.RenewLeaseResult = true;
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await Task.Yield();

        processingCts.Cancel();
        await leaseRenewal;

        Assert.True(repository.RenewLeaseCallCount >= 2, $"Expected at least 2 renewal calls, got {repository.RenewLeaseCallCount}.");
    }

    /// <summary>
    /// A failing artifact import must not end the cycle, and must not go quiet either.
    /// </summary>
    /// <remarks>
    /// R12-F4. ImportPendingAsync is called before every deployment step, and its own
    /// top-level guards -- the reparse-point checks on the import, processed and failed
    /// roots -- throw before its per-file try/catch is reached. A junction on any of those
    /// three folders, which needs no privilege to create, therefore ended the cycle at the
    /// same line every RefreshSeconds and nothing on the host ever deployed again. The
    /// exception type below is deliberately one no import filter lists.
    /// </remarks>
    [Fact]
    public async Task RunImportStepIsolatedAsync_WhenImportThrows_DoesNotPropagateAndPublishesTheReason()
    {
        var repository = new FakeOmpHostArtifactRepository();
        var engine = CreateEngine(repository);
        var hostId = Guid.NewGuid();

        await engine.RunImportStepIsolatedAsync(
            hostId,
            _ => throw new NotSupportedException("import root is a reparse point"),
            CancellationToken.None);

        var published = Assert.Single(repository.PublishedRuntimeStatusMessages);
        Assert.Contains("Artifact import failed", published, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("import root is a reparse point", published, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunImportStepIsolatedAsync_WhenImportRecovers_ClearsThePublishedFailure()
    {
        var repository = new FakeOmpHostArtifactRepository();
        var engine = CreateEngine(repository);
        var hostId = Guid.NewGuid();

        await engine.RunImportStepIsolatedAsync(
            hostId,
            _ => throw new NotSupportedException("boom"),
            CancellationToken.None);
        await engine.RunImportStepIsolatedAsync(hostId, _ => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(2, repository.PublishedRuntimeStatusMessages.Count);
        Assert.Contains("recovered", repository.PublishedRuntimeStatusMessages[^1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunImportStepIsolatedAsync_WhenSuccessful_PublishesNothing()
    {
        var repository = new FakeOmpHostArtifactRepository();
        var engine = CreateEngine(repository);

        await engine.RunImportStepIsolatedAsync(Guid.NewGuid(), _ => Task.CompletedTask, CancellationToken.None);

        Assert.Empty(repository.PublishedRuntimeStatusMessages);
    }

    /// <summary>
    /// Cancellation is the lease being lost or the service stopping, not an import failure,
    /// and it must still reach the cycle's own handler (metod section 4.5: name what the
    /// guard has to let through, and test that path).
    /// </summary>
    [Fact]
    public async Task RunImportStepIsolatedAsync_WhenCancelled_PropagatesAndPublishesNothing()
    {
        var repository = new FakeOmpHostArtifactRepository();
        var engine = CreateEngine(repository);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.RunImportStepIsolatedAsync(
                Guid.NewGuid(),
                ct => Task.FromCanceled(ct),
                cts.Token));

        Assert.Empty(repository.PublishedRuntimeStatusMessages);
    }

    private sealed class TestDbException : DbException
    {
        public TestDbException(string message)
            : base(message)
        {
        }
    }
}
