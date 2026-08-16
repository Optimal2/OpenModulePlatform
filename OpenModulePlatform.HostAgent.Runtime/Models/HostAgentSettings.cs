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

    /// <summary>
    /// Refuse to provision an artifact whose catalog row carries no Sha256, instead of
    /// accepting the local or downloaded content unverified.
    /// </summary>
    /// <remarks>
    /// R3-D6. Both behaviours are defensible and the code used to claim one while doing
    /// the other: it logged the missing hash as an error and then accepted the content
    /// anyway, under a comment saying it surfaced the problem "instead of silently
    /// accepting". Accepting is not indefensible -- artifact identity (app, package type,
    /// target, version) still has to match, and that is what R8-P2-10 concluded -- but it
    /// means content integrity is unchecked, which an installation may reasonably refuse.
    ///
    /// The default keeps today's behaviour, so no running installation changes when it
    /// upgrades. Set it to true where every artifact row is known to carry a SHA.
    ///
    /// R12-F12: "known to carry a SHA" used to be unknowable. Nothing counted the artifacts
    /// that make this unsafe to turn on -- the only signal was a per-artifact line written
    /// while that artifact was being provisioned, so an artifact this host never provisions
    /// was invisible and the gap could not be driven to zero. Two recurring audits now state
    /// it: ArtifactZipImportService reports the catalog-wide count and how many of those
    /// artifacts are still referenced by anything enabled (the number that decides whether
    /// the flag can be flipped at all), and ArtifactProvisioner reports the artifacts this
    /// particular host provisions without a hash (the number that decides whether it can be
    /// flipped HERE). Read both before setting this to true; the default is unchanged
    /// because only the operator of an installation can know that its own count is zero.
    /// </remarks>
    public bool RequireArtifactHash { get; set; }

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

    /// <summary>How long the rolled-up daily host resource history is kept.</summary>
    /// <remarks>
    /// Hourly rows survive a week; beyond that they are folded into one row per host, day
    /// and measurement. A year and a bit means the autumn's growth is still comparable to
    /// the following autumn's, which is the question this history exists to answer.
    /// </remarks>
    public int RetainDays { get; set; } = 400;

    public int PruneIntervalSeconds { get; set; } = 3600;

    public bool CollectIisAppPools { get; set; } = true;

    public bool CollectServiceProcesses { get; set; } = true;

    /// <summary>
    /// Samples the worker processes WorkerManager starts, keyed by worker instance (R8-P5-20).
    /// </summary>
    /// <remarks>
    /// These are neither IIS app pools nor Windows services, so without this the host summary
    /// silently omits the entire worker fleet -- eight processes and about a third of OMP's memory
    /// on this installation. The switch matches the other two sources so an operator can turn one
    /// collector off without losing the rest.
    /// </remarks>
    public bool CollectWorkerProcesses { get; set; } = true;

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

        if (RetainDays < 1)
        {
            throw new InvalidOperationException("HostAgent:ResourceTelemetry:RetainDays must be at least 1.");
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

    // Archived processed/failed import files (universal packages are multi-GB) were
    // kept forever, so the store volume slowly filled with dead zips (R5-D15). Prune
    // archives older than this many days; 0 disables pruning (keep forever).
    public int ProcessedRetentionDays { get; set; } = 30;

    /// <summary>
    /// Upper bound on the size of EACH import archive root (processed and failed), applied
    /// alongside <see cref="ProcessedRetentionDays" />: oldest archives are deleted first
    /// until the root is under the cap. 0 disables the size cap.
    /// </summary>
    /// <remarks>
    /// R12-F13. Age alone does not bound the archive, because volume is driven by cadence,
    /// not by age: a universal package is ~124 MB and a refresh can be run several times a
    /// day, so 30 days of retention is 30 days of however many refreshes were run. Measured
    /// on LINUS-LAPTOP 2026-08-16, 16 days after the store was created: 114 archived import
    /// files totalling 9,63 GB (processed 66 files / 7,10 GB, failed 48 files / 2,53 GB),
    /// projecting to roughly 18 GB once the 30-day window is actually full -- and the volume
    /// that fills is the one holding the artifact store, so the failure mode is imports
    /// failing with disk-full errors.
    ///
    /// The cap is per archive root rather than shared between them, because ProcessedPath
    /// and FailedPath are separately configurable and may sit on different volumes; a shared
    /// budget would prune one root because the other grew, which is the wrong root to touch
    /// and the wrong volume to protect.
    ///
    /// 4 GB per root holds roughly 30 universal packages -- far more rollback history than
    /// has ever been needed -- and bounds both archives together below what the processed
    /// archive alone reached in two weeks here. The newest archive in a root is never
    /// pruned, by age or by size, so a cap smaller than a single package cannot empty the
    /// archive and destroy the one file an operator looks for after a bad refresh.
    /// </remarks>
    public long ProcessedRetentionMaxBytes { get; set; } = 4L * 1024 * 1024 * 1024;

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
        if (ProcessedRetentionMaxBytes < 0 || (ProcessedRetentionMaxBytes > 0 && ProcessedRetentionMaxBytes < OneMegabyte))
        {
            throw new InvalidOperationException("HostAgent:ArtifactZipImport:ProcessedRetentionMaxBytes must be 0 (no size cap) or at least 1 MB.");
        }

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
