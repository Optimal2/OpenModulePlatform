namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed record TemplateMaterializationResult(
    int ModuleInstanceChanges,
    int AppInstanceChanges);
