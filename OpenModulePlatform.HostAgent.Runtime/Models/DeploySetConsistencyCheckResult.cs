namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed record DeploySetConsistencyCheckResult(
    Guid ModuleInstanceId,
    string ModuleInstanceKey,
    string ModuleKey,
    string SetKey,
    bool IsConsistent,
    string? ExpectedVersion,
    string? ActualVersions,
    int MatchedMemberCount,
    int TotalMemberCount);
