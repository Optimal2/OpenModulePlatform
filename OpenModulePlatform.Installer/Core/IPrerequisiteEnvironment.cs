namespace OpenModulePlatform.Installer.Core;

/// <summary>The state of one Windows feature relevant to the install.</summary>
public sealed record WindowsFeatureState(string Name, bool IsInstalled);

/// <summary>
/// Read-only view of the machine the prerequisite evaluator queries. Every
/// probe is an interface member so tests can evaluate prerequisites against a
/// fully fake machine, and so a dry run provably never mutates anything (the
/// interface has no mutating members at all).
/// </summary>
public interface IPrerequisiteEnvironment
{
    /// <summary>True on Windows Server, false on client Windows.</summary>
    bool IsServerOs { get; }

    /// <summary>Windows edition label for display (for example from the registry ProductName).</summary>
    string WindowsEditionName { get; }

    /// <summary>The local machine name used for account classification.</summary>
    string MachineName { get; }

    /// <summary>The install state of the named Windows feature, or null when the feature is unknown.</summary>
    bool? IsFeatureInstalled(string featureName);

    /// <summary>Highest installed major version of the shared ASP.NET Core runtime, or null when none.</summary>
    int? GetHighestAspNetCoreRuntimeMajor();

    /// <summary>True when AspNetCoreModuleV2 is registered as a global module in applicationHost.config.</summary>
    bool IsAspNetCoreIisModuleRegistered();

    /// <summary>True when the ASP.NET Core Module V2 DLL exists under %ProgramFiles%\IIS\Asp.Net Core Module\V2.</summary>
    bool AspNetCoreIisModuleDllExists();

    /// <summary>
    /// Probes SQL connectivity with the profile's settings and returns a
    /// human-readable verdict. The probe must never create anything: success
    /// means the configured database EXISTS and answered a trivial query.
    /// </summary>
    Task<(bool Ok, string Detail)> ProbeSqlDatabaseAsync(string server, string database, bool trustServerCertificate, CancellationToken cancellationToken);

    /// <summary>Free bytes on the drive that will hold the install root.</summary>
    long GetFreeDiskBytes(string pathOnTargetDrive);

    /// <summary>The hosting-bundle installer files available in the package's prereqs\ folder.</summary>
    IReadOnlyList<string> GetAvailableHostingBundleInstallers();

    /// <summary>
    /// The text of every <c>*.runtimeconfig.json</c> under the artifact payload
    /// root (used to derive the required runtime major). An unreadable payload
    /// yields an empty list.
    /// </summary>
    IReadOnlyList<string> FindRuntimeConfigTexts(string payloadRoot);

    /// <summary>Reads the text of a file, or null when it does not exist.</summary>
    string? TryReadAllText(string path);

    /// <summary>True when the file exists.</summary>
    bool FileExists(string path);

    /// <summary>True when the directory exists.</summary>
    bool DirectoryExists(string path);
}
