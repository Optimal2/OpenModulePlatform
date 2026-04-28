using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class HostAgentEngine
{
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ArtifactProvisioner _provisioner;
    private readonly ILogger<HostAgentEngine> _logger;

    public HostAgentEngine(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        ArtifactProvisioner provisioner,
        ILogger<HostAgentEngine> logger)
    {
        _settings = settings;
        _repository = repository;
        _provisioner = provisioner;
        _logger = logger;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        settings.Validate();

        var hostKey = settings.ResolveHostKey();
        await _repository.TouchHostHeartbeatAsync(hostKey, cancellationToken);

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

    private async Task<ArtifactProvisioningResult> EnsureAndPublishAsync(
        ArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        ArtifactProvisioningResult result;
        try
        {
            result = await _provisioner.EnsureAsync(artifact, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Artifact provisioning failed. ArtifactId={ArtifactId}, Version={Version}",
                artifact.ArtifactId,
                artifact.Version);

            result = ArtifactProvisioningResult.Failed(
                ArtifactProvisioningState.Failed,
                string.Empty,
                ex.Message);
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
}
