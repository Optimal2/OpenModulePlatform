using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class HostAgentEngine
{
    private const int MinimumLeaseSeconds = 30;
    // (R5-D3) Steady-state superseded-service/orphan-directory cleanup shells full
    // sc.exe enumerations; gate it to at most this interval instead of every cycle.
    private static readonly TimeSpan SupersededCleanupInterval = TimeSpan.FromHours(1);
    private readonly object _leaseStateLock = new();
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly IOmpHostArtifactRepository _repository;
    private readonly ArtifactProvisioner _provisioner;
    private readonly ArtifactZipImportService _artifactZipImportService;
    private readonly WebAppDeploymentService _webAppDeploymentService;
    private readonly ServiceAppDeploymentService _serviceAppDeploymentService;
    private readonly HostAgentSelfUpgradeService _selfUpgradeService;
    private readonly HostAgentFileMirrorService _fileMirrorService;
    private readonly WebAppHealthMonitor _webAppHealthMonitor;
    private readonly HostResourceCollector _resourceCollector;
    private readonly HostAgentJobProcessor _jobProcessor;
    private readonly DeploySetConsistencyService _deploySetConsistencyService;
    private readonly HostAgentProcessContext _process;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostAgentEngine> _logger;
    private HostAgentLeaseResult? _activeLease;

    /// <summary>
    /// True while a deploy-set consistency block message is the published status, so
    /// the next consistent cycle knows it has something to clear (R6-D5).
    /// </summary>
    private bool _deploySetBlockPublished;

    /// <summary>
    /// True while an artifact-import failure message is the published status, so the next
    /// clean import knows it has something to clear (R12-F4).
    /// </summary>
    private bool _artifactImportFailurePublished;

    /// <summary>
    /// Consecutive cycles in which the artifact import step has failed. Carried into the
    /// log and the published status so a permanent condition is visibly permanent rather
    /// than looking like a fresh blip every 30 seconds (R12-F4).
    /// </summary>
    private int _consecutiveArtifactImportFailures;
    private DateTimeOffset _lastSupersededCleanupUtc = DateTimeOffset.MinValue;

    public HostAgentEngine(
        IOptionsMonitor<HostAgentSettings> settings,
        IOmpHostArtifactRepository repository,
        ArtifactProvisioner provisioner,
        ArtifactZipImportService artifactZipImportService,
        WebAppDeploymentService webAppDeploymentService,
        ServiceAppDeploymentService serviceAppDeploymentService,
        HostAgentSelfUpgradeService selfUpgradeService,
        HostAgentFileMirrorService fileMirrorService,
        WebAppHealthMonitor webAppHealthMonitor,
        HostResourceCollector resourceCollector,
        HostAgentJobProcessor jobProcessor,
        DeploySetConsistencyService deploySetConsistencyService,
        HostAgentProcessContext process,
        TimeProvider timeProvider,
        ILogger<HostAgentEngine> logger)
    {
        _settings = settings;
        _repository = repository;
        _provisioner = provisioner;
        _artifactZipImportService = artifactZipImportService;
        _webAppDeploymentService = webAppDeploymentService;
        _serviceAppDeploymentService = serviceAppDeploymentService;
        _selfUpgradeService = selfUpgradeService;
        _fileMirrorService = fileMirrorService;
        _webAppHealthMonitor = webAppHealthMonitor;
        _resourceCollector = resourceCollector;
        _jobProcessor = jobProcessor;
        _deploySetConsistencyService = deploySetConsistencyService;
        _process = process;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;

        var hostKey = settings.ResolveHostKey();
        var runtimeMode = _process.RuntimeMode;
        // (R5-D3) Resolve the desired HostAgent upgrade row once per cycle and thread
        // it through the lease-takeover check, superseded-service cleanup and upgrade
        // preparation instead of each re-querying the repository.
        var desiredUpgrade = await _selfUpgradeService.GetDesiredUpgradeAsync(hostKey, cancellationToken);
        var leaseSeconds = Math.Max(MinimumLeaseSeconds, settings.RefreshSeconds * 3);
        var forceLeaseTakeover = runtimeMode == HostAgentRuntimeMode.Takeover
            || _selfUpgradeService.ShouldForceLeaseTakeover(desiredUpgrade);
        var lease = await _repository.TryAcquireHostAgentLeaseAsync(
            hostKey,
            _process.ServiceName,
            runtimeMode,
            forceTakeover: forceLeaseTakeover,
            leaseSeconds,
            cancellationToken);

        if (lease.HostId is null)
        {
            ClearActiveLease();
            _logger.LogWarning(
                "HostAgent skipped cycle because host key is not registered or enabled in the database. HostKey={HostKey}, CurrentService={CurrentService}",
                hostKey,
                _process.ServiceName);
            return;
        }

        if (!lease.Acquired)
        {
            ClearActiveLease();
            _logger.LogInformation(
                "HostAgent skipped cycle because another service owns the host lease. HostKey={HostKey}, CurrentService={CurrentService}, ActiveService={ActiveService}",
                hostKey,
                _process.ServiceName,
                lease.ActiveServiceName);
            return;
        }

        SetActiveLease(lease);

        var leaseLost = new System.Runtime.CompilerServices.StrongBox<bool>(false);
        using var leaseRenewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseRenewal = RenewLeaseUntilCycleCompletesAsync(
            lease,
            leaseSeconds,
            leaseLost,
            leaseRenewalCancellation,
            cancellationToken);

        try
        {
            await _repository.PublishHostAgentRuntimeStateAsync(
                lease.HostId.Value,
                _process,
                runtimeMode,
                artifactId: null,
                AppContext.BaseDirectory,
                isActive: true,
                statusMessage: null,
                leaseRenewalCancellation.Token,
                preserveExistingStatusMessage: true);

            if (runtimeMode == HostAgentRuntimeMode.Takeover)
            {
                await _selfUpgradeService.CompleteTakeoverAsync(hostKey, lease.HostId.Value, leaseRenewalCancellation.Token);
            }
            else if (desiredUpgrade is not null && ShouldRunSupersededCleanup())
            {
                // (R5-D3) Gate steady-state superseded-service/orphan cleanup so it
                // runs at most hourly instead of every cycle; a completed takeover
                // still cleans up immediately via CompleteTakeoverAsync.
                await _selfUpgradeService.CleanupSupersededHostAgentServicesAsync(
                    hostKey,
                    lease.HostId.Value,
                    desiredUpgrade,
                    leaseRenewalCancellation.Token);
                _lastSupersededCleanupUtc = _timeProvider.GetUtcNow();
            }

            if (_process.IsQuiesceRequested)
            {
                _process.MarkQuiesced();
                await _repository.MarkHostAgentQuiescedAsync(lease.HostId.Value, _process.ServiceName, leaseRenewalCancellation.Token);
                _logger.LogInformation("HostAgent is quiesced. HostKey={HostKey}, ServiceName={ServiceName}", hostKey, _process.ServiceName);
                return;
            }

            await _repository.TouchHostHeartbeatAsync(hostKey, leaseRenewalCancellation.Token);

            await ImportPendingArtifactsIsolatedAsync(lease.HostId.Value, leaseRenewalCancellation.Token);

            if (settings.ProcessHostDeployments)
            {
                await ProcessNextHostDeploymentAsync(hostKey, leaseRenewalCancellation.Token);
            }

            if (settings.MaterializeTemplates)
            {
                var materialization = await _repository.MaterializeTemplatesForHostAsync(hostKey, null, leaseRenewalCancellation.Token);
                if (materialization.ModuleInstanceChanges > 0 || materialization.AppInstanceChanges > 0)
                {
                    _logger.LogInformation(
                        "Materialized template topology. HostKey={HostKey}, ModuleInstanceChanges={ModuleInstanceChanges}, AppInstanceChanges={AppInstanceChanges}",
                        hostKey,
                        materialization.ModuleInstanceChanges,
                        materialization.AppInstanceChanges);
                }
            }

            var artifacts = await _repository.GetDesiredArtifactsAsync(
                hostKey,
                settings.ProvisionAppInstanceArtifacts,
                settings.ProvisionExplicitRequirements,
                settings.MaxArtifactsPerCycle,
                leaseRenewalCancellation.Token);

            _logger.LogInformation(
                "Resolved desired artifacts. HostKey={HostKey}, Count={Count}",
                hostKey,
                artifacts.Count);

            // (R5-D7) The desired-artifact query is capped at MaxArtifactsPerCycle
            // with a deterministic order, so when more artifacts are desired than the
            // cap the same tail is excluded every cycle and never provisions. Surface
            // the truncation so it is not silent.
            if (settings.MaxArtifactsPerCycle > 0 && artifacts.Count >= settings.MaxArtifactsPerCycle)
            {
                _logger.LogWarning(
                    "Desired artifact count reached the per-cycle cap; additional desired artifacts may be truncated and will not provision this cycle. HostKey={HostKey}, MaxArtifactsPerCycle={MaxArtifactsPerCycle}. Increase HostAgent:MaxArtifactsPerCycle if this persists.",
                    hostKey,
                    settings.MaxArtifactsPerCycle);
            }

            var consistencySummary = await _deploySetConsistencyService.CheckAsync(
                hostKey,
                artifacts,
                leaseRenewalCancellation.Token);
            var deploySetWarningsByModuleInstanceKey = BuildDeploySetWarningsByModuleInstanceKey(consistencySummary);
            // (R5-D11) In Block mode, scope the block to the affected module-instances'
            // deployments instead of throwing and aborting the whole cycle. Artifact
            // provisioning, self-upgrade, job processing and telemetry below still run;
            // the block reason is published into the host runtime state and onto each
            // skipped deployment result.
            var blockedModuleInstanceKeys = _deploySetConsistencyService.GetBlockedModuleInstanceKeys(consistencySummary);
            if (blockedModuleInstanceKeys.Count > 0)
            {
                await PublishDeploySetBlockRuntimeStateAsync(
                    lease.HostId.Value,
                    consistencySummary,
                    leaseRenewalCancellation.Token);
                _deploySetBlockPublished = true;
            }
            else if (_deploySetBlockPublished)
            {
                // Clear the block text once the artifact set is consistent again.
                // The steady-state runtime-state publish passes
                // preserveExistingStatusMessage: true, so nothing else ever
                // overwrites it and the Portal kept showing "Block is active"
                // indefinitely after the set was repaired -- the old
                // throw-and-abort behaviour self-cleared, this one did not (R6-D5).
                await PublishDeploySetBlockResolvedRuntimeStateAsync(
                    lease.HostId.Value,
                    leaseRenewalCancellation.Token);
                _deploySetBlockPublished = false;
            }

            foreach (var artifact in artifacts)
            {
                leaseRenewalCancellation.Token.ThrowIfCancellationRequested();
                await EnsureAndPublishAsync(artifact, leaseRenewalCancellation.Token);
            }

            await _webAppDeploymentService.DeployDesiredWebAppsAsync(
                hostKey,
                deploySetWarningsByModuleInstanceKey,
                blockedModuleInstanceKeys,
                leaseRenewalCancellation.Token);
            await _webAppHealthMonitor.ProbePortalAsync(
                lease.HostId.Value,
                recycleIfUnhealthy: false,
                leaseRenewalCancellation.Token);
            await _serviceAppDeploymentService.DeployDesiredServiceAppsAsync(
                hostKey,
                deploySetWarningsByModuleInstanceKey,
                blockedModuleInstanceKeys,
                leaseRenewalCancellation.Token);
            await _selfUpgradeService.CheckAndPrepareUpgradeAsync(hostKey, lease.HostId.Value, desiredUpgrade, leaseRenewalCancellation.Token);
            await _fileMirrorService.MirrorConfiguredFilesAsync(leaseRenewalCancellation.Token);

            if (settings.ProcessHostAgentJobs)
            {
                await _jobProcessor.ProcessPendingJobsAsync(
                    hostKey,
                    _process.ServiceName,
                    settings.MaxHostAgentJobsPerCycle,
                    leaseRenewalCancellation.Token);
            }

            await _resourceCollector.CollectAndPersistAsync(lease.HostId.Value, leaseRenewalCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && leaseLost.Value)
        {
            await HandleLostLeaseAsync(lease, cancellationToken);
        }
        finally
        {
            await StopLeaseRenewalAsync(leaseRenewalCancellation, leaseRenewal);
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        var activeLease = TakeActiveLease();
        if (activeLease?.HostId is null)
        {
            return;
        }

        try
        {
            await _repository.PublishHostAgentRuntimeStateAsync(
                activeLease.HostId.Value,
                _process,
                _process.RuntimeMode,
                artifactId: null,
                AppContext.BaseDirectory,
                isActive: false,
                statusMessage: "HostAgent stopped.",
                cancellationToken,
                preserveExistingStatusMessage: false);
        }
        catch (Exception ex) when (IsExpectedShutdownFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to publish inactive HostAgent runtime state during shutdown. HostId={HostId}, ServiceName={ServiceName}",
                activeLease.HostId.Value,
                _process.ServiceName);
        }

        try
        {
            await _repository.ReleaseHostAgentLeaseAsync(
                activeLease.HostId.Value,
                _process.ServiceName,
                // Only release OUR lease: a slow shutdown must never delete a row a
                // successor process has already acquired under a new token (R6-D3).
                activeLease.LeaseToken,
                cancellationToken);
        }
        catch (Exception ex) when (IsExpectedShutdownFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to release HostAgent lease during shutdown. HostId={HostId}, ServiceName={ServiceName}",
                activeLease.HostId.Value,
                _process.ServiceName);
        }
    }

    public async Task<ArtifactProvisioningResult> EnsureArtifactByIdAsync(
        int artifactId,
        string? desiredLocalPath,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;

        var hostKey = settings.ResolveHostKey();
        await _repository.TouchHostHeartbeatAsync(hostKey, cancellationToken);

        var artifact = await _repository.GetArtifactByIdAsync(hostKey, artifactId, desiredLocalPath, cancellationToken);
        if (artifact is null)
        {
            return ArtifactProvisioningResult.Failed(
                ArtifactProvisioningState.Failed,
                string.Empty,
                $"Artifact '{artifactId}' could not be resolved for host '{hostKey}'.");
        }

        return await EnsureAndPublishAsync(artifact, cancellationToken);
    }

    internal async Task ProcessNextHostDeploymentAsync(
        string hostKey,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        var leaseSeconds = Math.Max(MinimumLeaseSeconds, settings.HostDeploymentLeaseSeconds);
        var maxAttempts = Math.Max(1, settings.HostDeploymentMaxAttempts);

        var deployment = await _repository.TryClaimNextHostDeploymentAsync(
            hostKey,
            _process.ServiceName,
            leaseSeconds,
            maxAttempts,
            cancellationToken);
        if (deployment is null)
        {
            return;
        }

        _logger.LogInformation(
            "Claimed host deployment. HostKey={HostKey}, HostDeploymentId={HostDeploymentId}, HostTemplateKey={HostTemplateKey}",
            hostKey,
            deployment.HostDeploymentId,
            deployment.HostTemplateKey);

        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseRenewal = RenewHostDeploymentLeaseUntilProcessingCompletesAsync(
            deployment,
            leaseSeconds,
            processingCancellation,
            cancellationToken);

        try
        {
            var materialization = await _repository.MaterializeTemplatesForHostAsync(
                hostKey,
                deployment.HostTemplateId,
                processingCancellation.Token);

            var message =
                $"Template materialization completed. Module instance changes: {materialization.ModuleInstanceChanges}; app instance changes: {materialization.AppInstanceChanges}.";

            // Honour the rowcount: a lost lease means the deployment was re-claimed
            // under a new token and this completion updated nothing, so claiming
            // success in the log would be false (R6-D8).
            var completed = await _repository.CompleteHostDeploymentAsync(
                deployment.HostDeploymentId,
                deployment.LeaseToken,
                succeeded: true,
                outcomeMessage: message,
                processingCancellation.Token);

            if (completed)
            {
                _logger.LogInformation(
                    "Completed host deployment. HostKey={HostKey}, HostDeploymentId={HostDeploymentId}, ModuleInstanceChanges={ModuleInstanceChanges}, AppInstanceChanges={AppInstanceChanges}",
                    hostKey,
                    deployment.HostDeploymentId,
                    materialization.ModuleInstanceChanges,
                    materialization.AppInstanceChanges);
            }
            else
            {
                _logger.LogWarning(
                    "Host deployment completion did not update any row; the lease was lost and the deployment re-claimed elsewhere. HostKey={HostKey}, HostDeploymentId={HostDeploymentId}",
                    hostKey,
                    deployment.HostDeploymentId);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && processingCancellation.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Host deployment processing stopped because the deployment lease is no longer owned by this process. HostKey={HostKey}, HostDeploymentId={HostDeploymentId}",
                hostKey,
                deployment.HostDeploymentId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Host deployment processing stopped because HostAgent is shutting down. HostKey={HostKey}, HostDeploymentId={HostDeploymentId}",
                hostKey,
                deployment.HostDeploymentId);

            throw;
        }
        catch (Exception ex) when (IsExpectedDeploymentFailure(ex))
        {
            _logger.LogError(
                ex,
                "Host deployment failed. HostKey={HostKey}, HostDeploymentId={HostDeploymentId}",
                hostKey,
                deployment.HostDeploymentId);

            await _repository.CompleteHostDeploymentAsync(
                deployment.HostDeploymentId,
                deployment.LeaseToken,
                succeeded: false,
                outcomeMessage: ex.Message,
                processingCancellation.Token);
        }
        finally
        {
            await StopHostDeploymentLeaseRenewalAsync(processingCancellation, leaseRenewal);
        }
    }

    internal async Task RenewHostDeploymentLeaseUntilProcessingCompletesAsync(
        HostDeploymentWorkItem deployment,
        int leaseSeconds,
        CancellationTokenSource processingCancellation,
        CancellationToken hostAgentCancellationToken)
    {
        var renewalInterval = TimeSpan.FromSeconds(Math.Clamp(leaseSeconds / 3, 10, 120));
        while (!processingCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(renewalInterval, _timeProvider, processingCancellation.Token);
                var renewed = await _repository.RenewHostDeploymentLeaseAsync(
                    deployment.HostDeploymentId,
                    deployment.LeaseToken,
                    leaseSeconds,
                    processingCancellation.Token);

                if (!renewed)
                {
                    _logger.LogWarning(
                        "Host deployment lease renewal did not update a running deployment row. Cancelling local processing. HostDeploymentId={HostDeploymentId}",
                        deployment.HostDeploymentId);
                    await processingCancellation.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (hostAgentCancellationToken.IsCancellationRequested || processingCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsExpectedLeaseRenewalFailure(ex))
            {
                // Lease renewal is best-effort: log transient repository/SQL failures
                // and retry on the next renewal interval while the deployment still runs.
                _logger.LogWarning(
                    ex,
                    "Host deployment lease renewal failed. The next renewal attempt will retry while the deployment is still running. HostDeploymentId={HostDeploymentId}",
                    deployment.HostDeploymentId);
            }
        }
    }

    /// <remarks>
    /// Same finally-block hazard as StopLeaseRenewalAsync. Here it also matters that
    /// ProcessNextHostDeploymentAsync rethrows OperationCanceledException to signal a clean
    /// shutdown: a throw from this cleanup replaced that signal, so a normal stop was logged
    /// as a critical failure (R8-P4-4).
    /// </remarks>
    private async Task StopHostDeploymentLeaseRenewalAsync(
        CancellationTokenSource processingCancellation,
        Task leaseRenewal)
    {
        await processingCancellation.CancelAsync();
        try
        {
            await leaseRenewal;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Host deployment lease renewal ended with an error while stopping.");
        }
    }

    private async Task<bool> RenewLeaseUntilCycleCompletesAsync(
        HostAgentLeaseResult lease,
        int leaseSeconds,
        System.Runtime.CompilerServices.StrongBox<bool> leaseLost,
        CancellationTokenSource cycleCancellation,
        CancellationToken hostCancellationToken)
    {
        if (!lease.HostId.HasValue || !lease.LeaseToken.HasValue)
        {
            return false;
        }

        var renewalInterval = TimeSpan.FromSeconds(Math.Clamp(leaseSeconds / 3, 10, 120));
        while (!cycleCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(renewalInterval, _timeProvider, cycleCancellation.Token);
                var renewed = await _repository.RenewHostAgentLeaseAsync(
                    lease.HostId.Value,
                    lease.LeaseToken.Value,
                    leaseSeconds,
                    cycleCancellation.Token);

                if (!renewed)
                {
                    leaseLost.Value = true;
                    _logger.LogWarning(
                        "HostAgent host lease renewal did not update the active lease row. Cancelling the current cycle. HostId={HostId}, ServiceName={ServiceName}",
                        lease.HostId.Value,
                        _process.ServiceName);
                    await cycleCancellation.CancelAsync();
                    return true;
                }
            }
            catch (OperationCanceledException) when (hostCancellationToken.IsCancellationRequested || cycleCancellation.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (IsExpectedLeaseRenewalFailure(ex))
            {
                // Host lease renewal is best-effort: log transient repository/SQL failures
                // and retry on the next renewal interval while the cycle still runs.
                _logger.LogWarning(
                    ex,
                    "HostAgent host lease renewal failed. The next renewal attempt will retry while the current cycle continues. HostId={HostId}, ServiceName={ServiceName}",
                    lease.HostId.Value,
                    _process.ServiceName);
            }
        }

        return false;
    }

    private static bool IsExpectedLeaseRenewalFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or DbException
            or UnauthorizedAccessException
            or TimeoutException;

    private async Task HandleLostLeaseAsync(HostAgentLeaseResult lease, CancellationToken cancellationToken)
    {
        ClearActiveLease();

        if (!lease.HostId.HasValue)
        {
            return;
        }

        try
        {
            await _repository.PublishHostAgentRuntimeStateAsync(
                lease.HostId.Value,
                _process,
                _process.RuntimeMode,
                artifactId: null,
                AppContext.BaseDirectory,
                isActive: false,
                statusMessage: "HostAgent lost its host lease during the current cycle.",
                cancellationToken,
                preserveExistingStatusMessage: false);
        }
        catch (Exception ex) when (IsExpectedShutdownFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to publish inactive HostAgent runtime state after losing the host lease. HostId={HostId}, ServiceName={ServiceName}",
                lease.HostId.Value,
                _process.ServiceName);
        }
    }

    /// <summary>
    /// Awaits a cancelled lease renewal and never lets its fault propagate.
    /// </summary>
    /// <remarks>
    /// Called from a finally block, where a throw REPLACES the in-flight exception. The
    /// renewal loop's own filter covers only a handful of types, so anything else faulted the
    /// task and the rethrow here masked the real cycle outcome and skipped the rest of the
    /// cleanup. WorkerProcessHostedService already does this correctly with SuppressThrowing
    /// (R8-P4-4).
    /// </remarks>
    private async Task StopLeaseRenewalAsync(
        CancellationTokenSource cycleCancellation,
        Task<bool> leaseRenewal)
    {
        await cycleCancellation.CancelAsync();
        try
        {
            await leaseRenewal;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HostAgent lease renewal ended with an error while stopping.");
        }
    }

    private async Task<ArtifactProvisioningResult> EnsureAndPublishAsync(
        ArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        ArtifactProvisioningResult result;
        try
        {
            result = await _provisioner.EnsureAsync(artifact, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            result = CreateProvisioningFailure(artifact, ex);
        }
        catch (IOException ex)
        {
            result = CreateProvisioningFailure(artifact, ex);
        }
        catch (DbException ex)
        {
            result = CreateProvisioningFailure(artifact, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            result = CreateProvisioningFailure(artifact, ex);
        }

        await _repository.PublishResultAsync(artifact, result, cancellationToken);

        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "Artifact provisioned. ArtifactId={ArtifactId}, Version={Version}, LocalPath={LocalPath}",
                artifact.ArtifactId,
                artifact.Version,
                result.LocalPath);
        }
        else
        {
            _logger.LogWarning(
                "Artifact provisioning did not succeed. ArtifactId={ArtifactId}, Version={Version}, State={State}, Error={Error}",
                artifact.ArtifactId,
                artifact.Version,
                result.State,
                result.ErrorMessage);
        }

        return result;
    }

    private ArtifactProvisioningResult CreateProvisioningFailure(ArtifactDescriptor artifact, Exception exception)
    {
        _logger.LogError(
            exception,
            "Artifact provisioning failed. ArtifactId={ArtifactId}, Version={Version}",
            artifact.ArtifactId,
            artifact.Version);

        return ArtifactProvisioningResult.Failed(
            ArtifactProvisioningState.Failed,
            string.Empty,
            exception.Message);
    }

    /// <summary>
    /// Deployment faults the cycle records against the deployment row instead of letting
    /// them abort the whole HostAgent cycle.
    /// </summary>
    /// <remarks>
    /// R5-D1/R7-D2. Three copies of this list have drifted apart. R7-D2 added
    /// TimeoutException and Win32Exception to the two in the deployment services, and
    /// R8-P4-10 finished the recovery pair, but this one -- the outermost of the three,
    /// the one that decides whether the whole cycle survives -- never got them. It sits
    /// above both services, so anything they let through reaches it, and an escape here
    /// skips the Portal health probe, service-app deploy, self-upgrade, file mirroring,
    /// job processing and telemetry for that cycle with no per-deployment result
    /// published. DbException stays: only this copy wraps the database work.
    /// </remarks>
    private static bool IsExpectedDeploymentFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or DbException
            or UnauthorizedAccessException
            or TimeoutException
            or System.ComponentModel.Win32Exception
            or System.Management.ManagementException
            or System.Runtime.InteropServices.COMException
            || (exception is System.Reflection.TargetInvocationException invocation
                && invocation.InnerException is not null
                && IsExpectedDeploymentFailure(invocation.InnerException));

    private static bool IsExpectedShutdownFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or DbException
            or UnauthorizedAccessException
            or TimeoutException;

    private bool ShouldRunSupersededCleanup()
        => _timeProvider.GetUtcNow() - _lastSupersededCleanupUtc >= SupersededCleanupInterval;

    // (R5-D11) Record the deploy-set consistency block in the host runtime state so
    // it is visible in Portal, instead of only appearing as a repeated "cycle
    // failed" log from the previous throw-and-abort behavior.
    /// <summary>
    /// Replaces a previously published deploy-set block message once the artifact
    /// set is consistent again (R6-D5).
    /// </summary>
    private async Task PublishDeploySetBlockResolvedRuntimeStateAsync(
        Guid hostId,
        CancellationToken cancellationToken)
    {
        const string message = "Deploy-set consistency block resolved; deployments are converging normally.";
        _logger.LogInformation("{DeploySetBlockResolvedMessage}", message);

        try
        {
            await _repository.PublishHostAgentRuntimeStateAsync(
                hostId,
                _process,
                _process.RuntimeMode,
                artifactId: null,
                AppContext.BaseDirectory,
                isActive: true,
                message,
                cancellationToken);
        }
        catch (Exception ex) when (IsExpectedShutdownFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to clear the deploy-set consistency block from HostAgent runtime state. HostId={HostId}",
                hostId);
        }
    }

    /// <summary>
    /// Runs the artifact zip import step so that no failure inside it can end the cycle
    /// (R12-F4).
    /// </summary>
    /// <remarks>
    /// ImportPendingAsync is the first thing the cycle does after the heartbeat, and every
    /// deployment, provisioning, self-upgrade, file-mirroring and telemetry step sits below
    /// it. It also throws before its own per-file try/catch is reached: the reparse-point
    /// checks on the import, processed and failed roots run at the very top, so a junction
    /// on any of the three -- which needs no privilege to create -- ended the cycle at the
    /// same line every 30 seconds, forever, and not one app on the host converged. Any
    /// exception type its internal IsExpectedImportFailure does not list does the same. The
    /// import step is not allowed to be that gate. Cancellation still propagates: that is
    /// the lease being lost or the service stopping, and the caller handles it.
    /// The signal is deliberately loud and repeated -- an error log every cycle with the
    /// consecutive count, plus the reason published into the host runtime state where the
    /// Portal shows it -- because the failure mode this replaces was silent-but-fatal and
    /// silent-but-harmless would be no better.
    /// </remarks>
    private Task ImportPendingArtifactsIsolatedAsync(Guid hostId, CancellationToken cancellationToken)
        => RunImportStepIsolatedAsync(hostId, _artifactZipImportService.ImportPendingAsync, cancellationToken);

    /// <summary>
    /// The isolation itself, taking the import call as a delegate so it can be driven with a
    /// failing import in tests -- ArtifactZipImportService is a concrete type over a
    /// concrete repository and cannot be substituted.
    /// </summary>
    internal async Task RunImportStepIsolatedAsync(
        Guid hostId,
        Func<CancellationToken, Task> importPendingAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await importPendingAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _consecutiveArtifactImportFailures++;
            _logger.LogError(
                ex,
                "HostAgent artifact import failed; the rest of the cycle continues. ConsecutiveFailures={ConsecutiveFailures}, HostId={HostId}",
                _consecutiveArtifactImportFailures,
                hostId);

            await PublishArtifactImportFailureRuntimeStateAsync(hostId, ex, cancellationToken);
            return;
        }

        if (_consecutiveArtifactImportFailures > 0)
        {
            _logger.LogInformation(
                "HostAgent artifact import recovered after {ConsecutiveFailures} failed cycle(s). HostId={HostId}",
                _consecutiveArtifactImportFailures,
                hostId);
            _consecutiveArtifactImportFailures = 0;
        }

        if (_artifactImportFailurePublished)
        {
            await PublishArtifactImportRecoveredRuntimeStateAsync(hostId, cancellationToken);
        }
    }

    private async Task PublishArtifactImportFailureRuntimeStateAsync(
        Guid hostId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var message =
            $"Artifact import failed on {_consecutiveArtifactImportFailures} consecutive cycle(s) and no packages are being imported; deployments continue against the artifacts already registered. " +
            $"Reason: {exception.Message} " +
            "Check the HostAgent import, processed and failed folders (a junction or symlink on any of them is refused by design) and the HostAgent log.";
        try
        {
            await _repository.PublishHostAgentRuntimeStateAsync(
                hostId,
                _process,
                _process.RuntimeMode,
                artifactId: null,
                AppContext.BaseDirectory,
                isActive: true,
                message,
                cancellationToken);
            _artifactImportFailurePublished = true;
        }
        catch (Exception ex) when (IsExpectedShutdownFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to publish the artifact import failure into HostAgent runtime state. HostId={HostId}",
                hostId);
        }
    }

    private async Task PublishArtifactImportRecoveredRuntimeStateAsync(Guid hostId, CancellationToken cancellationToken)
    {
        const string message = "Artifact import recovered; pending packages are being imported normally again.";
        _logger.LogInformation("{ArtifactImportRecoveredMessage}", message);

        try
        {
            await _repository.PublishHostAgentRuntimeStateAsync(
                hostId,
                _process,
                _process.RuntimeMode,
                artifactId: null,
                AppContext.BaseDirectory,
                isActive: true,
                message,
                cancellationToken);
            _artifactImportFailurePublished = false;
        }
        catch (Exception ex) when (IsExpectedShutdownFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to clear the artifact import failure from HostAgent runtime state. HostId={HostId}",
                hostId);
        }
    }

    private async Task PublishDeploySetBlockRuntimeStateAsync(
        Guid hostId,
        DeploySetConsistencyCheckSummary summary,
        CancellationToken cancellationToken)
    {
        var first = summary.Deviations[0];
        var message =
            $"Deploy-set consistency Block is active: {summary.DeviationCount} module instance(s) have version mismatches and their deployments are skipped this cycle. " +
            $"First: ModuleInstance={first.ModuleInstanceKey} ({first.ModuleKey}), Set={first.SetKey}, Versions={first.ActualVersions ?? "unknown"}. " +
            "Rebuild and import a consistent artifact set, or switch HostAgent:DeploySetConsistencyMode to Warn.";
        _logger.LogWarning("{DeploySetBlockMessage}", message);

        try
        {
            await _repository.PublishHostAgentRuntimeStateAsync(
                hostId,
                _process,
                _process.RuntimeMode,
                artifactId: null,
                AppContext.BaseDirectory,
                isActive: true,
                message,
                cancellationToken);
        }
        catch (Exception ex) when (IsExpectedShutdownFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to publish deploy-set consistency block into HostAgent runtime state. HostId={HostId}",
                hostId);
        }
    }

    private static IReadOnlyDictionary<string, string> BuildDeploySetWarningsByModuleInstanceKey(
        DeploySetConsistencyCheckSummary summary)
    {
        if (!summary.HasDeviations)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return summary.Deviations
            .GroupBy(d => d.ModuleInstanceKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => string.Join(
                    Environment.NewLine,
                    g.Select(d =>
                        $"Deploy-set inconsistency in set '{d.SetKey}' for module instance '{d.ModuleInstanceKey}' " +
                        $"({d.ModuleKey}): versions differ — {d.ActualVersions ?? "unknown"}. " +
                        "All artifacts in the set should use the same version.")),
                StringComparer.OrdinalIgnoreCase);
    }

    private void SetActiveLease(HostAgentLeaseResult lease)
    {
        lock (_leaseStateLock)
        {
            _activeLease = lease;
        }
    }

    private HostAgentLeaseResult? TakeActiveLease()
    {
        lock (_leaseStateLock)
        {
            var lease = _activeLease;
            _activeLease = null;
            return lease;
        }
    }

    private void ClearActiveLease()
    {
        lock (_leaseStateLock)
        {
            _activeLease = null;
        }
    }
}
