using System.Data.Common;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class FakeOmpHostArtifactRepository : IOmpHostArtifactRepository
{
    public HostDeploymentWorkItem? NextDeployment { get; set; }

    public TemplateMaterializationResult MaterializeResult { get; set; } = new(0, 0);

    public Exception? MaterializeException { get; set; }

    public bool BlockMaterializeUntilCancelled { get; set; }

    public bool RenewLeaseResult { get; set; } = true;

    public Exception? RenewLeaseException { get; set; }

    public int RenewLeaseCallCount { get; private set; }

    public List<CompletedDeployment> CompletedDeployments { get; } = [];

    public string? LastClaimHostKey { get; private set; }

    public string? LastClaimServiceName { get; private set; }

    public int? LastClaimLeaseSeconds { get; private set; }

    public int? LastClaimMaxAttempts { get; private set; }

    public string GetConfiguredConnectionString()
        => "Server=(local);Database=OpenModulePlatform;Integrated Security=true;TrustServerCertificate=true";

    public Task<HostAgentLeaseResult> TryAcquireHostAgentLeaseAsync(
        string hostKey,
        string serviceName,
        string runtimeMode,
        bool forceTakeover,
        int leaseSeconds,
        CancellationToken ct)
        => Task.FromResult(new HostAgentLeaseResult(true, Guid.NewGuid(), Guid.NewGuid(), serviceName));

    public Task ReleaseHostAgentLeaseAsync(Guid hostId, string serviceName, CancellationToken ct)
        => Task.CompletedTask;

    public Task<bool> RenewHostAgentLeaseAsync(Guid hostId, Guid leaseToken, int leaseSeconds, CancellationToken ct)
        => Task.FromResult(true);

    public Task PublishHostAgentRuntimeStateAsync(
        Guid hostId,
        HostAgentProcessContext process,
        string runtimeMode,
        int? artifactId,
        string? installPath,
        bool isActive,
        string? statusMessage,
        CancellationToken ct,
        bool preserveExistingStatusMessage = false)
        => Task.CompletedTask;

    public Task MarkHostAgentQuiescedAsync(Guid hostId, string serviceName, CancellationToken ct)
        => Task.CompletedTask;

    public Task TouchHostHeartbeatAsync(string hostKey, CancellationToken ct)
        => Task.CompletedTask;

    public async Task<TemplateMaterializationResult> MaterializeTemplatesForHostAsync(
        string hostKey,
        int? hostTemplateId,
        CancellationToken ct)
    {
        if (MaterializeException is not null)
        {
            throw MaterializeException;
        }

        if (BlockMaterializeUntilCancelled)
        {
            var blocker = new TaskCompletionSource();
            using var registration = ct.Register(() => blocker.TrySetCanceled());
            await blocker.Task;
        }

        ct.ThrowIfCancellationRequested();
        return MaterializeResult;
    }

    public Task<IReadOnlyList<ArtifactDescriptor>> GetDesiredArtifactsAsync(
        string hostKey,
        bool includeAppInstanceArtifacts,
        bool includeExplicitRequirements,
        int maxArtifacts,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ArtifactDescriptor>>([]);

    public Task<ArtifactDescriptor?> GetArtifactByIdAsync(
        string hostKey,
        int artifactId,
        string? desiredLocalPath,
        CancellationToken ct)
        => Task.FromResult<ArtifactDescriptor?>(null);

    public Task PublishResultAsync(ArtifactDescriptor artifact, ArtifactProvisioningResult result, CancellationToken ct)
        => Task.CompletedTask;

    public Task<IReadOnlyList<DeploymentRuntimeRecoveryCandidate>> GetWebAppDeploymentRecoveryCandidatesAsync(
        string hostKey,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DeploymentRuntimeRecoveryCandidate>>([]);

    public Task<IReadOnlyList<DeploymentRuntimeRecoveryCandidate>> GetServiceAppDeploymentRecoveryCandidatesAsync(
        string hostKey,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DeploymentRuntimeRecoveryCandidate>>([]);

    public IReadOnlyList<DeploySetConsistencyCheckResult> ConsistencyResults { get; set; } = [];

    public Task<IReadOnlyList<DeploySetConsistencyCheckResult>> GetDeploySetConsistencyResultsAsync(
        string hostKey,
        IReadOnlyList<int> artifactIds,
        CancellationToken ct)
        => Task.FromResult(ConsistencyResults);

    public Task<HostDeploymentWorkItem?> TryClaimNextHostDeploymentAsync(
        string hostKey,
        string serviceName,
        int leaseSeconds,
        int maxAttempts,
        CancellationToken ct)
    {
        LastClaimHostKey = hostKey;
        LastClaimServiceName = serviceName;
        LastClaimLeaseSeconds = leaseSeconds;
        LastClaimMaxAttempts = maxAttempts;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NextDeployment);
    }

    public Task CompleteHostDeploymentAsync(
        long hostDeploymentId,
        Guid leaseToken,
        bool succeeded,
        string outcomeMessage,
        CancellationToken ct)
    {
        CompletedDeployments.Add(new CompletedDeployment(hostDeploymentId, leaseToken, succeeded, outcomeMessage));
        return Task.CompletedTask;
    }

    public Task<bool> RenewHostDeploymentLeaseAsync(
        long hostDeploymentId,
        Guid leaseToken,
        int leaseSeconds,
        CancellationToken ct)
    {
        RenewLeaseCallCount++;
        ct.ThrowIfCancellationRequested();

        if (RenewLeaseException is not null)
        {
            throw RenewLeaseException;
        }

        return Task.FromResult(RenewLeaseResult);
    }

    public sealed record CompletedDeployment(
        long HostDeploymentId,
        Guid LeaseToken,
        bool Succeeded,
        string OutcomeMessage);
}
