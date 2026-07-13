namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed record ModuleDefinitionConsistentArtifactSetEntry(
    string SetKey,
    string? Description,
    string VersionMatchRule,
    IReadOnlyList<ModuleDefinitionConsistentArtifactSetMemberEntry> Members);
