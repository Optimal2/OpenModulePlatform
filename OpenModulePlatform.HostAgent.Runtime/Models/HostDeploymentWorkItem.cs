namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed record HostDeploymentWorkItem(
    long HostDeploymentId,
    int? HostTemplateId,
    string? HostTemplateKey);
