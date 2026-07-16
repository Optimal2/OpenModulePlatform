namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class HostAgentSettings
{
    public const string SectionName = "HostAgent";

    public string ServiceName { get; set; } = "OMP.HostAgent";

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

    public int HostDeploymentLeaseSeconds { get; set; } = 300;

    public int HostDeploymentMaxAttempts { get; set; } = 3;

    public bool ProvisionAppInstanceArtifacts { get; set; } = true;

    public bool ProvisionExplicitRequirements { get; set; } = true;

    public bool ProcessHostAgentJobs { get; set; } = true;

    public int MaxHostAgentJobsPerCycle { get; set; } = 5;

    /// <summary>
    /// Interval in minutes between scheduled detect-only maintenance scans.
    /// A value of 0 disables the scheduled scan. Default is 1440 minutes (24 hours).
    /// </summary>
    public int MaintenanceScanIntervalMinutes { get; set; } = 1440;

    public bool DeployWebApps { get; set; }

    public string IisSiteName { get; set; } = string.Empty;

    public bool EnsureIisSite { get; set; }

    public string IisBindingProtocol { get; set; } = "http";

    public int IisBindingPort { get; set; } = 80;

    public string IisBindingHostHeader { get; set; } = string.Empty;

    public string IisBindingCertificateThumbprint { get; set; } = string.Empty;

    public string IisBindingCertificateSerialNumber { get; set; } = string.Empty;

    public string IisBindingCertificateStoreName { get; set; } = "My";

    public string WebAppsRoot { get; set; } = string.Empty;

    public string PortalPhysicalPath { get; set; } = string.Empty;

    public string IisAppPoolNamePrefix { get; set; } = "OMP_";

    public string IisAppPoolUserName { get; set; } = string.Empty;

    public string IisAppPoolPassword { get; set; } = string.Empty;

    public string IisAppPoolPasswordCredentialKey { get; set; } = string.Empty;

    public Dictionary<string, HostAgentIisAppPoolIdentitySettings> IisAppPoolOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string WebAppDataProtectionKeyPath { get; set; } = string.Empty;

    public bool WebAppUseForwardedHeaders { get; set; }

    public bool WebAppForwardedHeadersTrustAllProxies { get; set; }

    public string[] WebAppForwardedHeadersKnownProxies { get; set; } = [];

    public string[] WebAppForwardedHeadersKnownNetworks { get; set; } = [];

    public bool UseAppOfflineForWebAppDeployment { get; set; } = true;

    public int AppOfflineShutdownDelayMilliseconds { get; set; } = 1500;

    public bool StopIisAppPoolForWebAppDeployment { get; set; } = true;

    public bool StartIisAppPoolAfterWebAppDeployment { get; set; } = true;

    public int IisAppPoolStopTimeoutSeconds { get; set; } = 30;

    public HostAgentPortalHealthCheckSettings PortalHealthCheck { get; set; } = new();

    public HostResourceTelemetrySettings ResourceTelemetry { get; set; } = new();

    public string[] WebAppDeploymentExcludedEntries { get; set; } =
    [
        "appsettings.json",
        "appsettings.*.json",
        "logs",
        "App_Data"
    ];

    public bool DeployServiceApps { get; set; }

    public string ServicesRoot { get; set; } = string.Empty;

    public string ServiceAppUserName { get; set; } = string.Empty;

    public string ServiceAppPassword { get; set; } = string.Empty;

    public string ServiceAppPasswordCredentialKey { get; set; } = string.Empty;

    public Dictionary<string, HostAgentServiceAppIdentitySettings> ServiceAppIdentityOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

    public HostAgentCredentialStoreSettings CredentialStore { get; set; } = new();

    public int MaxArtifactsPerCycle { get; set; } = 100;

    public bool EnableRpc { get; set; } = true;

    public string RpcPipeName { get; set; } = string.Empty;

    public string[] RpcAllowedClientAccounts { get; set; } = [];

    public string[] RpcAllowedClientServiceNames { get; set; } = [];

    public int RpcRequestTimeoutSeconds { get; set; } = 60;

    public string DeploySetConsistencyMode { get; set; } = DeploySetConsistencyModes.Warn;

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

        if (MaxHostAgentJobsPerCycle < 1)
        {
            throw new InvalidOperationException("HostAgent:MaxHostAgentJobsPerCycle must be at least 1.");
        }

        if (MaintenanceScanIntervalMinutes < 0)
        {
            throw new InvalidOperationException("HostAgent:MaintenanceScanIntervalMinutes must be zero or greater.");
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

            PortalHealthCheck.Validate();
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
        CredentialStore.Validate();
        ResourceTelemetry.Validate();
    }
}

