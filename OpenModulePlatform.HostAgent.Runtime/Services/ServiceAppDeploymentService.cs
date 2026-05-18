using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class ServiceAppDeploymentService
{
    private const int ScAccessDeniedExitCode = 5;
    private const int ScServiceNotFoundExitCode = 1060;

    private static readonly char[] InvalidServiceNameCharacters = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ILogger<ServiceAppDeploymentService> _logger;

    public ServiceAppDeploymentService(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        ILogger<ServiceAppDeploymentService> logger)
    {
        _settings = settings;
        _repository = repository;
        _logger = logger;
    }

    public async Task DeployDesiredServiceAppsAsync(string hostKey, CancellationToken cancellationToken)
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

        var deployments = await _repository.GetDesiredServiceAppDeploymentsAsync(
            hostKey,
            settings.MaxArtifactsPerCycle,
            cancellationToken);

        _logger.LogInformation(
            "Resolved desired service app deployments. HostKey={HostKey}, Count={Count}",
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
        ServiceAppDeploymentDescriptor deployment,
        CancellationToken cancellationToken)
    {
        string? targetPath = null;
        string? serviceName = null;
        var serviceStopped = false;

        try
        {
            var executableRelativePath = ResolveExecutableRelativePath(deployment);
            serviceName = ResolveServiceName(deployment, executableRelativePath);
            targetPath = ResolveTargetPath(settings, deployment, serviceName);
            var targetExecutablePath = DeploymentPath.CombineUnderRoot(
                targetPath,
                executableRelativePath,
                $"Service app instance '{deployment.AppInstanceKey}' executable path");

            var configurationFiles = await _repository.GetArtifactConfigurationFilesAsync(
                deployment.ArtifactId,
                cancellationToken);

            if (IsAlreadyApplied(deployment, targetPath, serviceName, targetExecutablePath))
            {
                if (ArtifactConfigurationFileWriter.AreApplied(targetPath, configurationFiles))
                {
                    await _repository.PublishAppDeploymentResultAsync(
                        deployment,
                        AppDeploymentResult.Succeeded(targetPath, serviceName, applied: false),
                        cancellationToken);
                    return;
                }
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                AppDeploymentResult.Running(targetPath, serviceName),
                cancellationToken);

            if (settings.StopServiceForServiceAppDeployment)
            {
                serviceStopped = StopServiceIfRunning(serviceName, settings.ServiceAppStopTimeoutSeconds);
            }

            ArtifactDirectoryMirror.MirrorDirectory(
                deployment.SourceLocalPath,
                targetPath,
                settings.ServiceAppDeploymentExcludedEntries,
                cancellationToken);

            await ArtifactConfigurationFileWriter.ApplyAsync(targetPath, configurationFiles, cancellationToken);

            EnsureWindowsService(deployment, serviceName, targetExecutablePath);

            if (settings.StartServiceAfterServiceAppDeployment)
            {
                StartServiceIfStopped(serviceName, settings.ServiceAppStartTimeoutSeconds);
                serviceStopped = false;
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                AppDeploymentResult.Succeeded(targetPath, serviceName, applied: true),
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

            if (serviceStopped && settings.StartServiceAfterServiceAppDeployment && !string.IsNullOrWhiteSpace(serviceName))
            {
                TryStartService(serviceName, settings.ServiceAppStartTimeoutSeconds, _logger);
            }

            await _repository.PublishAppDeploymentResultAsync(
                deployment,
                AppDeploymentResult.Failed(targetPath, serviceName, ex.Message),
                cancellationToken);
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

        var installationName = Clean(deployment.InstallationName);
        if (!IsGenericInstallationName(installationName))
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

    private static string ResolveServiceName(ServiceAppDeploymentDescriptor deployment, string executableRelativePath)
    {
        var configuredName = Clean(deployment.InstallationName);
        var serviceName = IsGenericInstallationName(configuredName)
            ? Path.GetFileNameWithoutExtension(executableRelativePath)
            : configuredName!;

        ValidateServiceName(serviceName, deployment.AppInstanceKey);
        return serviceName;
    }

    private static string ResolveTargetPath(
        HostAgentSettings settings,
        ServiceAppDeploymentDescriptor deployment,
        string serviceName)
    {
        var installPath = Clean(deployment.InstallPath);
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            if (Path.IsPathRooted(installPath))
            {
                return Path.GetFullPath(installPath);
            }

            return DeploymentPath.CombineUnderRoot(
                settings.ServicesRoot.Trim(),
                installPath,
                $"Service app instance '{deployment.AppInstanceKey}' InstallPath");
        }

        var folderName = SanitizeFolderName(serviceName);
        return DeploymentPath.CombineUnderRoot(
            settings.ServicesRoot.Trim(),
            folderName,
            $"Service app instance '{deployment.AppInstanceKey}' folder name");
    }

    private static bool IsAlreadyApplied(
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
            && GetServiceState(serviceName) is not null;
    }

    private static void EnsureWindowsService(
        ServiceAppDeploymentDescriptor deployment,
        string serviceName,
        string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException($"Service executable was not found after deployment: '{executablePath}'.", executablePath);
        }

        var binaryPath = Quote(executablePath);
        var displayName = string.IsNullOrWhiteSpace(deployment.DisplayName) ? serviceName : deployment.DisplayName.Trim();
        var description = string.IsNullOrWhiteSpace(deployment.Description)
            ? $"OMP service app instance {deployment.AppInstanceKey}."
            : deployment.Description.Trim();

        if (GetServiceState(serviceName) is null)
        {
            RunScChecked(
                "create",
                serviceName,
                "binPath=",
                binaryPath,
                "start=",
                "auto",
                "DisplayName=",
                displayName);
        }
        else
        {
            RunScChecked(
                "config",
                serviceName,
                "binPath=",
                binaryPath,
                "start=",
                "auto",
                "DisplayName=",
                displayName);
        }

        RunScChecked("description", serviceName, description);
    }

    private static bool StopServiceIfRunning(string serviceName, int timeoutSeconds)
    {
        var state = GetServiceState(serviceName);
        if (state is null)
        {
            return false;
        }

        if (string.Equals(state, "STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        RunScChecked("stop", serviceName);
        WaitForServiceState(serviceName, "STOPPED", timeoutSeconds);
        return true;
    }

    private static void StartServiceIfStopped(string serviceName, int timeoutSeconds)
    {
        var state = GetServiceState(serviceName);
        if (state is null)
        {
            throw new InvalidOperationException($"Windows service '{serviceName}' was not found.");
        }

        if (string.Equals(state, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RunScChecked("start", serviceName);
        WaitForServiceState(serviceName, "RUNNING", timeoutSeconds);
    }

    private static void TryStartService(string serviceName, int timeoutSeconds, ILogger logger)
    {
        // The original deployment failure is the actionable error. Restart
        // recovery is best-effort and should not mask that primary failure.
        try
        {
            StartServiceIfStopped(serviceName, timeoutSeconds);
        }
        catch (Exception ex) when (IsExpectedRecoveryStartFailure(ex))
        {
            logger.LogDebug(
                ex,
                "Failed to restart Windows service after deployment failure. ServiceName={ServiceName}",
                serviceName);
        }
    }

    private static void WaitForServiceState(string serviceName, string desiredState, int timeoutSeconds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = GetServiceState(serviceName);
            if (string.Equals(state, desiredState, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"Windows service '{serviceName}' did not reach state '{desiredState}' within {timeoutSeconds} seconds.");
    }

    private static string? GetServiceState(string serviceName)
    {
        var result = RunSc("query", serviceName);
        if (result.ExitCode != 0)
        {
            if (IsServiceNotFound(result))
            {
                return null;
            }

            throw new InvalidOperationException(CreateScFailureMessage(
                result.ExitCode,
                result.Output,
                result.Error,
                "query",
                serviceName));
        }

        foreach (var line in result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var stateIndex = line.IndexOf("STATE", StringComparison.OrdinalIgnoreCase);
            if (stateIndex < 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':', stateIndex);
            if (separatorIndex < 0)
            {
                continue;
            }

            var stateText = line[(separatorIndex + 1)..].Trim();
            var parts = stateText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0)
            {
                return parts[^1];
            }
        }

        return null;
    }

    private static ScResult RunSc(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GetScPath(),
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
            ?? throw new InvalidOperationException("Failed to start sc.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ScResult(process.ExitCode, output, error);
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

    private static bool IsServiceNotFound(ScResult result)
    {
        var text = result.CombinedOutput;
        return result.ExitCode == ScServiceNotFoundExitCode
            || text.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase)
            || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
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

    private static void ValidateServiceName(string serviceName, string appInstanceKey)
    {
        if (string.IsNullOrWhiteSpace(serviceName)
            || serviceName.IndexOfAny(InvalidServiceNameCharacters) >= 0
            || serviceName.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"App instance '{appInstanceKey}' resolved an invalid Windows service name.");
        }
    }

    private static string SanitizeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "service-app" : sanitized;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsGenericInstallationName(string? value)
        => string.IsNullOrWhiteSpace(value)
            || value.Equals("default", StringComparison.OrdinalIgnoreCase)
            || value.Equals("service", StringComparison.OrdinalIgnoreCase)
            || value.Equals("serviceapp", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string value)
        => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

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

    private sealed record ScResult(int ExitCode, string Output, string Error)
    {
        public string CombinedOutput => string.Concat(Output, "\n", Error);
    }
}
