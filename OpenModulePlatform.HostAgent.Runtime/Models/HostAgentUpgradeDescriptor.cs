namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class HostAgentUpgradeDescriptor
{
    public Guid HostId { get; init; }

    public int ArtifactId { get; init; }

    public string Version { get; init; } = string.Empty;

    public string PackageType { get; init; } = string.Empty;

    public string? TargetName { get; init; }

    public string? RelativePath { get; init; }

    public string? Sha256 { get; init; }

    public string? ServiceNamePrefix { get; init; }

    public string? InstallRoot { get; init; }

    public string? SourceLocalPath { get; init; }

    public string? ContentSha256 { get; init; }

    public ArtifactDescriptor ToArtifactDescriptor()
        => new()
        {
            HostId = HostId,
            ArtifactId = ArtifactId,
            Version = Version,
            PackageType = PackageType,
            TargetName = TargetName,
            RelativePath = RelativePath,
            Sha256 = Sha256,
            RequirementKey = "host-agent-self-upgrade"
        };
}
