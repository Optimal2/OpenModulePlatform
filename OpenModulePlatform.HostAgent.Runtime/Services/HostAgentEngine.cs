using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class HostAgentEngine
{
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ArtifactProvisioner _provisioner;
    private readonly ArtifactZipImportService _artifactZipImportService;
    private readonly WebAppDeploymentService _webAppDeploymentService;
    private readonly ServiceAppDeploymentService _serviceAppDeploymentService;
    private readonly HostAgentFileMirrorService _fileMirrorService;
    private readonly ILogger<HostAgentEngine> _logger;

    public HostAgentEngine(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        ArtifactProvisioner provisioner,
        ArtifactZipImportService artifactZipImportService,
        WebAppDeploymentService webAppDeploymentService,
        ServiceAppDeploymentService serviceAppDeploymentService,
        HostAgentFileMirrorService fileMirrorService,
        ILogger<HostAgentEngine> logger)
    {
        _settings = settings;
        _repository = repository;
        _provisioner = provisioner;
        _artifactZipImportService = artifactZipImportService;
        _webAppDeploymentService = webAppDeploymentService;
        _serviceAppDeploymentService = serviceAppDeploymentService;
        _fileMirrorService = fileMirrorService;
        _logger = logger;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        settings.Validate();

        var hostKey = settings.ResolveHostKey();
        await _repository.TouchHostHeartbeatAsync(hostKey, cancellationToken);

        await _artifactZipImportService.ImportPendingAsync(cancellationToken);

        if (settings.ProcessHostDeployments)
        {
            await ProcessNextHostDeploymentAsync(hostKey, cancellationToken);
        }

        if (settings.MaterializeTemplates)
        {
            var materialization = await _repository.MaterializeTemplatesForHostAsync(hostKey, null, cancellationToken);
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
            cancellationToken);

        _logger.LogInformation(
            "Resolved desired artifacts. HostKey={HostKey}, Count={Count}",
            hostKey,
            artifacts.Count);

        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureAndPublishAsync(artifact, cancellationToken);
        }

        await _webAppDeploymentService.DeployDesiredWebAppsAsync(hostKey, cancellationToken);
        await _serviceAppDeploymentService.DeployDesiredServiceAppsAsync(hostKey, cancellationToken);
        await _fileMirrorService.MirrorConfiguredFilesAsync(cancellationToken);
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
        var deployment = await _repository.TryClaimNextHostDeploymentAsync(hostKey, cancellationToken);
        if (deployment is null)
        {
            return;
        }

        _logger.LogInformation(
            "Claimed host deployment. HostKey={HostKey}, HostDeploymentId={HostDeploymentId}, HostTemplateKey={HostTemplateKey}",
            hostKey,
            deployment.HostDeploymentId,
            deployment.HostTemplateKey);

        try
        {
            var materialization = await _repository.MaterializeTemplatesForHostAsync(
                hostKey,
                deployment.HostTemplateId,
                cancellationToken);

            var message =
                $"Template materialization completed. Module instance changes: {materialization.ModuleInstanceChanges}; app instance changes: {materialization.AppInstanceChanges}.";

            await _repository.CompleteHostDeploymentAsync(
                deployment.HostDeploymentId,
                succeeded: true,
                outcomeMessage: message,
                cancellationToken);

            _logger.LogInformation(
                "Completed host deployment. HostKey={HostKey}, HostDeploymentId={HostDeploymentId}, ModuleInstanceChanges={ModuleInstanceChanges}, AppInstanceChanges={AppInstanceChanges}",
                hostKey,
                deployment.HostDeploymentId,
                materialization.ModuleInstanceChanges,
                materialization.AppInstanceChanges);
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
                succeeded: false,
                outcomeMessage: ex.Message,
                cancellationToken);
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
}
