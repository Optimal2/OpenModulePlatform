namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Identifies runtime configuration files that must stay outside immutable
/// artifact payloads. These files are written by HostAgent from OMP metadata or
/// config overlays so changing configuration never changes the artifact hash.
/// </summary>
public static class RuntimeConfigurationFiles
{
    private static readonly string[] ExactFileNames =
    [
        "odv.site.config.js"
    ];

    public static bool IsRuntimeConfigurationFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (string.Equals(fileName, "appsettings.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ExactFileNames.Any(
            name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase));
    }
}
