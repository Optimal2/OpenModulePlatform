namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class DeploymentRuntimeRecoveryCandidate
{
    public Guid AppInstanceId { get; init; }

    public string AppInstanceKey { get; init; } = string.Empty;

    public string TargetPath { get; init; } = string.Empty;

    public string RuntimeName { get; init; } = string.Empty;
}
