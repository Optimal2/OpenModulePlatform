namespace OpenModulePlatform.Installer.Core;

/// <summary>
/// Resolves the artifact payload root for the matched profile. Mirrors the
/// Bootstrapper's ResolvePayloadRoot rules (Program.cs): the executable's own
/// folder when it is a package root, the parent of a <c>configs</c> folder,
/// and the package root relative to a <c>hosts\&lt;profile&gt;</c> directory.
/// </summary>
public static class PayloadRootResolver
{
    public static string Resolve(string configPath)
    {
        var appBaseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (LooksLikeInstallerPackageRoot(appBaseDirectory))
        {
            return appBaseDirectory;
        }

        var configDirectory = Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory;
        var trimmedConfigDirectory = Path.TrimEndingDirectorySeparator(configDirectory);
        if (Path.GetFileName(trimmedConfigDirectory).Equals("configs", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(trimmedConfigDirectory) ?? configDirectory;
        }

        return TryResolvePackageRootFromHostProfileDirectory(configDirectory)
            ?? configDirectory;
    }

    internal static bool LooksLikeInstallerPackageRoot(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var directoryName = Path.GetFileName(fullPath);
        return directoryName.StartsWith("OpenModulePlatformHostAgentFirst-", StringComparison.OrdinalIgnoreCase)
            || (directoryName.Equals("installer", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Join(fullPath, "OpenModulePlatform.Bootstrapper.exe")))
            || File.Exists(Path.Join(fullPath, "hostagent-first-package.json"))
            || Directory.Exists(Path.Join(fullPath, "data", "global"));
    }

    private static string? TryResolvePackageRootFromHostProfileDirectory(string configDirectory)
    {
        var hostDirectory = new DirectoryInfo(Path.GetFullPath(configDirectory));
        var hostsDirectory = hostDirectory.Parent;
        if (hostsDirectory is null
            || !hostsDirectory.Name.Equals("hosts", StringComparison.OrdinalIgnoreCase)
            || hostsDirectory.Parent is null)
        {
            return null;
        }

        var packageLocalCandidate = hostsDirectory.Parent.FullName;
        if (LooksLikeInstallerPackageRoot(packageLocalCandidate))
        {
            return packageLocalCandidate;
        }

        var siblingInstallerRoot = Path.Join(hostsDirectory.Parent.FullName, "installer");
        if (Directory.Exists(siblingInstallerRoot) && LooksLikeInstallerPackageRoot(siblingInstallerRoot))
        {
            return siblingInstallerRoot;
        }

        var siblingPackagesRoot = Path.Join(hostsDirectory.Parent.FullName, "package");
        if (!Directory.Exists(siblingPackagesRoot))
        {
            return null;
        }

        return Directory
            .EnumerateDirectories(siblingPackagesRoot, "OpenModulePlatformHostAgentFirst-*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(LooksLikeInstallerPackageRoot);
    }
}
