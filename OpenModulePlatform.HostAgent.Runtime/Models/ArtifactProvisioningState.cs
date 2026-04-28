namespace OpenModulePlatform.HostAgent.Runtime.Models;

public static class ArtifactProvisioningState
{
    public const byte Unknown = 0;
    public const byte Pending = 1;
    public const byte Succeeded = 2;
    public const byte Failed = 3;
    public const byte HashMismatch = 4;
}
