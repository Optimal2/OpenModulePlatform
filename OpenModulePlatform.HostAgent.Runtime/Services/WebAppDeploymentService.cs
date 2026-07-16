using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class WebAppDeploymentService
{
    private const string IisSslBindingAppId = "{6E9C7F30-6D4E-4D8A-9D1E-9F086C35B508}";
    private const string SystemSecurityPermissionsAssemblyName = "System.Security.Permissions";
    private const int MaxIisAppPoolNameLength = 80;
    private static readonly TimeSpan AppPoolStatePollDelay = TimeSpan.FromMilliseconds(250);

    private static readonly object IisAssemblyResolverLock = new();
    private static bool iisAssemblyResolverRegistered;

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly IOmpHostArtifactRepository _repository;
    private readonly HostAgentCredentialStoreService _credentialStore;
    private readonly ILogger<WebAppDeploymentService> _logger;

    public WebAppDeploymentService(
        IOptionsMonitor<HostAgentSettings> settings,
        IOmpHostArtifactRepository repository,
        HostAgentCredentialStoreService credentialStore,
        ILogger<WebAppDeploymentService> logger)
    {
        _settings = settings;
        _repository = repository;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public async Task DeployDesiredWebAppsAsync(string hostKey, CancellationToken cancellationToken)
        => await DeployDesiredWebAppsAsync(hostKey, null, cancellationToken);

    public async Task DeployDesiredWebAppsAsync(
        string hostKey,
        IReadOnlyDictionary<string, string>? deploySetWarningsByModuleInstanceKey,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.DeployWebApps)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Web app deployment requires Windows and IIS appcmd.exe.");
        }

        await RecoverInterruptedDeploymentsAsync(hostKey, cancellationToken);

        var deployments = await _repository.GetDesiredWebAppDeploymentsAsync(
            hostKey,
            settings.MaxArtifactsPerCycle,
            cancellationToken);

        _logger.LogInformation(
            "Resolved desired web app deployments. HostKey={HostKey}, Count={Count}",
            hostKey,
            deployments.Count);

        // A UNC data-protection key path is only required when auth cookies must be shared
        // across multiple enabled hosts; on single-host installs a local path is correct.
        var isMultiHost = deployments.Count > 0
            && await _repository.GetEnabledHostCountAsync(cancellationToken) > 1;

        foreach (var deployment in deployments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeployAsync(settings, deployment, deploySetWarningsByModuleInstanceKey, isMultiHost, cancellationToken);
        }
    }

    private async Task DeployAsync(
        HostAgentSettings settings,
        WebAppDeploymentDescriptor deployment,
        IReadOnlyDictionary<string, string>? deploySetWarningsByModuleInstanceKey,
        bool isMultiHost,
        CancellationToken cancellationToken)
    {
        string? targetPath = null;
        string? runtimeName = null;
        var appPoolStopped = false;
        var stopMarkerWritten = false;
        string? diagnosticWarning = null;
        OmpAuthValidationResult? ompAuthValidation = null;
        string? deploySetWarning = null;
        if (deploySetWarningsByModuleInstanceKey is not null
            && deploySetWarningsByModuleInstanceKey.TryGetValue(deployment.ModuleInstanceKey, out var foundDeploySetWarning))
        {
            deploySetWarning = foundDeploySetWarning;
        }

        AppDeploymentResult WithDeploySetWarning(AppDeploymentResult result)
        {
            if (string.IsNullOrWhiteSpace(deploySetWarning))
            {
                return result;
            }

            var existing = result.DiagnosticWarningMessage;
            var combined = string.IsNullOrWhiteSpace(existing)
                ? deploySetWarning
                : existing + Environment.NewLine + deploySetWarning;
            return result.WithDiagnosticWarning(combined);
        }

        AppDeploymentResult WithExtractedOmpAuth(AppDeploymentResult result)
        {
            return WithDeploySetWarning(result).WithEffectiveOmpAuth(
                ompAuthValidation?.EffectiveCookieName,
                ompAuthValidation?.EffectiveApplicationName,
                ompAuthValidation?.EffectiveDataProtectionKeyPath);
        }

        try
        {
            targetPath = ResolveTargetPath(settings, deployment);
            var iisAppName = ResolveIisAppName(settings, deployment);
            var appPoolName = settings.EnsureIisSite
                ? ResolveIisAppPoolName(settings, deployment)
                : null;
            runtimeName = appPoolName ?? (UseAppCmdAppPoolControl(settings)
                ? GetIisAppPoolName(iisAppName)
                : iisAppName);

            var configuredConnectionString = _repository.GetConfiguredConnectionString();
            var configurationFiles = await _repository.GetArtifactConfigurationFilesAsync(
                deployment.ArtifactId,
                deployment.HostKey,
                cancellationToken);
            configurationFiles = ArtifactConfigurationFileWriter.WithBuiltInWebAppConfiguration(
                configurationFiles,
                deployment,
                configuredConnectionString,
                settings);
            var configurationVariables = ArtifactConfigurationFileWriter.CreateVariables(
                deployment,
                configuredConnectionString,
                settings);

            var hostDataProtectionKeyPath = ResolveWebAppDataProtectionKeyPath(settings);
            ompAuthValidation = ValidateOmpAuthConfiguration(configurationFiles, configurationVariables, hostDataProtectionKeyPath, isMultiHost);
            foreach (var warning in ompAuthValidation.Warnings)
            {
                _logger.LogWarning(
                    "OmpAuth configuration warning for web app deployment. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Warning={Warning}",
                    deployment.AppInstanceId,
                    deployment.ArtifactId,
                    warning.Message);
            }

            diagnosticWarning = ompAuthValidation.Warnings.Count > 0
                ? string.Join(Environment.NewLine, ompAuthValidation.Warnings.Select(warning => warning.ToStoredString()))
                : null;

            var requiredRootSections = await _repository.GetRequiredConfigRootSectionsAsync(
                deployment.ArtifactId,
                cancellationToken);
            var missingRequiredSectionsWarning = RequiredConfigSectionsValidator.Validate(
                configurationFiles,
                requiredRootSections);
            if (!string.IsNullOrWhiteSpace(missingRequiredSectionsWarning))
            {
                _logger.LogWarning(
                    "Required config section warning for web app deployment. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Warning={Warning}",
                    deployment.AppInstanceId,
                    deployment.ArtifactId,
                    missingRequiredSectionsWarning);

                diagnosticWarning = string.IsNullOrWhiteSpace(diagnosticWarning)
                    ? missingRequiredSectionsWarning
                    : diagnosticWarning + Environment.NewLine + missingRequiredSectionsWarning;
            }

            if (IsAlreadyApplied(deployment, targetPath, runtimeName)
                && ArtifactConfigurationFileWriter.AreApplied(targetPath, configurationFiles, configurationVariables))
            {
                await _repository.PublishAppDeploymentResultAsync(
                    deployment,
                    WithExtractedOmpAuth(
                        AppDeploymentResult.Succeeded(targetPath, runtimeName, applied: false)
                            .WithDiagnosticWarning(diagnosticWarning)),
                    cancellationToken);
                return;
            }

            var lockStatus = DeploymentLockFile.ReadStatus(targetPath, DateTimeOffset.UtcNow);
            if (lockStatus.IsLocked)
            {
                var message = lockStatus.ToDeploymentSkippedMessage("Web app");
                _logger.LogInformation(
                    "Web app deployment skipped because a deployment lock is active. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Version={Version}, LockPath={LockPath}",
                    deployment.AppInstanceId,
                    deployment.ArtifactId,
                    deployment.Version,
                    lockStatus.Path);

                await _repository.PublishAppDeploymentResultAsync(
                    deployment,
                    WithExtractedOmpAuth(AppDeploymentResult.Warning(targetPath, runtimeName, message)),
                    cancellationToken);
                return;
            }

            var deploymentLock = await HostAgentDeploymentLockLease.TryAcquireAsync(
                targetPath,
                deployment.AppInstanceKey,
                $"HostAgent {deployment.HostKey}",
                "HostAgent web app deployment is running.",
                _logger,
                cancellationToken);
            if (deploymentLock.Lease is null)
            {
                var activeLock = deploymentLock.ExistingLockStatus
                    ?? DeploymentLockStatus.Locked(
                        DeploymentLockFile.GetPath(targetPath),
                        null,
                        "Deployment lock became active before HostAgent could begin deployment.");
                var message = activeLock.ToDeploymentSkippedMessage("Web app");
                _logger.LogInformation(
                    "Web app deployment skipped because a deployment lock became active before HostAgent acquired its deployment lease. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Version={Version}, LockPath={LockPath}",
                    deployment.AppInstanceId,
                    deployment.ArtifactId,
                    deployment.Version,
                    activeLock.Path);

                await _repository.PublishAppDeploymentResultAsync(
                    deployment,
                    WithExtractedOmpAuth(AppDeploymentResult.Warning(targetPath, runtimeName, message)),
                    cancellationToken);
                return;
            }

            await using var deploymentLockLease = deploymentLock.Lease!;

            if (settings.EnsureIisSite)
            {
                await EnsureIisApplicationAsync(settings, deployment, targetPath, iisAppName, runtimeName, cancellationToken);
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                WithExtractedOmpAuth(
                    AppDeploymentResult.Running(targetPath, runtimeName)
                        .WithDiagnosticWarning(diagnosticWarning)),
                cancellationToken);

            if (UseAppCmdAppPoolControl(settings)
                && settings.StopIisAppPoolForWebAppDeployment
                && settings.StartIisAppPoolAfterWebAppDeployment
                && !string.IsNullOrWhiteSpace(runtimeName))
            {
                var initialAppPoolState = GetAppPoolState(runtimeName);
                if (string.Equals(initialAppPoolState, "Started", StringComparison.OrdinalIgnoreCase))
                {
                    DeploymentRuntimeStopMarker.Write(
                        targetPath,
                        "web-app",
                        runtimeName,
                        deployment.AppInstanceId,
                        deployment.AppInstanceKey,
                        deployment.HostKey);
                    stopMarkerWritten = true;
                }

                appPoolStopped = await StopAppPoolIfRunningAsync(
                    runtimeName,
                    settings.IisAppPoolStopTimeoutSeconds,
                    initialAppPoolState,
                    cancellationToken);
                if (!appPoolStopped && stopMarkerWritten)
                {
                    DeploymentRuntimeStopMarker.Delete(targetPath);
                    stopMarkerWritten = false;
                }
            }

            await MirrorWebAppAsync(settings, deployment, targetPath, configurationFiles, configurationVariables, cancellationToken);

            if (UseAppCmdAppPoolControl(settings)
                && settings.StartIisAppPoolAfterWebAppDeployment
                && !string.IsNullOrWhiteSpace(runtimeName))
            {
                StartAppPoolIfStopped(runtimeName);
                appPoolStopped = false;
                if (stopMarkerWritten)
                {
                    DeploymentRuntimeStopMarker.Delete(targetPath);
                    stopMarkerWritten = false;
                }
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                WithExtractedOmpAuth(
                    AppDeploymentResult.Succeeded(targetPath, runtimeName, applied: true)
                        .WithDiagnosticWarning(diagnosticWarning)),
                cancellationToken);

            _logger.LogInformation(
                "Web app deployed. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Version={Version}, TargetPath={TargetPath}, RuntimeName={RuntimeName}",
                deployment.AppInstanceId,
                deployment.ArtifactId,
                deployment.Version,
                targetPath,
                runtimeName);
        }
        catch (Exception ex) when (IsExpectedDeploymentFailure(ex))
        {
            _logger.LogError(
                ex,
                "Web app deployment failed. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Version={Version}",
                deployment.AppInstanceId,
                deployment.ArtifactId,
                deployment.Version);

            if (appPoolStopped
                && UseAppCmdAppPoolControl(settings)
                && settings.StartIisAppPoolAfterWebAppDeployment
                && !string.IsNullOrWhiteSpace(runtimeName)
                && TryStartAppPool(runtimeName, _logger)
                && stopMarkerWritten
                && !string.IsNullOrWhiteSpace(targetPath))
            {
                DeploymentRuntimeStopMarker.Delete(targetPath);
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                WithExtractedOmpAuth(
                    AppDeploymentResult.Failed(targetPath, runtimeName, ex.Message)
                        .WithDiagnosticWarning(diagnosticWarning)),
                cancellationToken);
        }
    }

    private async Task MirrorWebAppAsync(
        HostAgentSettings settings,
        WebAppDeploymentDescriptor deployment,
        string targetPath,
        IReadOnlyList<ArtifactConfigurationFileDescriptor> configurationFiles,
        IReadOnlyDictionary<string, string> configurationVariables,
        CancellationToken cancellationToken)
    {
        string? appOfflinePath = null;
        try
        {
            if (settings.UseAppOfflineForWebAppDeployment)
            {
                appOfflinePath = CreateAppOfflineFile(targetPath, deployment);
                if (settings.AppOfflineShutdownDelayMilliseconds > 0)
                {
                    await Task.Delay(settings.AppOfflineShutdownDelayMilliseconds, cancellationToken);
                }
            }

            var excludedEntries = settings.WebAppDeploymentExcludedEntries;
            if (!string.IsNullOrWhiteSpace(appOfflinePath))
            {
                excludedEntries = [.. excludedEntries, "app_offline.htm"];
            }

            ArtifactDirectoryMirror.MirrorDirectory(
                deployment.SourceLocalPath,
                targetPath,
                excludedEntries,
                cancellationToken);

            await ArtifactConfigurationFileWriter.ApplyAsync(targetPath, configurationFiles, configurationVariables, cancellationToken);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(appOfflinePath))
            {
                ArtifactDirectoryMirror.DeleteFileIfExistsWithRetry(appOfflinePath, cancellationToken);
            }
        }
    }

    private static bool IsAlreadyApplied(
        WebAppDeploymentDescriptor deployment,
        string targetPath,
        string? appPoolName)
    {
        return deployment.DeploymentState == HostDeploymentStatuses.Succeeded
            && deployment.DeployedArtifactId == deployment.ArtifactId
            && string.Equals(deployment.DeployedSourceLocalPath, deployment.SourceLocalPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(deployment.DeployedTargetPath, targetPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(deployment.DeployedRuntimeName ?? string.Empty, appPoolName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(targetPath);
    }

    private sealed class OmpAuthValidationResult
    {
        public IReadOnlyList<OmpAuthConfigurationWarning> Warnings { get; init; } = Array.Empty<OmpAuthConfigurationWarning>();
        public string? EffectiveCookieName { get; init; }
        public string? EffectiveApplicationName { get; init; }
        public string? EffectiveDataProtectionKeyPath { get; init; }
    }

    private static OmpAuthValidationResult ValidateOmpAuthConfiguration(
        IReadOnlyList<ArtifactConfigurationFileDescriptor> configurationFiles,
        IReadOnlyDictionary<string, string> configurationVariables,
        string hostDataProtectionKeyPath,
        bool isMultiHost)
    {
        var warnings = new List<OmpAuthConfigurationWarning>();
        string? effectiveCookieName = null;
        string? effectiveApplicationName = null;
        string? effectiveDataProtectionKeyPath = null;

        var appSettingsFile = configurationFiles.FirstOrDefault(file =>
            string.Equals(file.RelativePath, "appsettings.json", StringComparison.OrdinalIgnoreCase));
        if (appSettingsFile is null)
        {
            return new OmpAuthValidationResult { Warnings = warnings };
        }

        // Stored configuration may contain {{Omp.*}} tokens that are only rendered at apply
        // time. Render before comparing so token-based configs are validated by their
        // effective values instead of false-positive mismatches on the raw token text.
        var content = ArtifactConfigurationFileWriter.Render(appSettingsFile.FileContent, configurationVariables);

        JsonNode? ompAuthNode;
        try
        {
            ompAuthNode = JsonNode.Parse(content)?["OmpAuth"];
        }
        catch (JsonException)
        {
            return new OmpAuthValidationResult { Warnings = warnings };
        }

        if (ompAuthNode is null)
        {
            return new OmpAuthValidationResult { Warnings = warnings };
        }

        const string DefaultCookieName = ".OpenModulePlatform.Auth";
        const string DefaultApplicationName = "OpenModulePlatform";

        if (ompAuthNode["CookieName"] is JsonNode cookieNameNode
            && cookieNameNode.GetValueKind() == JsonValueKind.String)
        {
            var cookieName = cookieNameNode.GetValue<string>();
            effectiveCookieName = cookieName;
            if (!string.Equals(cookieName, DefaultCookieName, StringComparison.Ordinal))
            {
                warnings.Add(new OmpAuthConfigurationWarning(
                    OmpAuthConfigurationWarning.CookieNameUnexpectedValueCode,
                    [cookieName, DefaultCookieName],
                    $"OmpAuth:CookieName is '{cookieName}' but the expected OMP default is '{DefaultCookieName}'. " +
                    "Shared auth cookies may break if this value differs across OMP web apps."));
            }
        }

        if (ompAuthNode["ApplicationName"] is JsonNode applicationNameNode
            && applicationNameNode.GetValueKind() == JsonValueKind.String)
        {
            var applicationName = applicationNameNode.GetValue<string>();
            effectiveApplicationName = applicationName;
            if (!string.Equals(applicationName, DefaultApplicationName, StringComparison.Ordinal))
            {
                warnings.Add(new OmpAuthConfigurationWarning(
                    OmpAuthConfigurationWarning.ApplicationNameUnexpectedValueCode,
                    [applicationName, DefaultApplicationName],
                    $"OmpAuth:ApplicationName is '{applicationName}' but the expected OMP default is '{DefaultApplicationName}'. " +
                    "Shared auth cookies may break if this value differs across OMP web apps."));
            }
        }

        if (ompAuthNode["DataProtectionKeyPath"] is JsonNode dataProtectionKeyPathNode
            && dataProtectionKeyPathNode.GetValueKind() == JsonValueKind.String)
        {
            var dataProtectionKeyPath = dataProtectionKeyPathNode.GetValue<string>();
            effectiveDataProtectionKeyPath = dataProtectionKeyPath;
            if (!string.Equals(dataProtectionKeyPath, hostDataProtectionKeyPath, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new OmpAuthConfigurationWarning(
                    OmpAuthConfigurationWarning.DataProtectionKeyPathMismatchCode,
                    [dataProtectionKeyPath, hostDataProtectionKeyPath],
                    $"OmpAuth:DataProtectionKeyPath is '{dataProtectionKeyPath}' but the HostAgent expects '{hostDataProtectionKeyPath}'. " +
                    "Data protection keys must be shared across OMP web apps for auth-cookie compatibility."));
            }

            // A UNC key path is only needed to share auth cookies across multiple enabled hosts.
            // On a single-host installation a local key path is correct, so only advise then.
            if (isMultiHost
                && !string.IsNullOrWhiteSpace(dataProtectionKeyPath)
                && !dataProtectionKeyPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                warnings.Add(new OmpAuthConfigurationWarning(
                    OmpAuthConfigurationWarning.DataProtectionKeyPathNotUncPathCode,
                    [dataProtectionKeyPath],
                    $"OmpAuth:DataProtectionKeyPath '{dataProtectionKeyPath}' is not a UNC path. " +
                    "A local key path will break auth-cookie sharing in load-balanced scenarios."));
            }
        }

        return new OmpAuthValidationResult
        {
            Warnings = warnings,
            EffectiveCookieName = effectiveCookieName,
            EffectiveApplicationName = effectiveApplicationName,
            EffectiveDataProtectionKeyPath = effectiveDataProtectionKeyPath
        };
    }

    private static string ResolveWebAppDataProtectionKeyPath(HostAgentSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.WebAppDataProtectionKeyPath))
        {
            return settings.WebAppDataProtectionKeyPath.Trim();
        }

        if (string.IsNullOrWhiteSpace(settings.WebAppsRoot))
        {
            return string.Empty;
        }

        var webAppsRoot = Path.GetFullPath(settings.WebAppsRoot.Trim());
        var runtimeRoot = Directory.GetParent(webAppsRoot)?.FullName;
        return string.IsNullOrWhiteSpace(runtimeRoot)
            ? string.Empty
            : Path.Join(runtimeRoot, "DataProtectionKeys");
    }

    private static string ResolveIisAppName(
        HostAgentSettings settings,
        WebAppDeploymentDescriptor deployment)
    {
        var appPath = ResolveRelativeIisAppPath(deployment);
        return string.IsNullOrWhiteSpace(appPath)
            ? $"{settings.IisSiteName.Trim()}/"
            : $"{settings.IisSiteName.Trim()}/{appPath}";
    }

    private static string ResolveTargetPath(
        HostAgentSettings settings,
        WebAppDeploymentDescriptor deployment)
    {
        if (!string.IsNullOrWhiteSpace(deployment.InstallPath))
        {
            return Path.GetFullPath(deployment.InstallPath.Trim());
        }

        var appPath = ResolveRelativeIisAppPath(deployment);
        if (string.IsNullOrWhiteSpace(appPath))
        {
            if (string.IsNullOrWhiteSpace(settings.PortalPhysicalPath))
            {
                throw new InvalidOperationException(
                    $"App instance '{deployment.AppInstanceKey}' has no InstallPath and maps to the IIS site root, but HostAgent:PortalPhysicalPath is not configured.");
            }

            return Path.GetFullPath(settings.PortalPhysicalPath.Trim());
        }

        if (string.IsNullOrWhiteSpace(settings.WebAppsRoot))
        {
            throw new InvalidOperationException(
                $"App instance '{deployment.AppInstanceKey}' has no InstallPath, but HostAgent:WebAppsRoot is not configured.");
        }

        var normalized = appPath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
        {
            throw new InvalidOperationException(
                $"App instance '{deployment.AppInstanceKey}' has an unsafe RoutePath and cannot be mapped under HostAgent:WebAppsRoot.");
        }

        var webAppsRoot = Path.GetFullPath(settings.WebAppsRoot.Trim());
        return DeploymentPath.CombineUnderRoot(
            webAppsRoot,
            normalized,
            $"App instance '{deployment.AppInstanceKey}' RoutePath");
    }

    private static string ResolveRelativeIisAppPath(WebAppDeploymentDescriptor deployment)
    {
        var route = Clean(deployment.RoutePath);
        if (!string.IsNullOrWhiteSpace(route))
        {
            if (Uri.TryCreate(route, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException(
                    $"App instance '{deployment.AppInstanceKey}' has an absolute RoutePath and cannot be mapped to a local IIS application.");
            }

            return route.Trim().Trim('/', '\\').Replace('\\', '/');
        }

        var installationName = Clean(deployment.InstallationName);
        if (!string.IsNullOrWhiteSpace(installationName)
            && !installationName.Equals("portal", StringComparison.OrdinalIgnoreCase))
        {
            return installationName.Trim().Trim('/', '\\').Replace('\\', '/');
        }

        return string.Empty;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool UseAppCmdAppPoolControl(HostAgentSettings settings)
        => settings.EnsureIisSite
            || (!settings.UseAppOfflineForWebAppDeployment
                && (settings.StopIisAppPoolForWebAppDeployment || settings.StartIisAppPoolAfterWebAppDeployment));

    private async Task EnsureIisApplicationAsync(
        HostAgentSettings settings,
        WebAppDeploymentDescriptor deployment,
        string targetPath,
        string iisAppName,
        string appPoolName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetPath);

        var appPath = ResolveRelativeIisAppPath(deployment);
        var siteRootPath = Path.GetFullPath(settings.PortalPhysicalPath.Trim());
        Directory.CreateDirectory(siteRootPath);

        if (string.IsNullOrWhiteSpace(appPath))
        {
            var identity = await ResolveIisAppPoolIdentityAsync(settings, deployment, appPoolName, cancellationToken);
            EnsureAppPool(
                settings,
                appPoolName,
                identity);
            EnsurePortalAppPoolFilesystemAccess(targetPath, appPoolName, identity, _logger);
            EnsureIisSite(settings, targetPath, appPoolName);
            return;
        }

        var rootAppPoolName = ResolvePortalAppPoolName(settings);
        var rootIdentity = await ResolveDefaultIisAppPoolIdentityAsync(settings, cancellationToken);
        EnsureAppPool(
            settings,
            rootAppPoolName,
            rootIdentity);
        EnsurePortalAppPoolFilesystemAccess(siteRootPath, rootAppPoolName, rootIdentity, _logger);
        EnsureIisSite(settings, siteRootPath, rootAppPoolName);
        EnsureAppPool(
            settings,
            appPoolName,
            await ResolveIisAppPoolIdentityAsync(settings, deployment, appPoolName, cancellationToken));
        EnsureIisChildApplication(settings, iisAppName, appPath, targetPath, appPoolName);
    }

    private static void EnsureIisSite(
        HostAgentSettings settings,
        string physicalPath,
        string appPoolName)
    {
        var siteName = settings.IisSiteName.Trim();
        if (!IisObjectExists("list", "site", $"/name:{siteName}"))
        {
            RunAppCmd(
                "add",
                "site",
                $"/name:{siteName}",
                $"/bindings:{CreateIisBinding(settings)}",
                $"/physicalPath:{physicalPath}");
        }
        else
        {
            RunAppCmd("set", "vdir", $"/vdir.name:{siteName}/", $"/physicalPath:{physicalPath}");
        }

        RunAppCmd("set", "app", $"/app.name:{siteName}/", $"/applicationPool:{appPoolName}");
        EnsureIisBindingCertificate(settings);
        EnsureIisAuthentication(siteName, anonymousEnabled: true, windowsEnabled: false);
    }

    private static void EnsureIisChildApplication(
        HostAgentSettings settings,
        string iisAppName,
        string appPath,
        string physicalPath,
        string appPoolName)
    {
        if (!IisApplicationExists(iisAppName))
        {
            RunAppCmd(
                "add",
                "app",
                $"/site.name:{settings.IisSiteName.Trim()}",
                $"/path:/{appPath}",
                $"/physicalPath:{physicalPath}",
                $"/applicationPool:{appPoolName}");
        }
        else
        {
            RunAppCmd("set", "vdir", $"/vdir.name:{iisAppName}/", $"/physicalPath:{physicalPath}");
            RunAppCmd("set", "app", $"/app.name:{iisAppName}", $"/applicationPool:{appPoolName}");
        }

        EnsureIisAuthentication(
            iisAppName,
            anonymousEnabled: true,
            windowsEnabled: IsOmpAuthenticationAppPath(appPath));
    }

    private static void EnsureIisAuthentication(
        string location,
        bool anonymousEnabled,
        bool windowsEnabled)
    {
        using var serverManager = CreateIisServerManager();
        var configuration = GetIisApplicationHostConfiguration(serverManager);
        var anonymousAuthentication = GetIisConfigurationSection(
            configuration,
            "system.webServer/security/authentication/anonymousAuthentication",
            location);
        SetIisConfigurationValue(anonymousAuthentication, "enabled", anonymousEnabled);
        SetIisConfigurationValue(anonymousAuthentication, "userName", string.Empty);
        SetIisConfigurationValue(anonymousAuthentication, "password", string.Empty);

        var windowsAuthentication = GetIisConfigurationSection(
            configuration,
            "system.webServer/security/authentication/windowsAuthentication",
            location);
        SetIisConfigurationValue(windowsAuthentication, "enabled", windowsEnabled);
        CommitIisChanges(serverManager);
    }

    private static bool IsOmpAuthenticationAppPath(string appPath)
        => string.Equals(
            appPath.Trim().Trim('/', '\\').Replace('\\', '/'),
            "auth",
            StringComparison.OrdinalIgnoreCase);

    private static void EnsureAppPool(
        HostAgentSettings settings,
        string appPoolName,
        HostAgentIisAppPoolIdentitySettings identity)
    {
        if (!IisObjectExists("list", "apppool", $"/name:{appPoolName}"))
        {
            RunAppCmd("add", "apppool", $"/name:{appPoolName}");
        }

        RunAppCmd(
            "set",
            "apppool",
            $"/apppool.name:{appPoolName}",
            "/managedRuntimeVersion:",
            "/processModel.loadUserProfile:true");

        if (!string.IsNullOrWhiteSpace(identity.UserName))
        {
            ConfigureSpecificUserAppPoolIdentity(appPoolName, identity);
        }
    }

    private static void ConfigureSpecificUserAppPoolIdentity(
        string appPoolName,
        HostAgentIisAppPoolIdentitySettings identity)
    {
        using var serverManager = CreateIisServerManager();
        var appPools = GetPropertyValue(serverManager, "ApplicationPools");
        var appPool = GetIndexedValue(appPools, appPoolName)
            ?? throw new InvalidOperationException($"IIS app pool '{appPoolName}' was not found after creation.");
        var processModel = GetPropertyValue(appPool, "ProcessModel");
        var identityType = GetIisEnumValue("Microsoft.Web.Administration.ProcessModelIdentityType", "SpecificUser");
        SetPropertyValue(processModel, "IdentityType", identityType);
        SetPropertyValue(processModel, "UserName", identity.UserName.Trim());
        if (!string.IsNullOrWhiteSpace(identity.Password))
        {
            SetPropertyValue(processModel, "Password", identity.Password);
        }

        CommitIisChanges(serverManager);
    }

    private static void EnsurePortalAppPoolFilesystemAccess(
        string portalPath,
        string appPoolName,
        HostAgentIisAppPoolIdentitySettings identity,
        ILogger logger)
    {
        Directory.CreateDirectory(portalPath);
        var accountNames = ResolveAppPoolFilesystemAccountNames(appPoolName, identity).ToList();
        if (accountNames.Count == 0)
        {
            return;
        }

        if (!TryGrantDirectoryAccess(portalPath, accountNames, "M", out var error))
        {
            throw new InvalidOperationException(
                $"Could not grant Modify access to Portal path '{portalPath}' for IIS app pool '{appPoolName}'. {error}");
        }

        logger.LogDebug(
            "Ensured Portal filesystem Modify access. Path={Path}, AppPoolName={AppPoolName}",
            portalPath,
            appPoolName);
    }

    private static bool TryGrantDirectoryAccess(
        string path,
        IReadOnlyCollection<string> accountNames,
        string permission,
        out string error)
    {
        var lastError = string.Empty;
        var grantResults = accountNames.Select(accountName => RunProcess(
            "icacls.exe",
            [path, "/grant", $"{accountName}:(OI)(CI)({permission})"]));

        foreach (var result in grantResults)
        {
            if (result.ExitCode == 0)
            {
                error = string.Empty;
                return true;
            }

            lastError = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim();
        }

        error = string.IsNullOrWhiteSpace(lastError)
            ? $"No configured account could be granted {permission} access."
            : lastError;
        return false;
    }

    private static IEnumerable<string> ResolveAppPoolFilesystemAccountNames(
        string appPoolName,
        HostAgentIisAppPoolIdentitySettings identity)
    {
        if (!string.IsNullOrWhiteSpace(identity.UserName)
            && !IsBuiltInServiceIdentity(identity.UserName))
        {
            foreach (var candidate in ResolveWindowsAccountNameCandidates(identity.UserName))
            {
                yield return candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(appPoolName))
        {
            yield return $@"IIS AppPool\{appPoolName.Trim()}";
        }
    }

    private static IEnumerable<string> ResolveWindowsAccountNameCandidates(string configuredAccountName)
    {
        var trimmed = configuredAccountName.Trim();
        var normalized = TryNormalizeDomainAccountName(trimmed);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            yield return normalized;
        }

        yield return trimmed;
    }

    private static string TryNormalizeDomainAccountName(string accountName)
    {
        var atIndex = accountName.IndexOf('@', StringComparison.Ordinal);
        if (atIndex > 0 && atIndex < accountName.Length - 1)
        {
            var userName = accountName[..atIndex];
            var domain = NormalizeNetBiosDomainName(accountName[(atIndex + 1)..]);
            return string.IsNullOrWhiteSpace(domain) ? string.Empty : domain + "\\" + userName;
        }

        var slashIndex = accountName.IndexOf('\\', StringComparison.Ordinal);
        if (slashIndex > 0 && slashIndex < accountName.Length - 1)
        {
            var domain = NormalizeNetBiosDomainName(accountName[..slashIndex]);
            return string.IsNullOrWhiteSpace(domain)
                ? string.Empty
                : domain + "\\" + accountName[(slashIndex + 1)..];
        }

        return string.Empty;
    }

    private static string NormalizeNetBiosDomainName(string domainName)
    {
        var trimmed = domainName.Trim();
        if (trimmed.Equals(".", StringComparison.Ordinal))
        {
            return ".";
        }

        var dotIndex = trimmed.IndexOf('.', StringComparison.Ordinal);
        var netBios = dotIndex > 0 ? trimmed[..dotIndex] : trimmed;
        return netBios.ToUpperInvariant();
    }

    private static bool IsBuiltInServiceIdentity(string accountName)
    {
        var normalized = accountName.Trim();
        return normalized.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("LocalService", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NetworkService", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("ApplicationPoolIdentity", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HostAgentIisAppPoolIdentitySettings> ResolveIisAppPoolIdentityAsync(
        HostAgentSettings settings,
        WebAppDeploymentDescriptor deployment,
        string appPoolName,
        CancellationToken cancellationToken)
    {
        var configuredOverride = ResolveIisAppPoolOverrideKeys(deployment, appPoolName)
            .Select(key => TryGetIisAppPoolIdentityOverride(settings, key, out var identity)
                ? identity
                : null)
            .FirstOrDefault(identity => identity is not null && HasConfiguredIisAppPoolIdentity(identity));
        if (configuredOverride is not null)
        {
            return await ResolveStoredIisAppPoolPasswordAsync(configuredOverride, cancellationToken);
        }

        return await ResolveDefaultIisAppPoolIdentityAsync(settings, cancellationToken);
    }

    private static bool HasConfiguredIisAppPoolIdentity(HostAgentIisAppPoolIdentitySettings identity)
        => !string.IsNullOrWhiteSpace(identity.UserName)
            || !string.IsNullOrWhiteSpace(identity.PasswordCredentialKey);

    private Task<HostAgentIisAppPoolIdentitySettings> ResolveDefaultIisAppPoolIdentityAsync(
        HostAgentSettings settings,
        CancellationToken cancellationToken)
        => ResolveStoredIisAppPoolPasswordAsync(new HostAgentIisAppPoolIdentitySettings
        {
            UserName = settings.IisAppPoolUserName,
            Password = settings.IisAppPoolPassword,
            PasswordCredentialKey = settings.IisAppPoolPasswordCredentialKey
        }, cancellationToken);

    private async Task<HostAgentIisAppPoolIdentitySettings> ResolveStoredIisAppPoolPasswordAsync(
        HostAgentIisAppPoolIdentitySettings identity,
        CancellationToken cancellationToken)
    {
        var result = new HostAgentIisAppPoolIdentitySettings
        {
            UserName = identity.UserName,
            Password = identity.Password,
            PasswordCredentialKey = identity.PasswordCredentialKey
        };

        if (string.IsNullOrWhiteSpace(result.PasswordCredentialKey))
        {
            return result;
        }

        var credential = await _credentialStore.TryReadCredentialAsync(result.PasswordCredentialKey, cancellationToken);
        if (credential is null)
        {
            throw new InvalidOperationException(
                $"IIS app pool credential '{result.PasswordCredentialKey}' could not be resolved from HostAgent credential store.");
        }

        if (string.IsNullOrWhiteSpace(result.UserName))
        {
            result.UserName = credential.UserName;
        }

        result.Password = credential.Password;
        return result;
    }

    private static IEnumerable<string> ResolveIisAppPoolOverrideKeys(
        WebAppDeploymentDescriptor deployment,
        string appPoolName)
    {
        yield return deployment.AppInstanceKey;

        if (!string.IsNullOrWhiteSpace(deployment.RoutePath))
        {
            yield return deployment.RoutePath.Trim().Trim('/', '\\').Replace('\\', '/');
        }

        yield return appPoolName;
    }

    private static bool TryGetIisAppPoolIdentityOverride(
        HostAgentSettings settings,
        string key,
        out HostAgentIisAppPoolIdentitySettings identity)
    {
        var overrides = settings.IisAppPoolOverrides;
        if (overrides is not { Count: > 0 } || string.IsNullOrWhiteSpace(key))
        {
            identity = new HostAgentIisAppPoolIdentitySettings();
            return false;
        }

        if (overrides.TryGetValue(key, out var configuredIdentity) && configuredIdentity is not null)
        {
            identity = configuredIdentity;
            return true;
        }

        var pair = overrides.FirstOrDefault(pair =>
            pair.Value is not null
            && string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
        if (pair.Value is not null)
        {
            identity = pair.Value;
            return true;
        }

        identity = new HostAgentIisAppPoolIdentitySettings();
        return false;
    }

    private static string ResolveIisAppPoolName(
        HostAgentSettings settings,
        WebAppDeploymentDescriptor deployment)
    {
        var appPath = ResolveRelativeIisAppPath(deployment);
        var baseName = string.IsNullOrWhiteSpace(appPath) ? "portal" : deployment.AppInstanceKey;
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = deployment.DisplayName;
        }

        return BuildIisAppPoolName(settings, baseName);
    }

    private static string ResolvePortalAppPoolName(HostAgentSettings settings)
        => BuildIisAppPoolName(settings, "portal");

    private static string BuildIisAppPoolName(HostAgentSettings settings, string value)
    {
        var prefix = string.IsNullOrWhiteSpace(settings.IisAppPoolNamePrefix)
            ? string.Empty
            : settings.IisAppPoolNamePrefix.Trim();
        var normalized = new string(value
            .Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_')
            .ToArray());
        normalized = normalized.Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "app";
        }

        var name = prefix + normalized;
        return name.Length <= MaxIisAppPoolNameLength ? name : name[..MaxIisAppPoolNameLength].TrimEnd('_', '.', '-');
    }

    private static string CreateIisBinding(HostAgentSettings settings)
    {
        var protocol = settings.IisBindingProtocol.Trim();
        return $"{protocol}/{CreateIisBindingInformation(settings)}";
    }

    private static string CreateIisBindingInformation(HostAgentSettings settings)
    {
        var hostHeader = settings.IisBindingHostHeader?.Trim() ?? string.Empty;
        return $"*:{settings.IisBindingPort}:{hostHeader}";
    }

    private static void EnsureIisBindingCertificate(HostAgentSettings settings)
    {
        if (!string.Equals(settings.IisBindingProtocol.Trim(), "https", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var thumbprint = ResolveCertificateThumbprint(settings);
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return;
        }

        var storeName = string.IsNullOrWhiteSpace(settings.IisBindingCertificateStoreName)
            ? "My"
            : settings.IisBindingCertificateStoreName.Trim();
        var bindingTarget = CreateNetshSslBindingTarget(settings);

        // IIS appcmd can create the HTTPS site binding, but on some Windows/IIS
        // versions it cannot set certificateHash on the binding collection. The
        // HTTP.sys SSL binding is the authoritative backend binding and works
        // for layered TLS termination where the public certificate lives on a
        // load balancer and IIS uses a node/server certificate.
        RunNetsh("http", "delete", "sslcert", bindingTarget);
        RunNetsh(
            "http",
            "add",
            "sslcert",
            bindingTarget,
            $"certhash={thumbprint}",
            $"appid={IisSslBindingAppId}",
            $"certstorename={storeName}");
    }

    private static string ResolveCertificateThumbprint(HostAgentSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.IisBindingCertificateThumbprint))
        {
            return NormalizeCertificateHex(settings.IisBindingCertificateThumbprint);
        }

        if (string.IsNullOrWhiteSpace(settings.IisBindingCertificateSerialNumber))
        {
            return string.Empty;
        }

        var storeName = string.IsNullOrWhiteSpace(settings.IisBindingCertificateStoreName)
            ? "My"
            : settings.IisBindingCertificateStoreName.Trim();
        var expectedSerialNumber = NormalizeCertificateHex(settings.IisBindingCertificateSerialNumber);

        using var store = new X509Store(storeName, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificate = store.Certificates.OfType<X509Certificate2>().FirstOrDefault(certificate =>
            string.Equals(
                NormalizeCertificateHex(certificate.SerialNumber),
                expectedSerialNumber,
                StringComparison.OrdinalIgnoreCase));
        if (certificate is not null)
        {
            return NormalizeCertificateHex(certificate.Thumbprint);
        }

        throw new InvalidOperationException(
            $"IIS binding certificate with serial number '{settings.IisBindingCertificateSerialNumber}' was not found in LocalMachine/{storeName}.");
    }

    private static string CreateNetshSslBindingTarget(HostAgentSettings settings)
    {
        var hostHeader = settings.IisBindingHostHeader?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(hostHeader)
            ? $"ipport=0.0.0.0:{settings.IisBindingPort}"
            : $"hostnameport={hostHeader}:{settings.IisBindingPort}";
    }

    private static string NormalizeCertificateHex(string value)
        => new(value
            .Where(Uri.IsHexDigit)
            .Select(static ch => char.ToUpperInvariant(ch))
            .ToArray());

    private static void RunNetsh(params string[] arguments)
    {
        var result = RunProcess("netsh.exe", arguments);
        if (result.ExitCode != 0 && !IsExpectedNetshSslDeleteFailure(arguments))
        {
            var message = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            throw new InvalidOperationException(
                $"netsh.exe failed with exit code {result.ExitCode}: {message.Trim()}");
        }
    }

    private static bool IsExpectedNetshSslDeleteFailure(IReadOnlyList<string> arguments)
        => arguments.Count >= 3
            && string.Equals(arguments[0], "http", StringComparison.OrdinalIgnoreCase)
            && string.Equals(arguments[1], "delete", StringComparison.OrdinalIgnoreCase)
            && string.Equals(arguments[2], "sslcert", StringComparison.OrdinalIgnoreCase);

    private static string CreateAppOfflineFile(
        string targetPath,
        WebAppDeploymentDescriptor deployment)
    {
        Directory.CreateDirectory(targetPath);
        var path = Path.Join(targetPath, "app_offline.htm");
        var content = $"""
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>Maintenance</title></head>
<body>OpenModulePlatform HostAgent is updating {deployment.AppInstanceKey}.</body>
</html>
""";
        File.WriteAllText(path, content);
        return path;
    }

    private static string GetAppCmdPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var appCmdPath = Path.Join(windowsDirectory, "System32", "inetsrv", "appcmd.exe");
        if (!File.Exists(appCmdPath))
        {
            throw new FileNotFoundException($"IIS appcmd.exe was not found: '{appCmdPath}'.", appCmdPath);
        }

        return appCmdPath;
    }

    private static string GetIisAppPoolName(string iisAppName)
    {
        var output = RunAppCmd("list", "app", iisAppName);
        var text = string.Join('\n', output);
        const string marker = "applicationPool:";
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            throw new InvalidOperationException($"IIS application '{iisAppName}' was not found or did not report an application pool.");
        }

        start += marker.Length;
        var end = text.IndexOf(')', start);
        if (end < 0)
        {
            end = text.Length;
        }

        var appPoolName = text[start..end].Trim();
        if (string.IsNullOrWhiteSpace(appPoolName))
        {
            throw new InvalidOperationException($"IIS application '{iisAppName}' has no application pool.");
        }

        return appPoolName;
    }

    private static string? GetAppPoolState(string appPoolName)
    {
        var output = RunAppCmd("list", "apppool", $"/name:{appPoolName}");
        var text = string.Join('\n', output);
        const string marker = "state:";
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = text.IndexOfAny([',', ')'], start);
        if (end < 0)
        {
            end = text.Length;
        }

        return text[start..end].Trim();
    }

    private static async Task<bool> StopAppPoolIfRunningAsync(
        string appPoolName,
        int timeoutSeconds,
        string? initialState,
        CancellationToken cancellationToken)
    {
        var state = initialState ?? GetAppPoolState(appPoolName);
        if (!string.Equals(state, "Started", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        RunAppCmd("stop", "apppool", $"/apppool.name:{appPoolName}");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            state = GetAppPoolState(appPoolName);
            if (string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            await Task.Delay(AppPoolStatePollDelay, cancellationToken);
        }

        throw new TimeoutException($"IIS app pool '{appPoolName}' did not stop within {timeoutSeconds} seconds.");
    }

    private static void StartAppPoolIfStopped(string appPoolName)
    {
        var state = GetAppPoolState(appPoolName);
        if (string.Equals(state, "Started", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RunAppCmd("start", "apppool", $"/apppool.name:{appPoolName}");
    }

    private static bool TryStartAppPool(string appPoolName, ILogger logger)
    {
        // The original deployment failure is the actionable error. Restart
        // recovery is best-effort and should not mask that primary failure.
        try
        {
            StartAppPoolIfStopped(appPoolName);
            return true;
        }
        catch (Exception ex) when (IsExpectedRecoveryStartFailure(ex))
        {
            logger.LogDebug(
                ex,
                "Failed to restart IIS app pool after deployment failure. AppPoolName={AppPoolName}",
                appPoolName);
            return false;
        }
    }

    private async Task RecoverInterruptedDeploymentsAsync(string hostKey, CancellationToken cancellationToken)
    {
        var candidates = await _repository.GetWebAppDeploymentRecoveryCandidatesAsync(hostKey, cancellationToken);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DeploymentRuntimeStopMarker.Exists(candidate.TargetPath))
            {
                continue;
            }

            try
            {
                var marker = DeploymentRuntimeStopMarker.TryRead(candidate.TargetPath);
                if (marker is null || marker.IsExpired(DateTimeOffset.UtcNow))
                {
                    DeploymentRuntimeStopMarker.Delete(candidate.TargetPath);
                    _logger.LogWarning(
                        "Deleted stale or unreadable interrupted web app deployment marker. AppInstanceId={AppInstanceId}, AppInstanceKey={AppInstanceKey}, TargetPath={TargetPath}",
                        candidate.AppInstanceId,
                        candidate.AppInstanceKey,
                        candidate.TargetPath);
                    continue;
                }

                var runtimeName = string.IsNullOrWhiteSpace(marker.RuntimeName)
                    ? candidate.RuntimeName
                    : marker.RuntimeName.Trim();
                if (string.IsNullOrWhiteSpace(runtimeName))
                {
                    _logger.LogWarning(
                        "Web app deployment recovery skipped because the interrupted deployment marker has no runtime name. AppInstanceId={AppInstanceId}, AppInstanceKey={AppInstanceKey}, TargetPath={TargetPath}",
                        candidate.AppInstanceId,
                        candidate.AppInstanceKey,
                        candidate.TargetPath);
                    continue;
                }

                StartAppPoolIfStopped(runtimeName);
                DeploymentRuntimeStopMarker.Delete(candidate.TargetPath);
                _logger.LogInformation(
                    "Recovered IIS app pool after an interrupted web app deployment. AppInstanceId={AppInstanceId}, AppInstanceKey={AppInstanceKey}, AppPoolName={AppPoolName}, TargetPath={TargetPath}",
                    candidate.AppInstanceId,
                    candidate.AppInstanceKey,
                    runtimeName,
                    candidate.TargetPath);
            }
            catch (Exception ex) when (IsExpectedRecoveryStartFailure(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Failed to recover IIS app pool after an interrupted web app deployment. AppInstanceId={AppInstanceId}, AppInstanceKey={AppInstanceKey}, RuntimeName={RuntimeName}, TargetPath={TargetPath}",
                    candidate.AppInstanceId,
                    candidate.AppInstanceKey,
                    candidate.RuntimeName,
                    candidate.TargetPath);
            }
        }
    }

    private static bool IisObjectExists(params string[] arguments)
        => RunAppCmdRaw(arguments, throwOnFailure: false).ExitCode == 0;

    private static bool IisApplicationExists(string iisAppName)
        => RunAppCmd("list", "app")
            .Any(line => line.Contains(
                $"\"{iisAppName}\"",
                StringComparison.OrdinalIgnoreCase));

    private static IDisposable CreateIisServerManager()
    {
        var serverManagerType = LoadMicrosoftWebAdministrationType("Microsoft.Web.Administration.ServerManager");
        return Activator.CreateInstance(serverManagerType) as IDisposable
            ?? throw new InvalidOperationException("Could not create Microsoft.Web.Administration.ServerManager.");
    }

    private static Type LoadMicrosoftWebAdministrationType(string typeName)
    {
        EnsureIisAssemblyResolver();
        var assembly = Assembly.LoadFrom(GetMicrosoftWebAdministrationPath());
        return assembly.GetType(typeName, throwOnError: true)
            ?? throw new InvalidOperationException($"Microsoft.Web.Administration type '{typeName}' was not found.");
    }

    private static void EnsureIisAssemblyResolver()
    {
        if (iisAssemblyResolverRegistered)
        {
            return;
        }

        lock (IisAssemblyResolverLock)
        {
            if (iisAssemblyResolverRegistered)
            {
                return;
            }

            AssemblyLoadContext.Default.Resolving += ResolveIisDependency;
            iisAssemblyResolverRegistered = true;
        }
    }

    private static Assembly? ResolveIisDependency(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (!string.Equals(
                assemblyName.Name,
                SystemSecurityPermissionsAssemblyName,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Microsoft.Web.Administration is loaded from the IIS folder, but the
        // compatibility assembly it needs is deployed beside the HostAgent.
        var dependencyPath = Path.Join(AppContext.BaseDirectory, SystemSecurityPermissionsAssemblyName + ".dll");
        return File.Exists(dependencyPath)
            ? context.LoadFromAssemblyPath(dependencyPath)
            : null;
    }

    private static string GetMicrosoftWebAdministrationPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var assemblyPath = Path.Join(windowsDirectory, "System32", "inetsrv", "Microsoft.Web.Administration.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                $"Microsoft.Web.Administration.dll was not found: '{assemblyPath}'.",
                assemblyPath);
        }

        return assemblyPath;
    }

    private static object GetIisApplicationHostConfiguration(object serverManager)
        => InvokeRequiredMethod(serverManager, "GetApplicationHostConfiguration");

    private static object GetIisConfigurationSection(
        object configuration,
        string sectionPath,
        string location)
    {
        var method = configuration.GetType().GetMethod(
            "GetSection",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(string), typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException("IIS configuration GetSection(path, location) method was not found.");

        return method.Invoke(configuration, [sectionPath, location])
            ?? throw new InvalidOperationException($"IIS configuration section '{sectionPath}' was not found for '{location}'.");
    }

    private static object GetIisEnumValue(string typeName, string value)
    {
        var enumType = LoadMicrosoftWebAdministrationType(typeName);
        return Enum.Parse(enumType, value, ignoreCase: false);
    }

    private static object GetPropertyValue(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found on '{target.GetType().FullName}'.");
        return property.GetValue(target)
            ?? throw new InvalidOperationException($"Property '{propertyName}' on '{target.GetType().FullName}' returned null.");
    }

    private static void SetPropertyValue(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found on '{target.GetType().FullName}'.");
        property.SetValue(target, value);
    }

    private static object? GetIndexedValue(object target, string key)
    {
        var property = target.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(property =>
            {
                var indexes = property.GetIndexParameters();
                return indexes.Length == 1 && indexes[0].ParameterType == typeof(string);
            })
            ?? throw new InvalidOperationException($"String indexer was not found on '{target.GetType().FullName}'.");

        return property.GetValue(target, [key]);
    }

    private static void SetIisConfigurationValue(object section, string propertyName, object value)
    {
        var indexer = section.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(property =>
            {
                var indexes = property.GetIndexParameters();
                return indexes.Length == 1 && indexes[0].ParameterType == typeof(string);
            })
            ?? throw new InvalidOperationException($"IIS configuration indexer was not found on '{section.GetType().FullName}'.");

        indexer.SetValue(section, value, [propertyName]);
    }

    private static object InvokeRequiredMethod(object target, string methodName)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [],
            modifiers: null)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found on '{target.GetType().FullName}'.");

        return method.Invoke(target, null)
            ?? throw new InvalidOperationException($"Method '{methodName}' on '{target.GetType().FullName}' returned null.");
    }

    private static void CommitIisChanges(object serverManager)
    {
        var method = serverManager.GetType().GetMethod(
            "CommitChanges",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [],
            modifiers: null)
            ?? throw new InvalidOperationException($"Method 'CommitChanges' was not found on '{serverManager.GetType().FullName}'.");

        method.Invoke(serverManager, null);
    }

    private static string[] RunAppCmd(params string[] arguments)
    {
        var result = RunAppCmdRaw(arguments, throwOnFailure: true);
        return result.StdOut
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static AppCmdResult RunAppCmdRaw(IReadOnlyList<string> arguments, bool throwOnFailure)
    {
        var result = RunProcess(GetAppCmdPath(), arguments);
        if (throwOnFailure && result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            throw new InvalidOperationException(CreateAppCmdFailureMessage(result.ExitCode, message));
        }

        return new AppCmdResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    private static ProcessResult RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var result = HostAgentProcessRunner.Run(fileName, arguments);
        return new ProcessResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    private static string CreateAppCmdFailureMessage(int exitCode, string message)
    {
        var trimmed = message.Trim();
        var result = $"appcmd.exe failed with exit code {exitCode}: {trimmed}";
        // appcmd.exe does not expose a stable structured error code for this
        // IIS configuration ACL failure. Keep this message enhancement as a
        // best-effort diagnostic hint and preserve the original appcmd output.
        if (trimmed.Contains("redirection.config", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase))
        {
            result += " HostAgent could not read IIS configuration. Grant the HostAgent service identity access to IIS configuration, or keep HostAgent:UseAppOfflineForWebAppDeployment enabled so web-app deployment does not need appcmd.exe.";
        }

        return result;
    }

    private static bool IsExpectedDeploymentFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException;

    private static bool IsExpectedRecoveryStartFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException
            or System.ComponentModel.Win32Exception;

    private sealed record AppCmdResult(int ExitCode, string StdOut, string StdErr);

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
