namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Service-name rules shared by the job processor, self-upgrade and service-app cleanup.
/// </summary>
internal static class HostAgentServiceNames
{
    // "EMP.*" is a legacy service-name family accepted for in-place upgrades:
    // upgrade and cleanup logic must still recognize installs registered under it.
    public static readonly string[] KnownHostAgentServiceNamePrefixes =
    [
        "EMP.HostAgent",
        "OMP.HostAgent",
        "OpenModulePlatform.HostAgent"
    ];

    /// <summary>
    /// The known prefixes plus the configured one, for matching installed service names.
    /// </summary>
    public static IReadOnlySet<string> GetKnownPrefixes(string serviceNamePrefix)
    {
        var prefixes = new HashSet<string>(KnownHostAgentServiceNamePrefixes, StringComparer.OrdinalIgnoreCase);
        var prefix = serviceNamePrefix.Trim().TrimEnd('.');
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            prefixes.Add(prefix);
        }

        return prefixes;
    }

    /// <summary>
    /// Strips a trailing dotted numeric version ("OMP.HostAgent.1.2.3" becomes
    /// "OMP.HostAgent") so a versioned service name yields its prefix.
    /// </summary>
    public static string TrimTrailingVersion(string serviceName)
    {
        var trimmed = serviceName.Trim();
        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return trimmed;
        }

        var suffixStart = parts.Length;
        while (suffixStart > 0 && parts[suffixStart - 1].All(char.IsDigit))
        {
            suffixStart--;
        }

        return suffixStart == parts.Length
            ? trimmed
            : string.Join('.', parts.Take(Math.Max(1, suffixStart)));
    }
}
