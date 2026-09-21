using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Shared appcmd.exe helpers for the services that manage IIS application pools.
/// One definition keeps app-pool naming and state parsing identical between deployment
/// and health monitoring; the copies this replaced had already started to drift in
/// their failure messages.
/// </summary>
internal static class IisAppCmd
{
    private const int MaxAppPoolNameLength = 80;

    /// <summary>
    /// Returns the appcmd.exe path, or null when IIS is not installed on this host.
    /// </summary>
    public static string? TryGetPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var appCmdPath = Path.Join(windowsDirectory, "System32", "inetsrv", "appcmd.exe");
        return File.Exists(appCmdPath) ? appCmdPath : null;
    }

    /// <summary>
    /// Returns the appcmd.exe path and throws when IIS is not installed on this host.
    /// </summary>
    public static string GetPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var appCmdPath = Path.Join(windowsDirectory, "System32", "inetsrv", "appcmd.exe");
        if (!File.Exists(appCmdPath))
        {
            throw new FileNotFoundException($"IIS appcmd.exe was not found: '{appCmdPath}'.", appCmdPath);
        }

        return appCmdPath;
    }

    /// <summary>
    /// Builds the IIS application pool name for <paramref name="value"/> under the configured
    /// prefix, normalizing characters IIS rejects and capping the length.
    /// </summary>
    public static string BuildAppPoolName(HostAgentSettings settings, string value)
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
        return name.Length <= MaxAppPoolNameLength ? name : name[..MaxAppPoolNameLength].TrimEnd('_', '.', '-');
    }

    /// <summary>
    /// Returns the app pool state as appcmd reports it ("Started", "Stopped", ...), or null
    /// when the listing carries no state.
    /// </summary>
    public static string? GetAppPoolState(string appPoolName)
    {
        var output = Run("list", "apppool", $"/name:{appPoolName}");
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

    /// <summary>
    /// Runs appcmd.exe and returns its stdout lines; throws on a non-zero exit code.
    /// </summary>
    public static string[] Run(params string[] arguments)
    {
        var result = RunRaw(arguments, throwOnFailure: true);
        return result.StdOut
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static HostAgentProcessResult RunRaw(IReadOnlyList<string> arguments, bool throwOnFailure)
    {
        var result = HostAgentProcessRunner.Run(GetPath(), arguments);
        if (throwOnFailure && result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            throw new InvalidOperationException(CreateFailureMessage(result.ExitCode, message));
        }

        return result;
    }

    private static string CreateFailureMessage(int exitCode, string message)
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
}
