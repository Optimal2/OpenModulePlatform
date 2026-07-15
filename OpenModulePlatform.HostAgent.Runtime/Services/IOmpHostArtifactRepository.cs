using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Repository for host-agent artifact, lease, deployment, and runtime state operations.
/// Extracted as an interface to enable deterministic unit testing of <see cref="HostAgentEngine" />.
/// </summary>
public interface IOmpHostArtifactRepository
{
    string GetConfiguredConnectionString();

    Task<HostAgentLeaseResult> TryAcquireHostAgentLeaseAsync(
        string hostKey,
        string serviceName,
        string runtimeMode,
        bool forceTakeover,
        int leaseSeconds,
        CancellationToken ct);

    Task ReleaseHostAgentLeaseAsync(Guid hostId, string serviceName, CancellationToken ct);

    Task<bool> RenewHostAgentLeaseAsync(Guid hostId, Guid leaseToken, int leaseSeconds, CancellationToken ct);

    Task PublishHostAgentRuntimeStateAsync(
        Guid hostId,
        HostAgentProcessContext process,
        string runtimeMode,
        int? artifactId,
        string? installPath,
        bool isActive,
        string? statusMessage,
        CancellationToken ct,
        bool preserveExistingStatusMessage = false);

    Task MarkHostAgentQuiescedAsync(Guid hostId, string serviceName, CancellationToken ct);

    Task TouchHostHeartbeatAsync(string hostKey, CancellationToken ct);

    Task<int> GetEnabledHostCountAsync(CancellationToken ct);

    Task<TemplateMaterializationResult> MaterializeTemplatesForHostAsync(
        string hostKey,
        int? hostTemplateId,
        CancellationToken ct);

    Task<IReadOnlyList<ArtifactDescriptor>> GetDesiredArtifactsAsync(
        string hostKey,
        bool includeAppInstanceArtifacts,
        bool includeExplicitRequirements,
        int maxArtifacts,
        CancellationToken ct);

    Task<ArtifactDescriptor?> GetArtifactByIdAsync(
        string hostKey,
        int artifactId,
        string? desiredLocalPath,
        CancellationToken ct);

    Task PublishResultAsync(ArtifactDescriptor artifact, ArtifactProvisioningResult result, CancellationToken ct);

    Task<IReadOnlyList<DeploymentRuntimeRecoveryCandidate>> GetWebAppDeploymentRecoveryCandidatesAsync(
        string hostKey,
        CancellationToken ct);

    Task<IReadOnlyList<DeploymentRuntimeRecoveryCandidate>> GetServiceAppDeploymentRecoveryCandidatesAsync(
        string hostKey,
        CancellationToken ct);

    Task<IReadOnlyList<DeploySetConsistencyCheckResult>> GetDeploySetConsistencyResultsAsync(
        string hostKey,
        IReadOnlyList<int> artifactIds,
        CancellationToken ct);

    Task<IReadOnlyList<WebAppDeploymentDescriptor>> GetDesiredWebAppDeploymentsAsync(
        string hostKey,
        int maxDeployments,
        CancellationToken ct);

    Task<IReadOnlyList<ServiceAppDeploymentDescriptor>> GetDesiredServiceAppDeploymentsAsync(
        string hostKey,
        int maxDeployments,
        CancellationToken ct);

    Task<IReadOnlyList<ArtifactConfigurationFileDescriptor>> GetArtifactConfigurationFilesAsync(
        int artifactId,
        string hostKey,
        CancellationToken ct);

    Task<IReadOnlyList<string>> GetRequiredConfigRootSectionsAsync(
        int artifactId,
        CancellationToken ct);

    Task PublishAppDeploymentResultAsync(
        WebAppDeploymentDescriptor deployment,
        AppDeploymentResult result,
        CancellationToken ct);

    Task PublishAppDeploymentResultAsync(
        ServiceAppDeploymentDescriptor deployment,
        AppDeploymentResult result,
        CancellationToken ct);

    Task<HostDeploymentWorkItem?> TryClaimNextHostDeploymentAsync(
        string hostKey,
        string serviceName,
        int leaseSeconds,
        int maxAttempts,
        CancellationToken ct);

    Task CompleteHostDeploymentAsync(
        long hostDeploymentId,
        Guid leaseToken,
        bool succeeded,
        string outcomeMessage,
        CancellationToken ct);

    Task<bool> RenewHostDeploymentLeaseAsync(
        long hostDeploymentId,
        Guid leaseToken,
        int leaseSeconds,
        CancellationToken ct);

    Task<bool> EnqueueMaintenanceScanJobAsync(
        string hostKey,
        string? requestedBy,
        CancellationToken ct);

    Task<IReadOnlyList<OrphanHostCandidate>> GetOrphanHostCandidatesAsync(
        Guid currentHostId,
        int maxCandidates,
        CancellationToken ct);
}
