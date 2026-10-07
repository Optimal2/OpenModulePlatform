using System.Net;

namespace OpenModulePlatform.Installation;

/// <summary>
/// A bootstrap configuration profile discovered on disk, with the machine names it
/// claims (profile.machineNames, hostAgent.hostName and hostAgent.hostKey).
/// </summary>
public sealed record BootstrapConfigProfile(
    string DisplayName,
    string ConfigPath,
    IReadOnlyList<string> MachineNames);

/// <summary>
/// Discovers host profiles (hosts\&lt;profile&gt;\bootstrap.json and loose config files)
/// around the current directory, the executable folder and up to three parents, and
/// loads the local machine names used for profile matching.
/// </summary>
public static class HostProfileDiscovery
{
    public static IReadOnlyList<BootstrapConfigProfile> ResolveProfiles(string? configPath, string? configDirectory)
    {
        var profiles = new Dictionary<string, BootstrapConfigProfile>(StringComparer.OrdinalIgnoreCase);
        void AddConfigFile(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return;
            }

            var config = InstallationEngine.ReadJsonAsync<BootstrapConfig>(fullPath).GetAwaiter().GetResult();
            var name = Path.GetFileNameWithoutExtension(fullPath)
                .Replace("bootstrap.", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(".sample", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace('.', ' ')
                .Replace('-', ' ')
                .Replace('_', ' ')
                .Trim();
            if (!string.IsNullOrWhiteSpace(config.Profile.DisplayName))
            {
                name = config.Profile.DisplayName.Trim();
            }

            profiles[fullPath] = new BootstrapConfigProfile(
                string.IsNullOrWhiteSpace(name) ? Path.GetFileName(fullPath) : name,
                fullPath,
                ResolveProfileMachineNames(config));
        }

        void AddConfigDirectory(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                return;
            }

            foreach (var candidatePath in Directory.EnumerateFiles(fullPath, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
            {
                AddConfigFile(candidatePath);
            }
        }

        void AddHostProfileRoot(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                return;
            }

            foreach (var profileDirectory in Directory.EnumerateDirectories(fullPath, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
            {
                var bootstrapPath = Path.Join(profileDirectory, "bootstrap.json");
                if (File.Exists(bootstrapPath))
                {
                    AddConfigFile(bootstrapPath);
                    continue;
                }

                AddConfigDirectory(profileDirectory);
            }
        }

        if (!string.IsNullOrWhiteSpace(configPath))
        {
            AddConfigFile(configPath);
        }

        foreach (var directory in EnumerateConfigDirectories(configPath, configDirectory))
        {
            AddConfigDirectory(directory);
        }

        foreach (var directory in EnumerateHostProfileRoots(configPath, configDirectory))
        {
            AddHostProfileRoot(directory);
        }

        if (profiles.Count == 0)
        {
            foreach (var path in EnumerateLegacyConfigFiles())
            {
                AddConfigFile(path);
            }
        }

        return profiles.Values
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ConfigPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> ResolveProfileMachineNames(BootstrapConfig config)
    {
        var names = new List<string>();
        names.AddRange(config.Profile.MachineNames);
        if (!string.IsNullOrWhiteSpace(config.HostAgent.HostName))
        {
            names.Add(config.HostAgent.HostName);
        }

        if (!string.IsNullOrWhiteSpace(config.HostAgent.HostKey))
        {
            names.Add(config.HostAgent.HostKey);
        }

        return names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlySet<string> GetLocalMachineNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddMachineName(names, Environment.MachineName);
        try
        {
            AddMachineName(names, Dns.GetHostName());
        }
        catch (System.Net.Sockets.SocketException)
        {
            // DNS lookups are not required for profile matching; the local
            // machine name above is enough for normal Windows installations.
        }

        return names;
    }

    private static void AddMachineName(HashSet<string> names, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        names.Add(normalized);
        var dotIndex = normalized.IndexOf('.');
        if (dotIndex > 0)
        {
            names.Add(normalized[..dotIndex]);
        }
    }

    private static IEnumerable<string> EnumerateConfigDirectories(string? configPath, string? configDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            yield return configDirectory;
        }

        if (!string.IsNullOrWhiteSpace(configPath)
            && Path.GetDirectoryName(configPath) is { } explicitConfigDirectory)
        {
            yield return explicitConfigDirectory;
            yield return Path.Join(explicitConfigDirectory, "configs");
        }

        yield return Path.Join(Environment.CurrentDirectory, "configs");
        yield return Path.Join(AppContext.BaseDirectory, "configs");
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "configs"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "configs"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "configs"));
    }

    private static IEnumerable<string> EnumerateHostProfileRoots(string? configPath, string? configDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            yield return Path.Join(configDirectory, "hosts");
        }

        if (!string.IsNullOrWhiteSpace(configPath)
            && Path.GetDirectoryName(configPath) is { } explicitConfigDirectory)
        {
            yield return Path.Join(explicitConfigDirectory, "hosts");
            yield return Path.GetFullPath(Path.Join(explicitConfigDirectory, ".."));
            yield return Path.GetFullPath(Path.Join(explicitConfigDirectory, "..", "..", "hosts"));
        }

        yield return Path.Join(Environment.CurrentDirectory, "hosts");
        yield return Path.Join(AppContext.BaseDirectory, "hosts");
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "hosts"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "hosts"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "hosts"));
    }

    private static IEnumerable<string> EnumerateLegacyConfigFiles()
    {
        yield return Path.Join(Environment.CurrentDirectory, "bootstrap.local.sample.json");
        yield return Path.Join(AppContext.BaseDirectory, "bootstrap.local.sample.json");
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "bootstrap.local.sample.json"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "bootstrap.local.sample.json"));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "bootstrap.local.sample.json"));
    }
}

