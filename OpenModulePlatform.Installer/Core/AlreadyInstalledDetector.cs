using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installer.Core;

/// <summary>
/// Decides whether this computer already has an OMP installation for the matched
/// profile. The first-install tool refuses to continue in that case: it never
/// upgrades, repairs or uninstalls.
/// </summary>
public static class AlreadyInstalledDetector
{
    /// <summary>
    /// Pure decision over pre-gathered facts: the HostAgent Windows service
    /// existing, or a HostAgent runtime already present under the profile's
    /// install root, means an installation exists.
    /// </summary>
    public static bool IsAlreadyInstalled(bool hostAgentServiceExists, bool installRootContainsHostAgent)
        => hostAgentServiceExists || installRootContainsHostAgent;

    /// <summary>
    /// Gathers the facts for the matched profile: does the profile's HostAgent
    /// service exist (configured name or the versioned name the install chain
    /// would use), and does the install root already contain a HostAgent.
    /// </summary>
    public static bool IsAlreadyInstalled(BootstrapConfig config)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var hostAgent = config.HostAgent;
        var serviceName = hostAgent.ServiceName?.Trim() ?? string.Empty;
        var installPath = hostAgent.InstallPath?.Trim() ?? string.Empty;
        if (hostAgent.Enabled && !string.IsNullOrWhiteSpace(serviceName) && !string.IsNullOrWhiteSpace(installPath))
        {
            var identity = InstallationEngine.ResolveBootstrapHostAgentServiceIdentity(config);
            serviceName = string.IsNullOrWhiteSpace(identity.ServiceName) ? serviceName : identity.ServiceName;
            installPath = string.IsNullOrWhiteSpace(identity.InstallPath) ? installPath : identity.InstallPath;
        }

        var serviceExists = !string.IsNullOrWhiteSpace(serviceName)
            && InstallationEngine.ServiceExists(serviceName);
        var installRootContainsHostAgent = !string.IsNullOrWhiteSpace(installPath)
            && File.Exists(Path.Join(installPath, "OpenModulePlatform.HostAgent.WindowsService.exe"));

        return IsAlreadyInstalled(serviceExists, installRootContainsHostAgent);
    }
}
