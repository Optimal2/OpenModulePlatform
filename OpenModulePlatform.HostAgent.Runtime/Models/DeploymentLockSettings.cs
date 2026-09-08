namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed record DeploymentLockSettings(string Scope, int LeaseSeconds);

public sealed record AppDeploymentLeaseResult(
    bool Acquired, Guid HostId, string HostName, Guid LeaseToken,
    DateTime LeaseUntilUtc, bool TookOverExpiredLease = false);