public sealed class HostResourceTelemetrySettings
{
    public bool Enabled { get; set; } = true;

    public int SampleIntervalSeconds { get; set; } = 60;

    public int SampleWindowSeconds { get; set; } = 1;

    public int MaxSamplesPerCycle { get; set; } = 200;

    public int BucketMinutes { get; set; } = 5;

    public int RetainHours { get; set; } = 168;

    public int PruneIntervalSeconds { get; set; } = 3600;

    public bool CollectIisAppPools { get; set; } = true;

    public bool CollectServiceProcesses { get; set; } = true;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (SampleIntervalSeconds < 1)
        {
            throw new InvalidOperationException("HostAgent:ResourceTelemetry:SampleIntervalSeconds must be at least 1.");
        }

        if (SampleWindowSeconds < 1)
        {
            throw new InvalidOperationException("HostAgent:ResourceTelemetry:SampleWindowSeconds must be at least 1.");
        }

        if (MaxSamplesPerCycle < 1)
        {
            throw new InvalidOperationException("HostAgent:ResourceTelemetry:MaxSamplesPerCycle must be at least 1.");
        }

        if (BucketMinutes < 1)
        {
            throw new InvalidOperationException("HostAgent:ResourceTelemetry:BucketMinutes must be at least 1.");
        }

        if (RetainHours < 1)
        {
            throw new InvalidOperationException("HostAgent:ResourceTelemetry:RetainHours must be at least 1.");
        }

        if (PruneIntervalSeconds < 1)
        {
            throw new InvalidOperationException("HostAgent:ResourceTelemetry:PruneIntervalSeconds must be at least 1.");
        }
    }
}

public sealed class HostAgentIisAppPoolIdentitySettings
{
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string PasswordCredentialKey { get; set; } = string.Empty;
}

public sealed class HostAgentPortalHealthCheckSettings
{
    public bool Enabled { get; set; } = true;

    public string HealthKey { get; set; } = "portal";

    public string DisplayName { get; set; } = "OMP Portal";

    public string Path { get; set; } = "/health/ready";

    public string Scheme { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public int? Port { get; set; }

    public string HostHeader { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

    public int FailureThreshold { get; set; } = 3;

    public bool AutoRecycleAppPool { get; set; }

    public int AutoRecycleCooldownMinutes { get; set; } = 15;

    public bool AllowInvalidTlsCertificate { get; set; }

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(HealthKey))
        {
            throw new InvalidOperationException("HostAgent:PortalHealthCheck:HealthKey must be configured when portal health checks are enabled.");
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new InvalidOperationException("HostAgent:PortalHealthCheck:Path must be configured when portal health checks are enabled.");
        }

        if (TimeoutSeconds < 1)
        {
            throw new InvalidOperationException("HostAgent:PortalHealthCheck:TimeoutSeconds must be at least 1.");
        }

        if (FailureThreshold < 1)
        {
            throw new InvalidOperationException("HostAgent:PortalHealthCheck:FailureThreshold must be at least 1.");
        }

        if (AutoRecycleCooldownMinutes < 1)
        {
            throw new InvalidOperationException("HostAgent:PortalHealthCheck:AutoRecycleCooldownMinutes must be at least 1.");
        }
    }
}

public sealed class HostAgentServiceAppIdentitySettings
{
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string PasswordCredentialKey { get; set; } = string.Empty;
}

public static class HostAgentCredentialAutomationModes
{
    public const string Disabled = "Disabled";

    public const string PortalAdminApproved = "PortalAdminApproved";

    public const string Full = "Full";

    public static bool IsKnown(string value)
        => string.Equals(value, Disabled, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, PortalAdminApproved, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Full, StringComparison.OrdinalIgnoreCase);
}

public static class HostAgentCredentialProtectionScopes
{
    public const string CurrentUser = "CurrentUser";

    public const string LocalMachine = "LocalMachine";

    public static bool IsKnown(string value)
        => string.Equals(value, CurrentUser, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, LocalMachine, StringComparison.OrdinalIgnoreCase);
}

public sealed class HostAgentCredentialStoreSettings
{
    public string AutomationMode { get; set; } = HostAgentCredentialAutomationModes.Disabled;

    public string FilePath { get; set; } = string.Empty;

