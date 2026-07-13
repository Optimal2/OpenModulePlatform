namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class ServiceAppDeploymentDescriptor
{
    public Guid HostId { get; init; }

    public string HostKey { get; init; } = string.Empty;

    public Guid AppInstanceId { get; init; }

    public string AppInstanceKey { get; init; } = string.Empty;

    public string ModuleInstanceKey { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? InstallPath { get; init; }

    public string? InstallationName { get; init; }

    public int ArtifactId { get; init; }

    public string Version { get; init; } = string.Empty;

    public string? TargetName { get; init; }

    public string SourceLocalPath { get; init; } = string.Empty;

    public string? ContentSha256 { get; init; }

    public int? DeployedArtifactId { get; init; }

    public byte? DeploymentState { get; init; }

    public string? DeployedSourceLocalPath { get; init; }

    public string? DeployedTargetPath { get; init; }

    public string? DeployedRuntimeName { get; init; }

    public DateTime? IdentityRepairRequestedUtc { get; init; }

    public string? IdentityRepairRequestedBy { get; init; }
}
