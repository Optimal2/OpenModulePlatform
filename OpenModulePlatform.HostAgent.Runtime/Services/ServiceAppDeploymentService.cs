using System.Collections.Concurrent;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class ServiceAppDeploymentService
{
    private const int ScAccessDeniedExitCode = 5;
    private const int MaxConsecutiveStartAttempts = 3;

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly IOmpHostArtifactRepository _repository;
    private readonly HostAgentCredentialStoreService _credentialStore;
    private readonly ILogger<ServiceAppDeploymentService> _logger;
    private readonly IWindowsServiceControl _serviceControl;
    private readonly ConcurrentDictionary<string, int> _consecutiveStartAttemptsByServiceName = new(StringComparer.OrdinalIgnoreCase);

    public ServiceAppDeploymentService(
        IOptionsMonitor<HostAgentSettings> settings,
        IOmpHostArtifactRepository repository,
        HostAgentCredentialStoreService credentialStore,
        ILogger<ServiceAppDeploymentService> logger,
        IWindowsServiceControl? serviceControl = null)
    {
        _settings = settings;
        _repository = repository;
        _credentialStore = credentialStore;
        _logger = logger;
        _serviceControl = serviceControl ?? WindowsServiceControl.Instance;
    }

    public async Task DeployDesiredServiceAppsAsync(string hostKey, CancellationToken cancellationToken)
        => await DeployDesiredServiceAppsAsync(hostKey, null, cancellationToken);

    public async Task DeployDesiredServiceAppsAsync(
        string hostKey,
        IReadOnlyDictionary<string, string>? deploySetWarningsByModuleInstanceKey,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.DeployServiceApps)
        {
            return;
        }

        settings.Validate();

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Service app deployment requires Windows and sc.exe.");
        }

        await RecoverInterruptedDeploymentsAsync(hostKey, settings, cancellationToken);

        var deployments = await _repository.GetDesiredServiceAppDeploymentsAsync(
            hostKey,
            settings.MaxArtifactsPerCycle,
            cancellationToken);

        _logger.LogInformation(
            "Resolved desired service app deployments. HostKey={HostKey}, Count={Count}",
            hostKey,
            deployments.Count);

        var resolvedServiceNames = deployments
            .ToDictionary(
                d => d.AppInstanceId,
                d => ServiceAppDeploymentNaming.ResolveServiceName(d, ResolveExecutableRelativePath(d)));

        foreach (var deployment in deployments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeployAsync(settings, deployment, resolvedServiceNames, deploySetWarningsByModuleInstanceKey, cancellationToken);
        }
    }

    private async Task DeployAsync(
        HostAgentSettings settings,
        ServiceAppDeploymentDescriptor deployment,
        IReadOnlyDictionary<Guid, string> resolvedServiceNames,
        IReadOnlyDictionary<string, string>? deploySetWarningsByModuleInstanceKey,
        CancellationToken cancellationToken)
    {
        string? targetPath = null;
        string? serviceName = null;
        var serviceStopped = false;
        var stopMarkerWritten = false;
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

        try
        {
            var executableRelativePath = ResolveExecutableRelativePath(deployment);
            serviceName = ServiceAppDeploymentNaming.ResolveServiceName(deployment, executableRelativePath);
            targetPath = ServiceAppDeploymentNaming.ResolveTargetPath(settings, deployment, serviceName);
            var targetExecutablePath = DeploymentPath.CombineUnderRoot(
                targetPath,
                executableRelativePath,
                $"Service app instance '{deployment.AppInstanceKey}' executable path");
            var serviceIdentity = await ResolveServiceAppIdentityAsync(
                settings,
                deployment,
                serviceName,
                cancellationToken);

            var configuredConnectionString = _repository.GetConfiguredConnectionString();
            var configurationFiles = await _repository.GetArtifactConfigurationFilesAsync(
                deployment.ArtifactId,
                deployment.HostKey,
                cancellationToken);
            configurationFiles = ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
                configurationFiles,
                deployment,
                configuredConnectionString,
                settings);
            var configurationVariables = ArtifactConfigurationFileWriter.CreateVariables(
                deployment,
                configuredConnectionString,
                settings);

            var lockStatus = DeploymentLockFile.ReadStatus(targetPath, DateTimeOffset.UtcNow);
            if (lockStatus.IsLocked)
            {
                var message = lockStatus.ToDeploymentSkippedMessage("Service app");
                _logger.LogInformation(
                    "Service app deployment skipped because a deployment lock is active. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Version={Version}, LockPath={LockPath}",
                    deployment.AppInstanceId,
                    deployment.ArtifactId,
                    deployment.Version,
                    lockStatus.Path);

                await _repository.PublishAppDeploymentResultAsync(
                    deployment,
                    WithDeploySetWarning(AppDeploymentResult.Warning(targetPath, serviceName, message)),
                    cancellationToken);
                return;
            }

            if (IsAlreadyApplied(deployment, targetPath, serviceName, targetExecutablePath)
                && ArtifactConfigurationFileWriter.AreApplied(targetPath, configurationFiles, configurationVariables))
            {
                EnsureWindowsService(deployment, serviceName, targetExecutablePath);

                var identityCheck = await EnsureServiceIdentityAsync(
                    settings,
                    deployment,
                    serviceName,
                    serviceIdentity,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(identityCheck.WarningMessage))
                {
                    await _repository.PublishAppDeploymentResultAsync(
                        deployment,
                        WithDeploySetWarning(
                            AddIdentityCheck(
                                AppDeploymentResult.Warning(targetPath, serviceName, identityCheck.WarningMessage),
                                identityCheck)),
                        cancellationToken);
                    return;
                }

                var reconcileRunningResult = await EnsureServiceRunningIfDesiredAsync(
                    settings,
                    deployment,
                    targetPath,
                    serviceName,
                    identityCheck,
                    cancellationToken);
                if (reconcileRunningResult is not null)
                {
                    await _repository.PublishAppDeploymentResultAsync(
                        deployment,
                        WithDeploySetWarning(AddIdentityCheck(reconcileRunningResult, identityCheck)),
                        cancellationToken);
                    return;
                }

                await _repository.PublishAppDeploymentResultAsync(
                    deployment,
                    WithDeploySetWarning(
                        AddIdentityCheck(
                            AppDeploymentResult.Succeeded(targetPath, serviceName, applied: identityCheck.Applied),
                            identityCheck)),
                    cancellationToken);
                return;
            }

            var deploymentLock = await HostAgentDeploymentLockLease.TryAcquireAsync(
                targetPath,
                deployment.AppInstanceKey,
                $"HostAgent {deployment.HostKey}",
                "HostAgent service app deployment is running.",
                _logger,
                cancellationToken);
            if (deploymentLock.Lease is null)
            {
                var activeLock = deploymentLock.ExistingLockStatus
                    ?? DeploymentLockStatus.Locked(
                        DeploymentLockFile.GetPath(targetPath),
                        null,
                        "Deployment lock became active before HostAgent could begin deployment.");
                var message = activeLock.ToDeploymentSkippedMessage("Service app");
                _logger.LogInformation(
                    "Service app deployment skipped because a deployment lock became active before HostAgent acquired its deployment lease. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Version={Version}, LockPath={LockPath}",
                    deployment.AppInstanceId,
                    deployment.ArtifactId,
                    deployment.Version,
                    activeLock.Path);

                await _repository.PublishAppDeploymentResultAsync(
                    deployment,
                    WithDeploySetWarning(AppDeploymentResult.Warning(targetPath, serviceName, message)),
                    cancellationToken);
                return;
            }

            await using var deploymentLockLease = deploymentLock.Lease!;

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                WithDeploySetWarning(AppDeploymentResult.Running(targetPath, serviceName)),
                cancellationToken);

            if (settings.StopServiceForServiceAppDeployment)
            {
                if (settings.StartServiceAfterServiceAppDeployment && IsServiceRunning(serviceName))
                {
                    DeploymentRuntimeStopMarker.Write(
                        targetPath,
                        "service-app",
                        serviceName,
                        deployment.AppInstanceId,
                        deployment.AppInstanceKey,
                        deployment.HostKey);
                    stopMarkerWritten = true;
                }

                serviceStopped = StopServiceIfRunning(serviceName, settings.ServiceAppStopTimeoutSeconds);
                if (!serviceStopped && stopMarkerWritten)
                {
                    DeploymentRuntimeStopMarker.Delete(targetPath);
                    stopMarkerWritten = false;
                }
            }

            var renameCleanup = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
                settings,
                deployment,
                executableRelativePath,
                serviceName,
                resolvedServiceNames);
            if (renameCleanup.ShouldCleanUp
                && !string.IsNullOrWhiteSpace(renameCleanup.OldServiceName)
                && !string.IsNullOrWhiteSpace(renameCleanup.OldTargetPath))
            {
                try
                {
                    CleanUpRenamedService(renameCleanup.OldServiceName, renameCleanup.OldTargetPath, targetPath);
                    _logger.LogInformation(
                        "Cleaned up renamed service app runtime. AppInstanceId={AppInstanceId}, OldServiceName={OldServiceName}, OldTargetPath={OldTargetPath}, NewServiceName={NewServiceName}, NewTargetPath={NewTargetPath}",
                        deployment.AppInstanceId,
                        renameCleanup.OldServiceName,
                        renameCleanup.OldTargetPath,
                        serviceName,
                        targetPath);
                }
                catch (Exception ex) when (IsExpectedDeploymentFailure(ex))
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to clean up renamed service app runtime; continuing with deployment. AppInstanceId={AppInstanceId}, OldServiceName={OldServiceName}, OldTargetPath={OldTargetPath}",
                        deployment.AppInstanceId,
                        renameCleanup.OldServiceName,
                        renameCleanup.OldTargetPath);
                }
            }
            else if (renameCleanup.OldServiceName is not null && !renameCleanup.ShouldCleanUp)
            {
                _logger.LogWarning(
                    "Service app runtime name changed but old service was not cleaned up. AppInstanceId={AppInstanceId}, OldServiceName={OldServiceName}, NewServiceName={NewServiceName}, Reason={Reason}",
                    deployment.AppInstanceId,
                    renameCleanup.OldServiceName,
                    serviceName,
                    renameCleanup.Reason);
            }

            ArtifactDirectoryMirror.MirrorDirectory(
                deployment.SourceLocalPath,
                targetPath,
                settings.ServiceAppDeploymentExcludedEntries,
                cancellationToken);

            await ArtifactConfigurationFileWriter.ApplyAsync(targetPath, configurationFiles, configurationVariables, cancellationToken);

            EnsureWindowsService(deployment, serviceName, targetExecutablePath);

            var postDeployIdentityCheck = await EnsureServiceIdentityAsync(
                settings,
                deployment,
                serviceName,
                serviceIdentity,
                cancellationToken);

            if (settings.StartServiceAfterServiceAppDeployment)
            {
                StartServiceIfStopped(serviceName, settings.ServiceAppStartTimeoutSeconds);
                serviceStopped = false;
                if (stopMarkerWritten)
                {
                    DeploymentRuntimeStopMarker.Delete(targetPath);
                    stopMarkerWritten = false;
                }
            }

            if (!string.IsNullOrWhiteSpace(postDeployIdentityCheck.WarningMessage))
            {
                await _repository.PublishAppDeploymentResultAsync(
                    deployment,
                    WithDeploySetWarning(
                        AddIdentityCheck(
                            AppDeploymentResult.Warning(targetPath, serviceName, postDeployIdentityCheck.WarningMessage),
                            postDeployIdentityCheck)),
                    cancellationToken);
                return;
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                WithDeploySetWarning(
                    AddIdentityCheck(
                        AppDeploymentResult.Succeeded(targetPath, serviceName, applied: true),
                        postDeployIdentityCheck)),
                cancellationToken);

            _logger.LogInformation(
                "Service app deployed. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Version={Version}, TargetPath={TargetPath}, ServiceName={ServiceName}",
                deployment.AppInstanceId,
                deployment.ArtifactId,
                deployment.Version,
                targetPath,
                serviceName);
        }
        catch (Exception ex) when (IsExpectedDeploymentFailure(ex))
        {
            _logger.LogError(
                ex,
                "Service app deployment failed. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Version={Version}",
                deployment.AppInstanceId,
                deployment.ArtifactId,
                deployment.Version);

            if (serviceStopped
                && settings.StartServiceAfterServiceAppDeployment
                && !string.IsNullOrWhiteSpace(serviceName)
                && TryStartService(serviceName, settings.ServiceAppStartTimeoutSeconds, _logger)
                && stopMarkerWritten
                && !string.IsNullOrWhiteSpace(targetPath))
            {
                DeploymentRuntimeStopMarker.Delete(targetPath);
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                WithDeploySetWarning(AppDeploymentResult.Failed(targetPath, serviceName, ex.Message)),
                cancellationToken);
        }
    }

    private async Task<AppDeploymentResult?> EnsureServiceRunningIfDesiredAsync(
        HostAgentSettings settings,
        ServiceAppDeploymentDescriptor deployment,
        string targetPath,
        string serviceName,
        ServiceIdentityCheckResult identityCheck,
        CancellationToken cancellationToken)
    {
        if (!settings.StartServiceAfterServiceAppDeployment)
        {
            // The deployment is configured to leave the service state alone.
            return null;
        }

        // A runtime stop-marker means the service was intentionally stopped for a
        // deployment that has not yet finished (or was interrupted). The recovery
        // path handles those; reconcile must not fight an intentional stop.
        // There is no other per-service "desired=Stopped" signal in this codebase.
        if (DeploymentRuntimeStopMarker.Exists(targetPath))
        {
            _logger.LogDebug(
                "Service app reconcile skipped auto-start because a runtime stop-marker is present. AppInstanceId={AppInstanceId}, ServiceName={ServiceName}, TargetPath={TargetPath}",
                deployment.AppInstanceId,
                serviceName,
                targetPath);
            return null;
        }

        if (_serviceControl.IsServiceRunning(serviceName))
        {
            _consecutiveStartAttemptsByServiceName.TryRemove(serviceName, out _);
            return null;
        }

        var attempts = _consecutiveStartAttemptsByServiceName.AddOrUpdate(serviceName, 1, static (_, count) => count + 1);
        if (attempts > MaxConsecutiveStartAttempts)
        {
            var persistentWarning = $"Service '{serviceName}' was stopped during reconcile and has exceeded the maximum number of restart attempts ({MaxConsecutiveStartAttempts}). Manual intervention required.";
            _logger.LogWarning(
                "Service app reconcile detected a stopped service but will not restart it because the crash-loop threshold has been reached. AppInstanceId={AppInstanceId}, ServiceName={ServiceName}, Attempts={Attempts}",
                deployment.AppInstanceId,
                serviceName,
                attempts);
            return AppDeploymentResult.Warning(targetPath, serviceName, persistentWarning);
        }

        _logger.LogWarning(
            "Service app reconcile detected a stopped service and will attempt to start it. AppInstanceId={AppInstanceId}, ServiceName={ServiceName}, Attempt={Attempt}",
            deployment.AppInstanceId,
            serviceName,
            attempts);

        try
        {
            _serviceControl.StartServiceIfStopped(serviceName, settings.ServiceAppStartTimeoutSeconds);
            return AppDeploymentResult.Warning(
                targetPath,
                serviceName,
                $"Service '{serviceName}' was stopped during reconcile; attempted restart. Verify service health.");
        }
        catch (Exception ex) when (IsExpectedDeploymentFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Service app reconcile failed to restart a stopped service. AppInstanceId={AppInstanceId}, ServiceName={ServiceName}",
                deployment.AppInstanceId,
                serviceName);
            return AppDeploymentResult.Warning(
                targetPath,
                serviceName,
                $"Service '{serviceName}' was stopped during reconcile; attempted restart failed. {ex.Message}");
        }
    }

    private static string ResolveExecutableRelativePath(ServiceAppDeploymentDescriptor deployment)
    {
        if (!Directory.Exists(deployment.SourceLocalPath))
        {
            throw new DirectoryNotFoundException($"Provisioned service app artifact path was not found: '{deployment.SourceLocalPath}'.");
        }

        var executables = Directory.EnumerateFiles(deployment.SourceLocalPath, "*.exe", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetRelativePath(deployment.SourceLocalPath, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (executables.Length == 0)
        {
            throw new InvalidOperationException(
                $"Service app artifact '{deployment.ArtifactId}' contains no executable in its root directory.");
        }

        if (executables.Length == 1)
        {
            return executables[0];
        }

        var installationName = ServiceAppDeploymentNaming.Clean(deployment.InstallationName);
        if (!ServiceAppDeploymentNaming.IsGenericInstallationName(installationName))
        {
            var expected = installationName + ".exe";
            var match = executables.FirstOrDefault(
                path => string.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        throw new InvalidOperationException(
            $"Service app artifact '{deployment.ArtifactId}' contains more than one root executable. Set AppInstances.InstallationName to the Windows service/executable name.");
    }

    private bool IsAlreadyApplied(
        ServiceAppDeploymentDescriptor deployment,
        string targetPath,
        string serviceName,
        string targetExecutablePath)
    {
        return deployment.DeploymentState == HostDeploymentStatuses.Succeeded
            && deployment.DeployedArtifactId == deployment.ArtifactId
            && string.Equals(deployment.DeployedSourceLocalPath, deployment.SourceLocalPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(deployment.DeployedTargetPath, targetPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(deployment.DeployedRuntimeName, serviceName, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(targetPath)
            && File.Exists(targetExecutablePath)
            && _serviceControl.GetServiceState(serviceName) is not null;
    }

    private void CleanUpRenamedService(string oldServiceName, string oldTargetPath, string newTargetPath)
    {
        StopServiceIfRunning(oldServiceName, timeoutSeconds: 30);
        RunScChecked("delete", oldServiceName);

        if (Directory.Exists(oldTargetPath)
            && !string.Equals(oldTargetPath, newTargetPath, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(oldTargetPath, recursive: true);
        }
    }

    private void EnsureWindowsService(
        ServiceAppDeploymentDescriptor deployment,
        string serviceName,
        string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException($"Service executable was not found after deployment: '{executablePath}'.", executablePath);
        }

        var displayName = ResolveServiceDisplayName(deployment, serviceName);
        var description = string.IsNullOrWhiteSpace(deployment.Description)
            ? $"OMP service app instance {deployment.AppInstanceKey}."
            : deployment.Description.Trim();

        _serviceControl.EnsureServiceConfigured(serviceName, executablePath, displayName, description);
    }

    private static string ResolveServiceDisplayName(ServiceAppDeploymentDescriptor deployment, string serviceName)
    {
        var displayName = string.IsNullOrWhiteSpace(deployment.DisplayName)
            ? serviceName
            : deployment.DisplayName.Trim();

        return displayName.StartsWith("OMP", StringComparison.OrdinalIgnoreCase)
            ? displayName
            : $"OMP {displayName}";
    }

    private async Task<ServiceIdentityCheckResult> EnsureServiceIdentityAsync(
        HostAgentSettings settings,
        ServiceAppDeploymentDescriptor deployment,
        string serviceName,
        ServiceAppIdentityResolution desiredIdentity,
        CancellationToken cancellationToken)
    {
        var automationMode = NormalizeAutomationMode(settings.CredentialStore.AutomationMode);
        if (!desiredIdentity.IsConfigured)
        {
            return new ServiceIdentityCheckResult(
                automationMode,
                null,
                GetServiceStartNameIfExists(serviceName),
                HostAppIdentityCheckStatuses.NotApplicable,
                null,
                Applied: false,
                ClearRepairRequest: false);
        }

        var actualIdentity = GetServiceStartNameIfExists(serviceName);
        if (actualIdentity is null)
        {
            return new ServiceIdentityCheckResult(
                automationMode,
                desiredIdentity.UserName,
                null,
                HostAppIdentityCheckStatuses.NotApplicable,
                null,
                Applied: false,
                ClearRepairRequest: false);
        }

        if (AccountsEqual(actualIdentity, desiredIdentity.UserName))
        {
            _logger.LogDebug(
                "Service app identity comparison is compliant. AppInstanceId={AppInstanceId}, ServiceName={ServiceName}, IdentitySource={IdentitySource}, DesiredIdentity={DesiredIdentity}, ActualIdentity={ActualIdentity}, AutomationMode={AutomationMode}",
                deployment.AppInstanceId,
                serviceName,
                desiredIdentity.Source,
                NormalizeAccountForAudit(desiredIdentity.UserName),
                NormalizeAccountForAudit(actualIdentity),
                automationMode);

            return new ServiceIdentityCheckResult(
                automationMode,
                desiredIdentity.UserName,
                actualIdentity,
                HostAppIdentityCheckStatuses.Compliant,
                null,
                Applied: false,
                ClearRepairRequest: true);
        }

        var canApply = string.Equals(automationMode, HostAgentCredentialAutomationModes.Full, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(automationMode, HostAgentCredentialAutomationModes.PortalAdminApproved, StringComparison.OrdinalIgnoreCase)
                && deployment.IdentityRepairRequestedUtc.HasValue);
        _logger.LogInformation(
            "Service app identity mismatch detected. AppInstanceId={AppInstanceId}, ServiceName={ServiceName}, IdentitySource={IdentitySource}, DesiredIdentity={DesiredIdentity}, ActualIdentity={ActualIdentity}, AutomationMode={AutomationMode}, RepairRequested={RepairRequested}, WillApply={WillApply}",
            deployment.AppInstanceId,
            serviceName,
            desiredIdentity.Source,
            NormalizeAccountForAudit(desiredIdentity.UserName),
            NormalizeAccountForAudit(actualIdentity),
            automationMode,
            deployment.IdentityRepairRequestedUtc.HasValue,
            canApply);

        if (canApply)
        {
            if (RequiresPassword(desiredIdentity.UserName) && string.IsNullOrWhiteSpace(desiredIdentity.Password))
            {
                throw new InvalidOperationException(
                    $"Windows service '{serviceName}' must run as '{desiredIdentity.UserName}', but HostAgent has no stored password for that account.");
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("Service app identity repair requires Windows service management.");
            }

            ApplyDesiredServiceIdentity(settings, serviceName, desiredIdentity);
            var updatedIdentity = GetServiceStartName(serviceName);
            if (!AccountsEqual(updatedIdentity, desiredIdentity.UserName))
            {
                throw new InvalidOperationException(
                    $"Windows service '{serviceName}' was configured to run as '{desiredIdentity.UserName}', but Windows still reports '{updatedIdentity}'.");
            }

            _logger.LogInformation(
                "Service app identity repair applied. AppInstanceId={AppInstanceId}, ServiceName={ServiceName}, IdentitySource={IdentitySource}, DesiredIdentity={DesiredIdentity}, ActualIdentity={ActualIdentity}, AutomationMode={AutomationMode}",
                deployment.AppInstanceId,
                serviceName,
                desiredIdentity.Source,
                NormalizeAccountForAudit(desiredIdentity.UserName),
                NormalizeAccountForAudit(updatedIdentity),
                automationMode);

            return new ServiceIdentityCheckResult(
                automationMode,
                desiredIdentity.UserName,
                updatedIdentity,
                HostAppIdentityCheckStatuses.Compliant,
                null,
                Applied: true,
                ClearRepairRequest: true);
        }

        if (string.Equals(automationMode, HostAgentCredentialAutomationModes.PortalAdminApproved, StringComparison.OrdinalIgnoreCase))
        {
            var status = deployment.IdentityRepairRequestedUtc.HasValue
                ? HostAppIdentityCheckStatuses.RepairRequested
                : HostAppIdentityCheckStatuses.WaitingForPortalAdminApproval;
            var message = deployment.IdentityRepairRequestedUtc.HasValue
                ? $"Windows service '{serviceName}' is waiting for HostAgent to apply the PortalAdmin-approved service identity repair."
                : $"Windows service '{serviceName}' runs as '{actualIdentity}', but desired identity is '{desiredIdentity.UserName}'. PortalAdmin approval is required before HostAgent changes the service account.";
            return new ServiceIdentityCheckResult(
                automationMode,
                desiredIdentity.UserName,
                actualIdentity,
                status,
                message,
                Applied: false,
                ClearRepairRequest: false);
        }

        return new ServiceIdentityCheckResult(
            automationMode,
            desiredIdentity.UserName,
            actualIdentity,
            HostAppIdentityCheckStatuses.ManualActionRequired,
            $"Windows service '{serviceName}' runs as '{actualIdentity}', but desired identity is '{desiredIdentity.UserName}'. Credential automation is disabled; manually configure the Windows service logon account and restart the service.",
            Applied: false,
            ClearRepairRequest: false);
    }

    private async Task<ServiceAppIdentityResolution> ResolveServiceAppIdentityAsync(
        HostAgentSettings settings,
        ServiceAppDeploymentDescriptor deployment,
        string serviceName,
        CancellationToken cancellationToken)
    {
        var configuredOverride = ResolveServiceAppIdentityOverrideKeys(deployment, serviceName)
            .Select(key => TryGetServiceAppIdentityOverride(settings, key, out var identity)
                ? identity
                : null)
            .FirstOrDefault(identity => identity is not null && HasConfiguredServiceAppIdentity(identity));
        if (configuredOverride is not null)
        {
            var resolvedOverride = await ResolveStoredServiceAppPasswordAsync(
                configuredOverride,
                "Override",
                cancellationToken);
            LogServiceIdentityResolution(deployment, serviceName, resolvedOverride);
            return resolvedOverride;
        }

        var usesSelfUpgradeFallback =
            (string.IsNullOrWhiteSpace(settings.ServiceAppUserName)
                && !string.IsNullOrWhiteSpace(settings.SelfUpgrade.ServiceAccountName))
            || (string.IsNullOrWhiteSpace(settings.ServiceAppPassword)
                && !string.IsNullOrWhiteSpace(settings.SelfUpgrade.ServiceAccountPassword))
            || (string.IsNullOrWhiteSpace(settings.ServiceAppPasswordCredentialKey)
                && !string.IsNullOrWhiteSpace(settings.SelfUpgrade.ServiceAccountPasswordCredentialKey));
        var defaultIdentity = new HostAgentServiceAppIdentitySettings
        {
            UserName = string.IsNullOrWhiteSpace(settings.ServiceAppUserName)
                ? settings.SelfUpgrade.ServiceAccountName
                : settings.ServiceAppUserName,
            Password = string.IsNullOrWhiteSpace(settings.ServiceAppPassword)
                ? settings.SelfUpgrade.ServiceAccountPassword
                : settings.ServiceAppPassword,
            PasswordCredentialKey = string.IsNullOrWhiteSpace(settings.ServiceAppPasswordCredentialKey)
                ? settings.SelfUpgrade.ServiceAccountPasswordCredentialKey
                : settings.ServiceAppPasswordCredentialKey
        };

        var resolvedDefault = await ResolveStoredServiceAppPasswordAsync(
            defaultIdentity,
            usesSelfUpgradeFallback ? "SelfUpgradeFallback" : "ServiceAppDefault",
            cancellationToken);
        LogServiceIdentityResolution(deployment, serviceName, resolvedDefault);
        return resolvedDefault;
    }

    private async Task<ServiceAppIdentityResolution> ResolveStoredServiceAppPasswordAsync(
        HostAgentServiceAppIdentitySettings identity,
        string source,
        CancellationToken cancellationToken)
    {
        var userName = identity.UserName;
        var password = identity.Password;
        if (!string.IsNullOrWhiteSpace(identity.PasswordCredentialKey))
        {
            var credential = await _credentialStore.TryReadCredentialAsync(identity.PasswordCredentialKey, cancellationToken);
            if (credential is null)
            {
                return string.IsNullOrWhiteSpace(userName)
                    ? ServiceAppIdentityResolution.NotConfigured
                    : new ServiceAppIdentityResolution(userName.Trim(), password, source);
            }

            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = credential.UserName;
            }

            password = credential.Password;
        }

        return string.IsNullOrWhiteSpace(userName)
            ? ServiceAppIdentityResolution.NotConfigured
            : new ServiceAppIdentityResolution(userName.Trim(), password, source);
    }

    private void LogServiceIdentityResolution(
        ServiceAppDeploymentDescriptor deployment,
        string serviceName,
        ServiceAppIdentityResolution resolvedIdentity)
    {
        if (!resolvedIdentity.IsConfigured)
        {
            _logger.LogDebug(
                "Service app identity is not configured. AppInstanceId={AppInstanceId}, ServiceName={ServiceName}, IdentitySource={IdentitySource}",
                deployment.AppInstanceId,
                serviceName,
                resolvedIdentity.Source);
            return;
        }

        _logger.LogInformation(
            "Service app identity resolved. AppInstanceId={AppInstanceId}, ServiceName={ServiceName}, IdentitySource={IdentitySource}, DesiredIdentity={DesiredIdentity}",
            deployment.AppInstanceId,
            serviceName,
            resolvedIdentity.Source,
            NormalizeAccountForAudit(resolvedIdentity.UserName));
    }

    private static bool HasConfiguredServiceAppIdentity(HostAgentServiceAppIdentitySettings identity)
        => !string.IsNullOrWhiteSpace(identity.UserName)
            || !string.IsNullOrWhiteSpace(identity.PasswordCredentialKey);

    private static IEnumerable<string> ResolveServiceAppIdentityOverrideKeys(
        ServiceAppDeploymentDescriptor deployment,
        string serviceName)
    {
        yield return deployment.AppInstanceKey;

        if (!string.IsNullOrWhiteSpace(deployment.InstallationName))
        {
            yield return deployment.InstallationName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(deployment.TargetName))
        {
            yield return deployment.TargetName.Trim();
        }

        yield return serviceName;
    }

    private static bool TryGetServiceAppIdentityOverride(
        HostAgentSettings settings,
        string key,
        out HostAgentServiceAppIdentitySettings identity)
    {
        var overrides = settings.ServiceAppIdentityOverrides;
        if (overrides is not { Count: > 0 } || string.IsNullOrWhiteSpace(key))
        {
            identity = new HostAgentServiceAppIdentitySettings();
            return false;
        }

        if (overrides.TryGetValue(key, out var configuredIdentity) && configuredIdentity is not null)
        {
            identity = configuredIdentity;
            return true;
        }

        var trimmedKey = key.Trim();
        var pair = overrides.FirstOrDefault(pair =>
            string.Equals(pair.Key?.Trim(), trimmedKey, StringComparison.OrdinalIgnoreCase)
            && pair.Value is not null);
        if (pair.Value is not null)
        {
            identity = pair.Value;
            return true;
        }

        identity = new HostAgentServiceAppIdentitySettings();
        return false;
    }

    private static AppDeploymentResult AddIdentityCheck(
        AppDeploymentResult result,
        ServiceIdentityCheckResult identityCheck)
        => result.WithIdentityCheck(
            identityCheck.AutomationMode,
            identityCheck.DesiredIdentity,
            identityCheck.ActualIdentity,
            identityCheck.Status,
            identityCheck.ClearRepairRequest);

    [SupportedOSPlatform("windows")]
    private void ApplyDesiredServiceIdentity(
        HostAgentSettings settings,
        string serviceName,
        ServiceAppIdentityResolution desiredIdentity)
    {
        var actualIdentity = GetServiceStartNameIfExists(serviceName);
        if (actualIdentity is null || AccountsEqual(actualIdentity, desiredIdentity.UserName))
        {
            return;
        }

        var wasRunning = IsServiceRunning(serviceName);
        if (wasRunning)
        {
            StopServiceIfRunning(serviceName, settings.ServiceAppStopTimeoutSeconds);
        }

        var serviceStartName = NormalizeAccountForSc(desiredIdentity.UserName);
        var serviceStartPassword = RequiresPassword(desiredIdentity.UserName)
            ? desiredIdentity.Password
            : null;
        ChangeServiceStartAccount(serviceName, serviceStartName, serviceStartPassword);

        if (wasRunning)
        {
            StartServiceIfStopped(serviceName, settings.ServiceAppStartTimeoutSeconds);
        }
    }

    private bool StopServiceIfRunning(string serviceName, int timeoutSeconds)
        => _serviceControl.StopServiceIfRunning(serviceName, timeoutSeconds);

    private void StartServiceIfStopped(string serviceName, int timeoutSeconds)
        => _serviceControl.StartServiceIfStopped(serviceName, timeoutSeconds);

    private bool IsServiceRunning(string serviceName)
        => _serviceControl.IsServiceRunning(serviceName);

    private bool TryStartService(string serviceName, int timeoutSeconds, ILogger logger)
    {
        // The original deployment failure is the actionable error. Restart
        // recovery is best-effort and should not mask that primary failure.
        try
        {
            StartServiceIfStopped(serviceName, timeoutSeconds);
            return true;
        }
        catch (Exception ex) when (IsExpectedRecoveryStartFailure(ex))
        {
            logger.LogDebug(
                ex,
                "Failed to restart Windows service after deployment failure. ServiceName={ServiceName}",
                serviceName);
            return false;
        }
    }

    private async Task RecoverInterruptedDeploymentsAsync(
        string hostKey,
        HostAgentSettings settings,
        CancellationToken cancellationToken)
    {
        var candidates = await _repository.GetServiceAppDeploymentRecoveryCandidatesAsync(hostKey, cancellationToken);
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
                        "Deleted stale or unreadable interrupted service app deployment marker. AppInstanceId={AppInstanceId}, AppInstanceKey={AppInstanceKey}, TargetPath={TargetPath}",
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
                        "Service app deployment recovery skipped because the interrupted deployment marker has no runtime name. AppInstanceId={AppInstanceId}, AppInstanceKey={AppInstanceKey}, TargetPath={TargetPath}",
                        candidate.AppInstanceId,
                        candidate.AppInstanceKey,
                        candidate.TargetPath);
                    continue;
                }

                StartServiceIfStopped(runtimeName, settings.ServiceAppStartTimeoutSeconds);
                DeploymentRuntimeStopMarker.Delete(candidate.TargetPath);
                _logger.LogInformation(
                    "Recovered Windows service after an interrupted service app deployment. AppInstanceId={AppInstanceId}, AppInstanceKey={AppInstanceKey}, ServiceName={ServiceName}, TargetPath={TargetPath}",
                    candidate.AppInstanceId,
                    candidate.AppInstanceKey,
                    runtimeName,
                    candidate.TargetPath);
            }
            catch (Exception ex) when (IsExpectedRecoveryStartFailure(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Failed to recover Windows service after an interrupted service app deployment. AppInstanceId={AppInstanceId}, AppInstanceKey={AppInstanceKey}, RuntimeName={RuntimeName}, TargetPath={TargetPath}",
                    candidate.AppInstanceId,
                    candidate.AppInstanceKey,
                    candidate.RuntimeName,
                    candidate.TargetPath);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ChangeServiceStartAccount(
        string serviceName,
        string startName,
        string? startPassword)
    {
        using var service = GetWindowsServiceManagementObject(serviceName);
        using var parameters = service.GetMethodParameters("Change");
        parameters["StartName"] = startName;
        parameters["StartPassword"] = startPassword;

        using var result = service.InvokeMethod("Change", parameters, null);
        var returnValue = Convert.ToUInt32(result?["ReturnValue"] ?? 0, CultureInfo.InvariantCulture);
        if (returnValue != 0)
        {
            throw new InvalidOperationException(
                $"Win32_Service.Change failed with return value {returnValue} for Windows service '{serviceName}'.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static ManagementObject GetWindowsServiceManagementObject(string serviceName)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT * FROM Win32_Service WHERE Name = " + QuoteWqlString(serviceName));

        foreach (ManagementObject service in searcher.Get())
        {
            return service;
        }

        throw new InvalidOperationException($"Windows service '{serviceName}' was not found.");
    }

    private static string QuoteWqlString(string value)
        => "'" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static string? GetServiceStartNameIfExists(string serviceName)
    {
        var result = RunSc("qc", serviceName);
        if (result.ExitCode != 0)
        {
            if (result.IsServiceNotFound())
            {
                return null;
            }

            throw new InvalidOperationException(CreateScFailureMessage(
                result.ExitCode,
                result.Output,
                result.Error,
                "query configuration for",
                serviceName));
        }

        return ParseServiceStartName(serviceName, result.Output);
    }

    private static string GetServiceStartName(string serviceName)
        => GetServiceStartNameIfExists(serviceName)
            ?? throw new InvalidOperationException($"Windows service '{serviceName}' was not found.");

    private static string ParseServiceStartName(string serviceName, string output)
    {
        foreach (var line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var nameIndex = line.IndexOf("SERVICE_START_NAME", StringComparison.OrdinalIgnoreCase);
            if (nameIndex < 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':', nameIndex);
            if (separatorIndex < 0)
            {
                continue;
            }

            var startName = line[(separatorIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(startName))
            {
                return startName;
            }
        }

        throw new InvalidOperationException(
            $"Could not determine logon account for Windows service '{serviceName}'. sc.exe did not return SERVICE_START_NAME.");
    }

    private static ScCommandResult RunSc(params string[] arguments)
    {
        var result = HostAgentProcessRunner.Run(GetScPath(), arguments);
        return new ScCommandResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    private static void RunScChecked(params string[] arguments)
    {
        var result = RunSc(arguments);
        if (result.ExitCode != 0)
        {
            var operation = arguments.Length > 0 ? arguments[0] : "unknown";
            var serviceName = arguments.Length > 1 ? arguments[1] : null;
            throw new InvalidOperationException(CreateScFailureMessage(
                result.ExitCode,
                result.Output,
                result.Error,
                operation,
                serviceName));
        }
    }

    private static string CreateScFailureMessage(
        int exitCode,
        string output,
        string error,
        string operation,
        string? serviceName)
    {
        var message = string.IsNullOrWhiteSpace(error) ? output : error;
        var trimmed = message.Trim();
        var result = $"sc.exe failed with exit code {exitCode}";
        if (!string.IsNullOrWhiteSpace(operation))
        {
            result += $" while trying to {operation} Windows service";
        }

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            result += $" '{serviceName}'";
        }

        result += $": {trimmed}";

        if (exitCode == ScAccessDeniedExitCode
            || trimmed.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("OpenSCManager", StringComparison.OrdinalIgnoreCase))
        {
            result += " HostAgent cannot safely deploy service apps without Windows service-control rights. Run the HostAgent service as an account with permission to query, stop, configure, and start the target service before retrying.";
        }

        return result;
    }

    private static string GetScPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var scPath = Path.Join(windowsDirectory, "System32", "sc.exe");
        if (!File.Exists(scPath))
        {
            throw new FileNotFoundException($"Windows sc.exe was not found: '{scPath}'.", scPath);
        }

        return scPath;
    }

    private static bool AccountsEqual(string actual, string desired)
    {
        var desiredCandidates = NormalizeAccountCandidates(desired);
        return NormalizeAccountCandidates(actual).Any(desiredCandidates.Contains);
    }

    private static string NormalizeAccountForAudit(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : NormalizeAccountForComparison(value);

    private static string NormalizeAccountForComparison(string value)
    {
        var normalized = value.Trim();
        if (normalized.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(".\\LocalSystem", StringComparison.OrdinalIgnoreCase))
        {
            return "LocalSystem";
        }

        if (normalized.Equals("LocalService", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\LocalService", StringComparison.OrdinalIgnoreCase))
        {
            return "NT AUTHORITY\\LocalService";
        }

        if (normalized.Equals("NetworkService", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\NetworkService", StringComparison.OrdinalIgnoreCase))
        {
            return "NT AUTHORITY\\NetworkService";
        }

        if (normalized.StartsWith(".\\", StringComparison.Ordinal))
        {
            return Environment.MachineName + "\\" + normalized[2..];
        }

        if (!normalized.Contains('\\', StringComparison.Ordinal)
            && !normalized.Contains('@', StringComparison.Ordinal))
        {
            return Environment.MachineName + "\\" + normalized;
        }

        return normalized;
    }

    private static HashSet<string> NormalizeAccountCandidates(string value)
    {
        var normalized = NormalizeAccountForComparison(value);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            normalized
        };

        if (TrySplitDomainAccount(normalized, out var domain, out var accountName))
        {
            AddDomainAccountCandidates(candidates, domain, accountName);
        }
        else if (TrySplitUserPrincipalName(normalized, out accountName, out domain))
        {
            AddDomainAccountCandidates(candidates, domain, accountName);
        }

        return candidates;
    }

    private static void AddDomainAccountCandidates(HashSet<string> candidates, string domain, string accountName)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        var trimmedDomain = domain.Trim();
        var trimmedAccount = accountName.Trim();
        candidates.Add(trimmedDomain + "\\" + trimmedAccount);
        candidates.Add(trimmedAccount + "@" + trimmedDomain);

        var shortDomain = GetShortDomainName(trimmedDomain);
        if (!shortDomain.Equals(trimmedDomain, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(shortDomain + "\\" + trimmedAccount);
            candidates.Add(trimmedAccount + "@" + shortDomain);
        }
    }

    private static bool TrySplitDomainAccount(string value, out string domain, out string accountName)
    {
        var separatorIndex = value.IndexOf('\\', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex >= value.Length - 1)
        {
            domain = string.Empty;
            accountName = string.Empty;
            return false;
        }

        domain = value[..separatorIndex];
        accountName = value[(separatorIndex + 1)..];
        return true;
    }

    private static bool TrySplitUserPrincipalName(string value, out string accountName, out string domain)
    {
        var separatorIndex = value.IndexOf('@', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex >= value.Length - 1)
        {
            accountName = string.Empty;
            domain = string.Empty;
            return false;
        }

        accountName = value[..separatorIndex];
        domain = value[(separatorIndex + 1)..];
        return true;
    }

    private static string GetShortDomainName(string domain)
    {
        var dotIndex = domain.IndexOf('.', StringComparison.Ordinal);
        return dotIndex > 0 ? domain[..dotIndex] : domain;
    }

    private static string NormalizeAccountForSc(string value)
    {
        var normalized = NormalizeAccountForComparison(value);
        return normalized.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase)
            ? "LocalSystem"
            : normalized;
    }

    private static bool RequiresPassword(string userName)
    {
        var normalized = NormalizeAccountForComparison(userName);
        return !normalized.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("NT AUTHORITY\\LocalService", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("NT AUTHORITY\\NetworkService", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAutomationMode(string automationMode)
    {
        if (string.Equals(automationMode?.Trim(), HostAgentCredentialAutomationModes.Full, StringComparison.OrdinalIgnoreCase))
        {
            return HostAgentCredentialAutomationModes.Full;
        }

        if (string.Equals(automationMode?.Trim(), HostAgentCredentialAutomationModes.PortalAdminApproved, StringComparison.OrdinalIgnoreCase))
        {
            return HostAgentCredentialAutomationModes.PortalAdminApproved;
        }

        return HostAgentCredentialAutomationModes.Disabled;
    }

    private static bool IsExpectedDeploymentFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException
            or System.ComponentModel.Win32Exception;

    private static bool IsExpectedRecoveryStartFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException
            or System.ComponentModel.Win32Exception;

    private sealed record ServiceAppIdentityResolution(string UserName, string Password, string Source)
    {
        public static ServiceAppIdentityResolution NotConfigured { get; } = new(string.Empty, string.Empty, "NotConfigured");

        public bool IsConfigured => !string.IsNullOrWhiteSpace(UserName);
    }

    private sealed record ServiceIdentityCheckResult(
        string AutomationMode,
        string? DesiredIdentity,
        string? ActualIdentity,
        string Status,
        string? WarningMessage,
        bool Applied,
        bool ClearRepairRequest);
}
