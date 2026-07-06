using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class HostAgentEngine
{
    private const int MinimumLeaseSeconds = 30;
    private readonly object _leaseStateLock = new();
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ArtifactProvisioner _provisioner;
    private readonly ArtifactZipImportService _artifactZipImportService;
    private readonly WebAppDeploymentService _webAppDeploymentService;
    private readonly ServiceAppDeploymentService _serviceAppDeploymentService;
    private readonly HostAgentSelfUpgradeService _selfUpgradeService;
    private readonly HostAgentFileMirrorService _fileMirrorService;
    private readonly WebAppHealthMonitor _webAppHealthMonitor;
    private readonly HostResourceCollector _resourceCollector;
    private readonly HostAgentJobProcessor _jobProcessor;
    private readonly HostAgentProcessContext _process;
    private readonly ILogger<HostAgentEngine> _logger;
    private HostAgentLeaseResult? _activeLease;

    public HostAgentEngine(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        ArtifactProvisioner provisioner,
        ArtifactZipImportService artifactZipImportService,
        WebAppDeploymentService webAppDeploymentService,
        ServiceAppDeploymentService serviceAppDeploymentService,
        HostAgentSelfUpgradeService selfUpgradeService,
        HostAgentFileMirrorService fileMirrorService,
        WebAppHealthMonitor webAppHealthMonitor,
        HostResourceCollector resourceCollector,
        HostAgentJobProcessor jobProcessor,
        HostAgentProcessContext process,
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
        _process = process;
        _logger = logger;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        settings.Validate();

        var hostKey = settings.ResolveHostKey();
        var runtimeMode = _process.RuntimeMode;
        var leaseSeconds = Math.Max(MinimumLeaseSeconds, settings.RefreshSeconds * 3);
        var forceLeaseTakeover = runtimeMode == HostAgentRuntimeMode.Takeover
            || await _selfUpgradeService.ShouldForceLeaseTakeoverAsync(hostKey, cancellationToken);
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
            else
            {
                await _selfUpgradeService.CleanupSupersededHostAgentServicesAsync(hostKey, lease.HostId.Value, leaseRenewalCancellation.Token);
            }

            if (_process.IsQuiesceRequested)
            {
                _process.MarkQuiesced();
                await _repository.MarkHostAgentQuiescedAsync(lease.HostId.Value, _process.ServiceName, leaseRenewalCancellation.Token);
                _logger.LogInformation("HostAgent is quiesced. HostKey={HostKey}, ServiceName={ServiceName}", hostKey, _process.ServiceName);
                return;
            }

            await _repository.TouchHostHeartbeatAsync(hostKey, leaseRenewalCancellation.Token);

            await _artifactZipImportService.ImportPendingAsync(leaseRenewalCancellation.Token);

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

            foreach (var artifact in artifacts)
            {
                leaseRenewalCancellation.Token.ThrowIfCancellationRequested();
                await EnsureAndPublishAsync(artifact, leaseRenewalCancellation.Token);
            }

            await _webAppDeploymentService.DeployDesiredWebAppsAsync(hostKey, leaseRenewalCancellation.Token);
            await _webAppHealthMonitor.ProbePortalAsync(
                lease.HostId.Value,
                recycleIfUnhealthy: false,
                leaseRenewalCancellation.Token);
            await _serviceAppDeploymentService.DeployDesiredServiceAppsAsync(hostKey, leaseRenewalCancellation.Token);
            await _selfUpgradeService.CheckAndPrepareUpgradeAsync(hostKey, lease.HostId.Value, leaseRenewalCancellation.Token);
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
        settings.Validate();

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

    private async Task ProcessNextHostDeploymentAsync(
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

            await _repository.CompleteHostDeploymentAsync(
                deployment.HostDeploymentId,
                deployment.LeaseToken,
                succeeded: true,
                outcomeMessage: message,
                processingCancellation.Token);

            _logger.LogInformation(
                "Completed host deployment. HostKey={HostKey}, HostDeploymentId={HostDeploymentId}, ModuleInstanceChanges={ModuleInstanceChanges}, AppInstanceChanges={AppInstanceChanges}",
                hostKey,
                deployment.HostDeploymentId,
                materialization.ModuleInstanceChanges,
                materialization.AppInstanceChanges);
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

    private async Task RenewHostDeploymentLeaseUntilProcessingCompletesAsync(
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
                await Task.Delay(renewalInterval, processingCancellation.Token);
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

    private static async Task StopHostDeploymentLeaseRenewalAsync(
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
                await Task.Delay(renewalInterval, cycleCancellation.Token);
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

    private static async Task StopLeaseRenewalAsync(
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

    private static bool IsExpectedDeploymentFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or DbException
            or UnauthorizedAccessException;

    private static bool IsExpectedShutdownFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or DbException
            or UnauthorizedAccessException
            or TimeoutException;

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
