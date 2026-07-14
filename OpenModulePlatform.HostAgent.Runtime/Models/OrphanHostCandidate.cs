namespace OpenModulePlatform.HostAgent.Runtime.Models;

/// <summary>
/// Describes a host row that matches the conservative orphan-host criteria used by
/// the maintenance scan. The counts are exposed so the finder can produce accurate
/// safety notes before any cleanup is considered.
/// </summary>
public sealed class OrphanHostCandidate
{
    public Guid HostId { get; set; }

    public string HostKey { get; set; } = string.Empty;

    public Guid InstanceId { get; set; }

    public string? Environment { get; set; }

    public int AppInstanceCount { get; set; }

    public int HostArtifactRequirementCount { get; set; }

    public int HostArtifactStateCount { get; set; }

    public int HostAppDeploymentStateCount { get; set; }
}
