namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Shared sc.exe query helpers for the services that inventory Windows services
/// (job processor, self-upgrade, service control). One definition keeps the state and
/// BINARY_PATH_NAME parsing identical across callers.
/// </summary>
internal static class WindowsServiceQuery
{
    public static ScCommandResult Run(params string[] arguments)
    {
        var result = HostAgentProcessRunner.Run(
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "sc.exe"),
            arguments);
        return new ScCommandResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    /// <summary>
    /// Returns the service state token ("RUNNING", "STOPPED", ...), null when the service
    /// does not exist, and throws on any other sc.exe failure.
    /// </summary>
    public static string? GetServiceState(string serviceName)
    {
        var result = Run("query", serviceName);
        if (result.ExitCode != 0)
        {
            return result.IsServiceNotFound() ? null : throw new InvalidOperationException(result.CombinedOutput.Trim());
        }

        return ParseServiceState(result.Output);
    }

    public static string? ParseServiceState(string scQueryOutput)
    {
        foreach (var line in scQueryOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
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

            var parts = line[(separatorIndex + 1)..].Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0)
            {
                return parts[^1];
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the executable path from the service's BINARY_PATH_NAME, or null when the
    /// service cannot be queried or the path cannot be parsed.
    /// </summary>
    public static string? TryGetServiceExecutablePath(string serviceName)
    {
        var result = Run("qc", serviceName);
        return result.ExitCode != 0 ? null : ParseServiceExecutablePath(result.Output);
    }

    public static string? ParseServiceExecutablePath(string scQcOutput)
    {
        foreach (var line in scQcOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var binaryPathIndex = line.IndexOf("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase);
            if (binaryPathIndex < 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':', binaryPathIndex);
            if (separatorIndex < 0)
            {
                continue;
            }

            return TryExtractExecutablePath(line[(separatorIndex + 1)..].Trim());
        }

        return null;
    }

    public static string? TryExtractExecutablePath(string binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return null;
        }

        var trimmed = binaryPath.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }

        // An unquoted path ends its executable at the first ".exe" that is followed by
        // whitespace or the end of the value, so a directory segment such as "Tools.exe.d"
        // earlier in the path does not truncate it.
        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (exeIndex >= 0)
        {
            var end = exeIndex + ".exe".Length;
            if (end == trimmed.Length || char.IsWhiteSpace(trimmed[end]))
            {
                return trimmed[..end];
            }

            exeIndex = trimmed.IndexOf(".exe", end, StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }
}