/// <summary>
/// Matches discovered profiles against a set of candidate machine names and selects
/// the single profile for this computer. Pure: the candidate names are supplied by
/// the caller, so a first-install tool can dry-run the selection with any name.
/// Rules (unchanged from the Bootstrapper): case-insensitive matching with and
/// without domain suffix, sample configs lose to real profiles, and exactly one
/// profile must match.
/// </summary>
public static class HostProfileMatcher
{
    public static string SelectConfigPath(
        IReadOnlyList<BootstrapConfigProfile> profiles,
        IReadOnlySet<string> candidateMachineNames)
    {
        if (profiles.Count == 0)
        {
            throw new FileNotFoundException(
                "No bootstrap configuration file was found. Create a machine-specific profile in 'hosts\\<profile>\\bootstrap.json' or in the package 'configs' folder before starting the installer.");
        }

        var localMachineNames = candidateMachineNames;
        var machineMatches = profiles
            .Where(profile => ProfileMatchesMachine(profile, localMachineNames))
            .ToArray();

        // On developer machines the generated sample template carries the
        // build machine's hostAgent identity, so it can shadow the real host
        // profile. A sample may only be selected when no real config matches.
        var nonSampleMatches = machineMatches
            .Where(static profile => !Path.GetFileName(profile.ConfigPath)
                .EndsWith(".sample.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (nonSampleMatches.Length > 0)
        {
            machineMatches = nonSampleMatches;
        }

        if (machineMatches.Length == 1)
        {
            return machineMatches[0].ConfigPath;
        }

        if (machineMatches.Length > 1)
        {
            throw new InvalidOperationException(
                "More than one bootstrap configuration matches this computer. Keep exactly one matching host profile, then start the installer again." + Environment.NewLine + Environment.NewLine
                + "Local computer names: " + string.Join(", ", localMachineNames.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)) + Environment.NewLine
                + "Matching configs: " + string.Join(", ", machineMatches.Select(static profile => profile.ConfigPath)));
        }

        throw new InvalidOperationException(
            "No bootstrap configuration matches this computer. The installer is locked to the config whose profile.machineNames, hostAgent.hostName, or hostAgent.hostKey matches the local computer name." + Environment.NewLine + Environment.NewLine
            + "Local computer names: " + string.Join(", ", localMachineNames.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)) + Environment.NewLine
            + "Profile folders: " + string.Join(Environment.NewLine + "  ", profiles.Select(static profile => Path.GetDirectoryName(profile.ConfigPath)).Distinct(StringComparer.OrdinalIgnoreCase)) + Environment.NewLine + Environment.NewLine
            + "Create or update a matching host profile, then start the installer again.");
    }

    private static bool ProfileMatchesMachine(
        BootstrapConfigProfile profile,
        IReadOnlySet<string> localMachineNames)
        => profile.MachineNames.Any(name => MachineNameMatches(name, localMachineNames));

    private static bool MachineNameMatches(string configuredName, IReadOnlySet<string> localMachineNames)
    {
        if (string.IsNullOrWhiteSpace(configuredName))
        {
            return false;
        }

        var normalized = configuredName.Trim();
        if (localMachineNames.Contains(normalized))
        {
            return true;
        }

        var dotIndex = normalized.IndexOf('.');
        return dotIndex > 0 && localMachineNames.Contains(normalized[..dotIndex]);
    }
}
