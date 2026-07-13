namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed record ModuleDefinitionConsistentArtifactSetMemberEntry(
    string AppKey,
    string PackageType,
    string? TargetName);
