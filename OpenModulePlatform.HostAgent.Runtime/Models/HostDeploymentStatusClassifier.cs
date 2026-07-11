namespace OpenModulePlatform.HostAgent.Runtime.Models;

public static class HostDeploymentStatusClassifier
{
    public static bool IsValidTransition(byte fromStatus, byte toStatus)
    {
        if (fromStatus == toStatus)
        {
            return true;
        }

        return (fromStatus, toStatus) switch
        {
            (HostDeploymentStatuses.Pending, HostDeploymentStatuses.Running) => true,
            (HostDeploymentStatuses.Running, HostDeploymentStatuses.Succeeded) => true,
            (HostDeploymentStatuses.Running, HostDeploymentStatuses.Failed) => true,
            _ => false
        };
    }

    public static string GetDisplayName(byte status)
        => status switch
        {
            HostDeploymentStatuses.Pending => "Pending",
            HostDeploymentStatuses.Running => "Running",
            HostDeploymentStatuses.Succeeded => "Succeeded",
            HostDeploymentStatuses.Failed => "Failed",
            HostDeploymentStatuses.Warning => "Warning",
            _ => "Unknown"
        };
}
