using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class WebAppDeploymentService
{
    private const string IisSslBindingAppId = "{6E9C7F30-6D4E-4D8A-9D1E-9F086C35B508}";

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly HostAgentCredentialStoreService _credentialStore;
    private readonly ILogger<WebAppDeploymentService> _logger;

    public WebAppDeploymentService(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        HostAgentCredentialStoreService credentialStore,
        ILogger<WebAppDeploymentService> logger)
    {
        _settings = settings;
        _repository = repository;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public async Task DeployDesiredWebAppsAsync(string hostKey, CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.DeployWebApps)
        {
            return;
        }

        settings.Validate();

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Web app deployment requires Windows and IIS appcmd.exe.");
        }

        var deployments = await _repository.GetDesiredWebAppDeploymentsAsync(
            hostKey,
            settings.MaxArtifactsPerCycle,
            cancellationToken);

        _logger.LogInformation(
            "Resolved desired web app deployments. HostKey={HostKey}, Count={Count}",
            hostKey,
            deployments.Count);

        foreach (var deployment in deployments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeployAsync(settings, deployment, cancellationToken);
        }
    }

    private async Task DeployAsync(
        HostAgentSettings settings,
        WebAppDeploymentDescriptor deployment,
        CancellationToken cancellationToken)
    {
        string? targetPath = null;
        string? runtimeName = null;
        var appPoolStopped = false;

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

            if (IsAlreadyApplied(deployment, targetPath, runtimeName)
                && ArtifactConfigurationFileWriter.AreApplied(targetPath, configurationFiles, configurationVariables))
            {
                await _repository.PublishAppDeploymentResultAsync(
                    deployment,
                    AppDeploymentResult.Succeeded(targetPath, runtimeName, applied: false),
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
                    AppDeploymentResult.Warning(targetPath, runtimeName, message),
                    cancellationToken);
                return;
            }

            if (settings.EnsureIisSite)
            {
                await EnsureIisApplicationAsync(settings, deployment, targetPath, iisAppName, runtimeName, cancellationToken);
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                AppDeploymentResult.Running(targetPath, runtimeName),
                cancellationToken);

            if (UseAppCmdAppPoolControl(settings)
                && settings.StopIisAppPoolForWebAppDeployment
                && !string.IsNullOrWhiteSpace(runtimeName))
            {
                appPoolStopped = StopAppPoolIfRunning(runtimeName, settings.IisAppPoolStopTimeoutSeconds);
            }

            await MirrorWebAppAsync(settings, deployment, targetPath, configurationFiles, configurationVariables, cancellationToken);

            if (UseAppCmdAppPoolControl(settings)
                && settings.StartIisAppPoolAfterWebAppDeployment
                && !string.IsNullOrWhiteSpace(runtimeName))
            {
                StartAppPoolIfStopped(runtimeName);
                appPoolStopped = false;
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                AppDeploymentResult.Succeeded(targetPath, runtimeName, applied: true),
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
                && !string.IsNullOrWhiteSpace(runtimeName))
            {
                TryStartAppPool(runtimeName, _logger);
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                AppDeploymentResult.Failed(targetPath, runtimeName, ex.Message),
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
            EnsureAppPool(
                settings,
                appPoolName,
                await ResolveIisAppPoolIdentityAsync(settings, deployment, appPoolName, cancellationToken));
            EnsureIisSite(settings, targetPath, appPoolName);
            return;
        }

        var rootAppPoolName = ResolvePortalAppPoolName(settings);
        EnsureAppPool(
            settings,
            rootAppPoolName,
            await ResolveDefaultIisAppPoolIdentityAsync(settings, cancellationToken));
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
        RunAppCmd(
            "set",
            "config",
            location,
            "/section:system.webServer/security/authentication/anonymousAuthentication",
            $"/enabled:{FormatIisBoolean(anonymousEnabled)}",
            "/userName:",
            "/password:",
            "/commit:apphost");

        RunAppCmd(
            "set",
            "config",
            location,
            "/section:system.webServer/security/authentication/windowsAuthentication",
            $"/enabled:{FormatIisBoolean(windowsEnabled)}",
            "/commit:apphost");
    }

    private static bool IsOmpAuthenticationAppPath(string appPath)
        => string.Equals(
            appPath.Trim().Trim('/', '\\').Replace('\\', '/'),
            "auth",
            StringComparison.OrdinalIgnoreCase);

    private static string FormatIisBoolean(bool value)
        => value ? "true" : "false";

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
            var arguments = new List<string>
            {
                "set",
                "apppool",
                $"/apppool.name:{appPoolName}",
                "/processModel.identityType:SpecificUser",
                $"/processModel.userName:{identity.UserName.Trim()}"
            };

            if (!string.IsNullOrWhiteSpace(identity.Password))
            {
                arguments.Add($"/processModel.password:{identity.Password}");
            }

            RunAppCmd([.. arguments]);
        }
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
        return name.Length <= 80 ? name : name[..80].TrimEnd('_', '.', '-');
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
        // for VGR-style TLS termination where the public certificate lives on a
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
        var serialNumber = NormalizeCertificateHex(settings.IisBindingCertificateSerialNumber);

        using var store = new X509Store(storeName, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificate = store.Certificates.OfType<X509Certificate2>().FirstOrDefault(certificate =>
            string.Equals(
                NormalizeCertificateHex(certificate.SerialNumber),
                serialNumber,
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

    private static bool StopAppPoolIfRunning(string appPoolName, int timeoutSeconds)
    {
        var state = GetAppPoolState(appPoolName);
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

            Thread.Sleep(250);
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

    private static void TryStartAppPool(string appPoolName, ILogger logger)
    {
        // The original deployment failure is the actionable error. Restart
        // recovery is best-effort and should not mask that primary failure.
        try
        {
            StartAppPoolIfStopped(appPoolName);
        }
        catch (Exception ex) when (IsExpectedRecoveryStartFailure(ex))
        {
            logger.LogDebug(
                ex,
                "Failed to restart IIS app pool after deployment failure. AppPoolName={AppPoolName}",
                appPoolName);
        }
    }

    private static bool IisObjectExists(params string[] arguments)
        => RunAppCmdRaw(arguments, throwOnFailure: false).ExitCode == 0;

    private static bool IisApplicationExists(string iisAppName)
        => RunAppCmd("list", "app")
            .Any(line => line.Contains(
                $"\"{iisAppName}\"",
                StringComparison.OrdinalIgnoreCase));

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
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();

        return new ProcessResult(process.ExitCode, output, error);
    }

    private static string CreateAppCmdFailureMessage(int exitCode, string message)
    {
        var trimmed = message.Trim();
        var result = $"appcmd.exe failed with exit code {exitCode}: {trimmed}";
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
