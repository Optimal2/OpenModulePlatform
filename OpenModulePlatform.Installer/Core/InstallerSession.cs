using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installer.Core;

/// <summary>The fully prepared installation context: matched profile, config, payload root.</summary>
public sealed record InstallerSession(
    BootstrapConfigProfile Profile,
    BootstrapConfig Config,
    string PayloadRoot)
{
    /// <summary>
    /// The profile's environment label for the confirmation card: the
    /// <c>hosts\&lt;profile&gt;</c> folder name when the config lives in one,
    /// otherwise the config file name.
    /// </summary>
    public string EnvironmentLabel
    {
        get
        {
            var configDirectory = Path.GetDirectoryName(Profile.ConfigPath);
            var parent = configDirectory is null ? null : Directory.GetParent(configDirectory);
            if (parent is not null && parent.Name.Equals("hosts", StringComparison.OrdinalIgnoreCase))
            {
                return new DirectoryInfo(configDirectory!).Name;
            }

            return Path.GetFileNameWithoutExtension(Profile.ConfigPath);
        }
    }
}

/// <summary>Loads the matched profile into a session, shared by the GUI and --dry-run.</summary>
public static class InstallerSessionLoader
{
    public static async Task<InstallerSession> LoadAsync(BootstrapConfigProfile profile)
    {
        var config = await BootstrapConfigLoader.LoadAsync(profile.ConfigPath);
        var payloadRoot = PayloadRootResolver.Resolve(profile.ConfigPath);
        return new InstallerSession(profile, config, payloadRoot);
    }
}

/// <summary>Builds the confirmation-card lines for the matched profile.</summary>
public static class ProfileSummaryBuilder
{
    public static IReadOnlyList<(string Label, string Value)> Build(
        InstallerSession session,
        string matchedMachineName)
    {
        var config = session.Config;
        var lines = new List<(string, string)>
        {
            ("Profile", session.Profile.DisplayName),
            ("Computer", matchedMachineName),
            ("Environment", session.EnvironmentLabel),
            ("SQL Server", config.Sql.Server),
            ("Database", config.Sql.Database),
            ("Modules", CountModuleDefinitions(session.PayloadRoot).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Artifacts", config.Artifacts.Count(artifact => artifact.Enabled).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Install root", config.HostAgent.InstallPath)
        };
        return lines;
    }

    private static int CountModuleDefinitions(string payloadRoot)
    {
        try
        {
            var root = Path.Join(payloadRoot, "data", "global", "module-definitions");
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*.module-definition.json", SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
