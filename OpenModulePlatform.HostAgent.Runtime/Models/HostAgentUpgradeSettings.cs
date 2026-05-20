namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class HostAgentUpgradeSettings
{
    public bool IsEnabled { get; set; }

    public string InstallRoot { get; set; } = string.Empty;

    public string ServiceNamePrefix { get; set; } = string.Empty;

    public string ServiceAccountName { get; set; } = string.Empty;

    public string ServiceAccountPassword { get; set; } = string.Empty;

    public int TakeoverStopTimeoutSeconds { get; set; } = 45;

    public bool DeletePreviousServiceAfterTakeover { get; set; } = true;

    public bool StartPreparedService { get; set; } = true;

    public void Validate()
    {
        if (!IsEnabled)
        {
            return;
        }

        if (TakeoverStopTimeoutSeconds < 1)
        {
            throw new InvalidOperationException("HostAgent:SelfUpgrade:TakeoverStopTimeoutSeconds must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(ServiceAccountName)
            && !string.IsNullOrWhiteSpace(ServiceAccountPassword))
        {
            throw new InvalidOperationException("HostAgent:SelfUpgrade:ServiceAccountPassword requires ServiceAccountName.");
        }
    }
}
