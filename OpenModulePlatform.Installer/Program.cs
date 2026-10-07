using System.Text;
using OpenModulePlatform.Installation;
using OpenModulePlatform.Installer.Core;
using OpenModulePlatform.Installer.Install;
using OpenModulePlatform.Installer.Ui;

namespace OpenModulePlatform.Installer;

/// <summary>Command line for the first-install tool.</summary>
internal sealed record CliOptions(bool DryRun, string MachineName, bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        var dryRun = false;
        var machineName = string.Empty;
        var showHelp = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--machine-name":
                    if (i + 1 >= args.Length)
                    {
                        throw new InvalidOperationException("Missing value for --machine-name.");
                    }

                    machineName = args[++i];
                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument: {args[i]}");
            }
        }

        if (!string.IsNullOrWhiteSpace(machineName) && !dryRun)
        {
            // --machine-name exists so a prepared profile can be dry-run checked
            // from another machine; a REAL install always runs on the machine
            // the profile matches, so the override is meaningless there.
            throw new InvalidOperationException("--machine-name can only be used together with --dry-run.");
        }

        return new CliOptions(dryRun, machineName.Trim(), showHelp);
    }
}

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        CliOptions cli;
        try
        {
            cli = CliOptions.Parse(args);
        }
        catch (InvalidOperationException ex)
        {
            ConsoleErrorAttach.WriteLine(ex.Message);
            return 1;
        }

        if (cli.DryRun)
        {
            return await RunDryRunAsync(cli);
        }

        if (cli.ShowHelp)
        {
            ConsoleErrorAttach.WriteUsage();
            return 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            ConsoleErrorAttach.WriteLine("OpenModulePlatform.Installer requires Windows.");
            return 1;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// Runs every check and prints what a real install would do, without
    /// changing anything: no feature installs, no hosting bundle, no SQL
    /// writes, no service or IIS changes.
    /// </summary>
    private static async Task<int> RunDryRunAsync(CliOptions cli)
    {
        ConsoleErrorAttach.AttachToParentConsole();
        try
        {
            var candidates = string.IsNullOrWhiteSpace(cli.MachineName)
                ? HostProfileDiscovery.GetLocalMachineNames()
                : InstallerProfileResolverCandidateNames(cli.MachineName);

            var resolution = InstallerProfileResolver.DiscoverAndResolve(candidates);
            switch (resolution.Outcome)
            {
                case ProfileMatchOutcome.NoProfilesFound:
                    Console.WriteLine("No installation profiles were found next to the installer (hosts\\<profile>\\bootstrap.json).");
                    return 2;
                case ProfileMatchOutcome.NoMatch:
                    Console.WriteLine($"No installation is prepared for this computer ({string.Join(", ", resolution.CandidateMachineNames)}).");
                    return 2;
                case ProfileMatchOutcome.MultipleMatches:
                    Console.WriteLine("More than one profile matches: " + string.Join(", ", resolution.MatchingProfiles.Select(p => p.DisplayName)));
                    return 2;
            }

            var session = await InstallerSessionLoader.LoadAsync(resolution.Profile!);
            Console.WriteLine("Profile:    " + session.Profile.DisplayName);
            Console.WriteLine("Config:     " + session.Profile.ConfigPath);
            Console.WriteLine("Payload:    " + session.PayloadRoot);
            Console.WriteLine("SQL target: " + session.Config.Sql.Server + "/" + session.Config.Sql.Database);
            Console.WriteLine();

            if (AlreadyInstalledDetector.IsAlreadyInstalled(session.Config))
            {
                Console.WriteLine("An installation already exists on this computer. This program only performs the first installation.");
                return 2;
            }

            var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
                session.Config,
                session.PayloadRoot,
                new WindowsPrerequisiteEnvironment(),
                serviceAccountPasswordValidated: false);

            var request = new InstallRequest(
                session.Config,
                session.Profile.ConfigPath,
                session.PayloadRoot,
                evaluation,
                OperatorPassword: string.Empty);
            await InstallOrchestrator.RunAsync(
                request,
                new ThrowingInstallActions(),
                new ConsoleInstallProgress(),
                dryRun: true);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException or IOException)
        {
            Console.WriteLine("Dry run failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Candidate machine names for a --machine-name override (with and without domain suffix).</summary>
    internal static IReadOnlySet<string> InstallerProfileResolverCandidateNames(string machineName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = machineName.Trim();
        names.Add(trimmed);
        var dotIndex = trimmed.IndexOf('.');
        if (dotIndex > 0)
        {
            names.Add(trimmed[..dotIndex]);
        }

        return names;
    }
}

/// <summary>
/// Dry-run guard: the orchestrator never touches actions in dry-run mode, and
/// this implementation proves it by throwing on every call.
/// </summary>
internal sealed class ThrowingInstallActions : IInstallActions
{
    public void InstallIisFeatures(IReadOnlyList<string> featureNames, bool isServerOs)
        => throw new InvalidOperationException("Dry run must not install features.");

    public int RunHostingBundle(string installerPath, bool repair)
        => throw new InvalidOperationException("Dry run must not run the hosting bundle.");

    public void RestartIis()
        => throw new InvalidOperationException("Dry run must not restart IIS.");

    public bool EnsureServiceLogonRight(string accountName, out string detail)
        => throw new InvalidOperationException("Dry run must not grant rights.");

    public bool ValidateAccountPassword(string userName, string domain, string password, out string error)
        => throw new InvalidOperationException("Dry run must not validate passwords.");

    public Task<bool> WaitForHostAgentActiveAsync(string server, string database, bool trustServerCertificate, string hostKey, string serviceName, DateTimeOffset notBefore, TimeSpan timeout, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Dry run must not wait for services.");

    public Task<(bool Ok, string Detail)> ProbePortalAsync(string portalUrl, TimeSpan timeout, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Dry run must not probe the portal.");
}

/// <summary>Console plumbing for the WinExe dry-run path (attach to the parent console).</summary>
internal static class ConsoleErrorAttach
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void WriteLine(string message)
    {
        AttachToParentConsole();
        Console.WriteLine(message);
    }

    public static void WriteUsage()
    {
        WriteLine("OpenModulePlatform.Installer - first installation of OpenModulePlatform on this computer.");
        WriteLine(string.Empty);
        WriteLine("Usage:");
        WriteLine("  OpenModulePlatform.Installer.exe                 Start the graphical installer.");
        WriteLine("  OpenModulePlatform.Installer.exe --dry-run       Run every check and print what would be installed, without changing anything.");
        WriteLine("  OpenModulePlatform.Installer.exe --dry-run --machine-name <name>");
        WriteLine("                                                   Dry-run as if running on <name>.");
    }

    public static void AttachToParentConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            return;
        }

        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch (IOException)
        {
            // Best-effort only.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
