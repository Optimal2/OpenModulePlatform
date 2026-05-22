namespace OpenModulePlatform.HostAgent.Runtime.Models;

public static class HostDeploymentStatuses
{
    public const byte Pending = 0;
    public const byte Running = 1;
    public const byte Succeeded = 2;
    public const byte Failed = 3;
    public const byte Warning = 4;
}

public static class HostAppIdentityCheckStatuses
{
    public const string NotApplicable = "NotApplicable";
    public const string Compliant = "Compliant";
    public const string WaitingForPortalAdminApproval = "WaitingForPortalAdminApproval";
    public const string RepairRequested = "RepairRequested";
    public const string ManualActionRequired = "ManualActionRequired";
    public const string RepairFailed = "RepairFailed";
}
