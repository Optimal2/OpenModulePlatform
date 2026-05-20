namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class HostAgentSettings
{
    public string ServiceName { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string RuntimeMode { get; set; } = HostAgentRuntimeMode.Normal;

    public string TakeoverFromServiceName { get; set; } = string.Empty;

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

    public bool EnsureIisSite { get; set; }

    public string IisBindingProtocol { get; set; } = "http";

    public int IisBindingPort { get; set; } = 80;

    public string IisBindingHostHeader { get; set; } = string.Empty;

    public string WebAppsRoot { get; set; } = string.Empty;

    public string PortalPhysicalPath { get; set; } = string.Empty;

    public string IisAppPoolNamePrefix { get; set; } = "OMP_";

    public string IisAppPoolUserName { get; set; } = string.Empty;

    public string IisAppPoolPassword { get; set; } = string.Empty;

    public string WebAppDataProtectionKeyPath { get; set; } = string.Empty;

    public bool UseAppOfflineForWebAppDeployment { get; set; } = true;

    public int AppOfflineShutdownDelayMilliseconds { get; set; } = 1500;

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

    public bool DeployServiceApps { get; set; }

    public string ServicesRoot { get; set; } = string.Empty;

    public bool StopServiceForServiceAppDeployment { get; set; } = true;

    public bool StartServiceAfterServiceAppDeployment { get; set; } = true;

    public int ServiceAppStopTimeoutSeconds { get; set; } = 30;

    public int ServiceAppStartTimeoutSeconds { get; set; } = 30;

    public string[] ServiceAppDeploymentExcludedEntries { get; set; } =
    [
        "appsettings.json",
        "appsettings.*.json",
        "logs",
        "App_Data"
    ];

    public HostAgentFileMirrorSettings[] FileMirrors { get; set; } = [];

    public HostAgentArtifactZipImportSettings ArtifactZipImport { get; set; } = new();

    public HostAgentUpgradeSettings SelfUpgrade { get; set; } = new();

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

            if (EnsureIisSite)
            {
                if (string.IsNullOrWhiteSpace(PortalPhysicalPath))
                {
                    throw new InvalidOperationException("HostAgent:PortalPhysicalPath must be configured when HostAgent:EnsureIisSite is enabled.");
                }

                if (string.IsNullOrWhiteSpace(IisBindingProtocol))
                {
                    throw new InvalidOperationException("HostAgent:IisBindingProtocol must be configured when HostAgent:EnsureIisSite is enabled.");
                }

                if (IisBindingPort is < 1 or > 65535)
                {
                    throw new InvalidOperationException("HostAgent:IisBindingPort must be between 1 and 65535 when HostAgent:EnsureIisSite is enabled.");
                }
            }

            if (IisAppPoolStopTimeoutSeconds < 1)
            {
                throw new InvalidOperationException("HostAgent:IisAppPoolStopTimeoutSeconds must be at least 1.");
            }

            if (AppOfflineShutdownDelayMilliseconds < 0)
            {
                throw new InvalidOperationException("HostAgent:AppOfflineShutdownDelayMilliseconds must be zero or greater.");
            }
        }

        if (DeployServiceApps)
        {
            if (string.IsNullOrWhiteSpace(ServicesRoot))
            {
                throw new InvalidOperationException("HostAgent:ServicesRoot must be configured when HostAgent:DeployServiceApps is enabled.");
            }

            if (ServiceAppStopTimeoutSeconds < 1)
            {
                throw new InvalidOperationException("HostAgent:ServiceAppStopTimeoutSeconds must be at least 1.");
            }

            if (ServiceAppStartTimeoutSeconds < 1)
            {
                throw new InvalidOperationException("HostAgent:ServiceAppStartTimeoutSeconds must be at least 1.");
            }
        }

        if (RpcRequestTimeoutSeconds < 1)
        {
            throw new InvalidOperationException("HostAgent:RpcRequestTimeoutSeconds must be at least 1.");
        }

        foreach (var mirror in FileMirrors.Where(static mirror => mirror.IsEnabled))
        {
            mirror.Validate();
        }

        ArtifactZipImport.Validate();
        SelfUpgrade.Validate();
    }
}

public sealed class HostAgentArtifactZipImportSettings
{
    public bool IsEnabled { get; set; }

    public string ImportPath { get; set; } = string.Empty;

    public string ProcessedPath { get; set; } = string.Empty;

    public string FailedPath { get; set; } = string.Empty;

    public int MaxFilesPerCycle { get; set; } = 10;

    public bool CopyConfigurationFilesFromPreviousVersion { get; set; } = true;

    public void Validate()
    {
        if (!IsEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ImportPath))
        {
            throw new InvalidOperationException("HostAgent:ArtifactZipImport:ImportPath must be configured when artifact zip import is enabled.");
        }

        if (MaxFilesPerCycle < 1)
        {
            throw new InvalidOperationException("HostAgent:ArtifactZipImport:MaxFilesPerCycle must be at least 1.");
        }
    }

    public string ResolveProcessedPath()
        => string.IsNullOrWhiteSpace(ProcessedPath)
            ? Path.Combine(ImportPath, "processed")
            : ProcessedPath.Trim();

    public string ResolveFailedPath()
        => string.IsNullOrWhiteSpace(FailedPath)
            ? Path.Combine(ImportPath, "failed")
            : FailedPath.Trim();
}

public sealed class HostAgentFileMirrorSettings
{
    public bool IsEnabled { get; set; } = true;

    public string SourcePath { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public bool DeleteStaleTargetEntries { get; set; } = true;

    public string[] ExcludedEntries { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            throw new InvalidOperationException("HostAgent:FileMirrors:SourcePath must be configured for enabled file mirrors.");
        }

        if (string.IsNullOrWhiteSpace(TargetPath))
        {
            throw new InvalidOperationException("HostAgent:FileMirrors:TargetPath must be configured for enabled file mirrors.");
        }

        var source = Path.GetFullPath(SourcePath.Trim());
        var target = Path.GetFullPath(TargetPath.Trim());
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HostAgent:FileMirrors source and target paths must be different.");
        }

        var targetRoot = Path.GetPathRoot(target);
        if (string.IsNullOrWhiteSpace(targetRoot)
            || string.Equals(Path.TrimEndingDirectorySeparator(targetRoot), Path.TrimEndingDirectorySeparator(target), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HostAgent:FileMirrors target path must not be a drive or share root.");
        }
    }
}
