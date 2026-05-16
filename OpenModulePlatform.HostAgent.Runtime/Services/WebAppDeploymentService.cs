using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class WebAppDeploymentService
{
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ILogger<WebAppDeploymentService> _logger;

    public WebAppDeploymentService(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        ILogger<WebAppDeploymentService> logger)
    {
        _settings = settings;
        _repository = repository;
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
        string? appPoolName = null;
        var appPoolStopped = false;

        try
        {
            targetPath = ResolveTargetPath(settings, deployment);
            var iisAppName = ResolveIisAppName(settings, deployment);
            appPoolName = GetIisAppPoolName(iisAppName);

            if (IsAlreadyApplied(deployment, targetPath, appPoolName))
            {
                await _repository.PublishAppDeploymentResultAsync(
                    deployment,
                    AppDeploymentResult.Succeeded(targetPath, appPoolName, applied: false),
                    cancellationToken);
                return;
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                AppDeploymentResult.Running(targetPath, appPoolName),
                cancellationToken);

            if (settings.StopIisAppPoolForWebAppDeployment && !string.IsNullOrWhiteSpace(appPoolName))
            {
                appPoolStopped = StopAppPoolIfRunning(appPoolName, settings.IisAppPoolStopTimeoutSeconds);
            }

            ArtifactDirectoryMirror.MirrorDirectory(
                deployment.SourceLocalPath,
                targetPath,
                settings.WebAppDeploymentExcludedEntries,
                cancellationToken);

            if (settings.StartIisAppPoolAfterWebAppDeployment && !string.IsNullOrWhiteSpace(appPoolName))
            {
                StartAppPoolIfStopped(appPoolName);
                appPoolStopped = false;
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                AppDeploymentResult.Succeeded(targetPath, appPoolName, applied: true),
                cancellationToken);

            _logger.LogInformation(
                "Web app deployed. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Version={Version}, TargetPath={TargetPath}, AppPoolName={AppPoolName}",
                deployment.AppInstanceId,
                deployment.ArtifactId,
                deployment.Version,
                targetPath,
                appPoolName);
        }
        catch (Exception ex) when (IsExpectedDeploymentFailure(ex))
        {
            _logger.LogError(
                ex,
                "Web app deployment failed. AppInstanceId={AppInstanceId}, ArtifactId={ArtifactId}, Version={Version}",
                deployment.AppInstanceId,
                deployment.ArtifactId,
                deployment.Version);

            if (appPoolStopped && settings.StartIisAppPoolAfterWebAppDeployment && !string.IsNullOrWhiteSpace(appPoolName))
            {
                TryStartAppPool(appPoolName);
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                AppDeploymentResult.Failed(targetPath, appPoolName, ex.Message),
                cancellationToken);
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
        var targetPath = Path.GetFullPath(Path.Combine(webAppsRoot, normalized));
        if (!IsUnderRoot(webAppsRoot, targetPath))
        {
            throw new InvalidOperationException(
                $"App instance '{deployment.AppInstanceKey}' resolved outside HostAgent:WebAppsRoot.");
        }

        return targetPath;
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

    private static string GetAppCmdPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var appCmdPath = Path.Combine(windowsDirectory, "System32", "inetsrv", "appcmd.exe");
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

    private static void TryStartAppPool(string appPoolName)
    {
        // The original deployment failure is the actionable error. Restart
        // recovery is best-effort and is logged by IIS/EventLog if it fails.
        try
        {
            StartAppPoolIfStopped(appPoolName);
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (TimeoutException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string[] RunAppCmd(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GetAppCmdPath(),
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
            ?? throw new InvalidOperationException("Failed to start IIS appcmd.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"appcmd.exe failed with exit code {process.ExitCode}: {message.Trim()}");
        }

        return output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsExpectedDeploymentFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException;

    private static bool IsUnderRoot(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
