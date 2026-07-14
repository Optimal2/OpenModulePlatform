using System.Runtime.Versioning;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Default Windows service-control implementation that shells out to sc.exe.
/// This is intentionally stateless; all lifecycle state lives in the Windows
/// Service Control Manager.
/// </summary>
public sealed class WindowsServiceControl : IWindowsServiceControl
{
    private const int ScAccessDeniedExitCode = 5;

    public static WindowsServiceControl Instance { get; } = new();

    public string? GetServiceState(string serviceName)
    {
        var result = RunSc("query", serviceName);
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

    public bool IsServiceRunning(string serviceName)
        => string.Equals(GetServiceState(serviceName), "RUNNING", StringComparison.OrdinalIgnoreCase);

    public void StartServiceIfStopped(string serviceName, int timeoutSeconds)
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

    public bool StopServiceIfRunning(string serviceName, int timeoutSeconds)
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

    public void EnsureServiceConfigured(
        string serviceName,
        string executablePath,
        string displayName,
        string description)
    {
        var binaryPath = Quote(executablePath);

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

    public void DeleteService(string serviceName)
    {
        if (GetServiceState(serviceName) is null)
        {
            return;
        }

        StopServiceIfRunning(serviceName, timeoutSeconds: 30);

        var result = RunSc("delete", serviceName);
        if (result.ExitCode == 0 || result.IsServiceNotFound() || result.IsServiceMarkedForDeletion())
        {
            return;
        }

        throw new InvalidOperationException(CreateScFailureMessage(
            result.ExitCode,
            result.Output,
            result.Error,
            "delete",
            serviceName));
    }

    private void WaitForServiceState(string serviceName, string desiredState, int timeoutSeconds)
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

    private static string Quote(string value)
        => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '\"';
}
