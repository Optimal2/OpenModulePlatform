namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed record HostAgentLeaseResult(
    bool Acquired,
    Guid? HostId,
    Guid? LeaseToken,
    string? ActiveServiceName);