    public string ProtectionScope { get; set; } = HostAgentCredentialProtectionScopes.LocalMachine;

    public string EntropyPurpose { get; set; } = "OpenModulePlatform.HostAgent.CredentialStore.v1";

    public bool IsEnabled()
        => !string.Equals(
            AutomationMode?.Trim(),
            HostAgentCredentialAutomationModes.Disabled,
            StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        var automationMode = string.IsNullOrWhiteSpace(AutomationMode)
            ? HostAgentCredentialAutomationModes.Disabled
            : AutomationMode.Trim();
        if (!HostAgentCredentialAutomationModes.IsKnown(automationMode))
        {
            throw new InvalidOperationException(
                "HostAgent:CredentialStore:AutomationMode must be Disabled, PortalAdminApproved, or Full.");
        }

        var protectionScope = string.IsNullOrWhiteSpace(ProtectionScope)
            ? HostAgentCredentialProtectionScopes.LocalMachine
            : ProtectionScope.Trim();
        if (!HostAgentCredentialProtectionScopes.IsKnown(protectionScope))
        {
            throw new InvalidOperationException(
                "HostAgent:CredentialStore:ProtectionScope must be CurrentUser or LocalMachine.");
        }

        if (IsEnabled() && string.IsNullOrWhiteSpace(EntropyPurpose))
        {
            throw new InvalidOperationException(
                "HostAgent:CredentialStore:EntropyPurpose must be configured when credential storage is enabled.");
        }
    }

    public string ResolveFilePath()
        => string.IsNullOrWhiteSpace(FilePath)
            ? Path.Join(AppContext.BaseDirectory, "hostagent.credentials.json")
            : FilePath.Trim();
}

public sealed class HostAgentStoredCredentialEntry
{
    public string UserName { get; set; } = string.Empty;

    public string EncryptedPassword { get; set; } = string.Empty;

    public string ProtectionProvider { get; set; } = "WindowsDpapi";

    public string ProtectionScope { get; set; } = HostAgentCredentialProtectionScopes.LocalMachine;

    public string Description { get; set; } = string.Empty;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HostAgentCredentialStoreDocument
{
    public int FormatVersion { get; set; } = 1;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, HostAgentStoredCredentialEntry> Credentials { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record HostAgentPlainTextCredential(
    string Key,
    string UserName,
    string Password);

public sealed class HostAgentArtifactZipImportSettings
{
    public bool IsEnabled { get; set; }

    public string ImportPath { get; set; } = string.Empty;

    public string ProcessedPath { get; set; } = string.Empty;

    public string FailedPath { get; set; } = string.Empty;

    public int MaxFilesPerCycle { get; set; } = 10;

    public bool CopyConfigurationFilesFromPreviousVersion { get; set; } = true;

    public long MaxArtifactPackageTotalUncompressedBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    public long MaxArtifactPackageEntryUncompressedBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public long MaxUniversalPackageTotalUncompressedBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    public long MaxUniversalPackageEntryUncompressedBytes { get; set; } = 2L * 1024 * 1024 * 1024;

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

        const long OneMegabyte = 1024L * 1024;
        if (MaxArtifactPackageTotalUncompressedBytes < OneMegabyte)
        {
            throw new InvalidOperationException("HostAgent:ArtifactZipImport:MaxArtifactPackageTotalUncompressedBytes must be at least 1 MB.");
        }

        if (MaxArtifactPackageEntryUncompressedBytes < OneMegabyte)
        {
            throw new InvalidOperationException("HostAgent:ArtifactZipImport:MaxArtifactPackageEntryUncompressedBytes must be at least 1 MB.");
        }

        if (MaxUniversalPackageTotalUncompressedBytes < OneMegabyte)
        {
            throw new InvalidOperationException("HostAgent:ArtifactZipImport:MaxUniversalPackageTotalUncompressedBytes must be at least 1 MB.");
        }

        if (MaxUniversalPackageEntryUncompressedBytes < OneMegabyte)
        {
            throw new InvalidOperationException("HostAgent:ArtifactZipImport:MaxUniversalPackageEntryUncompressedBytes must be at least 1 MB.");
        }
    }

    public string ResolveProcessedPath()
        => string.IsNullOrWhiteSpace(ProcessedPath)
            ? Path.Join(ImportPath, "processed")
            : ProcessedPath.Trim();

    public string ResolveFailedPath()
        => string.IsNullOrWhiteSpace(FailedPath)
            ? Path.Join(ImportPath, "failed")
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
