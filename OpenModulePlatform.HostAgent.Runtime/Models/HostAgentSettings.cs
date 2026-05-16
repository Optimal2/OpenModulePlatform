namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class HostAgentSettings
{
    public string? HostKey { get; set; }

    public string? HostName { get; set; }

    public int RefreshSeconds { get; set; } = 30;

    public string CentralArtifactRoot { get; set; } = string.Empty;

    public string LocalArtifactCacheRoot { get; set; } = string.Empty;

    public bool MaterializeTemplates { get; set; } = true;

    public bool ProcessHostDeployments { get; set; } = true;

    public bool ProvisionAppInstanceArtifacts { get; set; } = true;

    public bool ProvisionExplicitRequirements { get; set; } = true;

    public bool DeployWebApps { get; set; }

    public string IisSiteName { get; set; } = string.Empty;

    public string WebAppsRoot { get; set; } = string.Empty;

    public string PortalPhysicalPath { get; set; } = string.Empty;

    public bool StopIisAppPoolForWebAppDeployment { get; set; } = true;

    public bool StartIisAppPoolAfterWebAppDeployment { get; set; } = true;

    public int IisAppPoolStopTimeoutSeconds { get; set; } = 30;

    public string[] WebAppDeploymentExcludedEntries { get; set; } =
    [
        "appsettings.json",
        "appsettings.*.json",
        "logs",
        "App_Data"
    ];

    public int MaxArtifactsPerCycle { get; set; } = 100;

    public bool EnableRpc { get; set; } = true;

    public string RpcPipeName { get; set; } = string.Empty;

    public int RpcRequestTimeoutSeconds { get; set; } = 60;

    public string ResolveHostKey()
    {
        if (!string.IsNullOrWhiteSpace(HostKey))
        {
            return HostKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(HostName))
        {
            return HostName.Trim();
        }

        return Environment.MachineName;
    }

    public string ResolveRpcPipeName()
    {
        return string.IsNullOrWhiteSpace(RpcPipeName)
            ? $"OpenModulePlatform.HostAgent.{ResolveHostKey()}"
            : RpcPipeName.Trim();
    }

    public void Validate()
    {
        if (RefreshSeconds < 1)
        {
            throw new InvalidOperationException("HostAgent:RefreshSeconds must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(CentralArtifactRoot))
        {
            throw new InvalidOperationException("HostAgent:CentralArtifactRoot must be configured.");
        }

        if (string.IsNullOrWhiteSpace(LocalArtifactCacheRoot))
        {
            throw new InvalidOperationException("HostAgent:LocalArtifactCacheRoot must be configured.");
        }

        if (MaxArtifactsPerCycle < 1)
        {
            throw new InvalidOperationException("HostAgent:MaxArtifactsPerCycle must be at least 1.");
        }

        if (DeployWebApps)
        {
            if (string.IsNullOrWhiteSpace(IisSiteName))
            {
                throw new InvalidOperationException("HostAgent:IisSiteName must be configured when HostAgent:DeployWebApps is enabled.");
            }

            if (string.IsNullOrWhiteSpace(WebAppsRoot) && string.IsNullOrWhiteSpace(PortalPhysicalPath))
            {
                throw new InvalidOperationException("HostAgent:WebAppsRoot or HostAgent:PortalPhysicalPath must be configured when HostAgent:DeployWebApps is enabled.");
            }

            if (IisAppPoolStopTimeoutSeconds < 1)
            {
                throw new InvalidOperationException("HostAgent:IisAppPoolStopTimeoutSeconds must be at least 1.");
            }
        }

        if (RpcRequestTimeoutSeconds < 1)
        {
            throw new InvalidOperationException("HostAgent:RpcRequestTimeoutSeconds must be at least 1.");
        }
    }
}
