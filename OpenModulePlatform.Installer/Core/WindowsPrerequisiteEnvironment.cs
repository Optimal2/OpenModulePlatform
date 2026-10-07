using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installer.Core;

/// <summary>
/// The real machine, probed read-only for the prerequisite evaluation. Every
/// member only reads: registry values, feature states, files on disk, SQL
/// connectivity and free disk space.
/// </summary>
public sealed class WindowsPrerequisiteEnvironment : IPrerequisiteEnvironment
{
    private const int MaxRuntimeConfigFiles = 200;

    public bool IsServerOs
    {
        get
        {
            var installationType = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "InstallationType",
                string.Empty) as string;
            return string.Equals(installationType, "Server", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string WindowsEditionName
    {
        get
        {
            var productName = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "ProductName",
                string.Empty) as string;
            return string.IsNullOrWhiteSpace(productName)
                ? (IsServerOs ? "Windows Server" : "Windows")
                : productName.Trim();
        }
    }

    public string MachineName => Environment.MachineName;

    public bool? IsFeatureInstalled(string featureName)
    {
        try
        {
            if (IsServerOs)
            {
                var result = InstallationEngine.RunProcess(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command",
                        $"(Get-WindowsFeature -Name {featureName}).InstallState"],
                    throwOnFailure: false,
                    timeout: TimeSpan.FromMinutes(2));
                if (result.ExitCode != 0)
                {
                    return null;
                }

                var state = result.StdOut.Trim();
                return state.Equals("Installed", StringComparison.OrdinalIgnoreCase);
            }

            var dism = InstallationEngine.RunProcess(
                "dism.exe",
                ["/online", "/get-featureinfo", $"/featurename:{featureName}"],
                throwOnFailure: false,
                timeout: TimeSpan.FromMinutes(2));
            if (dism.ExitCode != 0)
            {
                return null;
            }

            foreach (var line in dism.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("State", StringComparison.OrdinalIgnoreCase)
                    && trimmed.Contains(':', StringComparison.Ordinal))
                {
                    var value = trimmed[(trimmed.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                    return value.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            return null;
        }
    }

    public int? GetHighestAspNetCoreRuntimeMajor()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var runtimeRoot = Path.Join(programFiles, "dotnet", "shared", "Microsoft.AspNetCore.App");
        if (!Directory.Exists(runtimeRoot))
        {
            return null;
        }

        int? highest = null;
        foreach (var directory in Directory.EnumerateDirectories(runtimeRoot))
        {
            var name = Path.GetFileName(directory);
            var dotIndex = name.IndexOf('.', StringComparison.Ordinal);
            if (dotIndex <= 0 || !int.TryParse(name[..dotIndex], out var major))
            {
                continue;
            }

            if (highest is null || major > highest.Value)
            {
                highest = major;
            }
        }

        return highest;
    }

    public bool IsAspNetCoreIisModuleRegistered()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var configPath = Path.Join(windows, "System32", "inetsrv", "config", "applicationHost.config");
        var text = TryReadAllText(configPath);
        return text is not null
            && text.Contains("AspNetCoreModuleV2", StringComparison.OrdinalIgnoreCase);
    }

    public bool AspNetCoreIisModuleDllExists()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return File.Exists(Path.Join(programFiles, "IIS", "Asp.Net Core Module", "V2", "aspnetcorev2.dll"));
    }

    public async Task<(bool Ok, string Detail)> ProbeSqlDatabaseAsync(
        string server,
        string database,
        bool trustServerCertificate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
        {
            return (false, "The profile does not configure sql.server and sql.database.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = trustServerCertificate,
            ConnectTimeout = 10
        };

        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT DB_ID();", connection) { CommandTimeout = 10 };
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is not null and not DBNull
                ? (true, $"Connected to {server}; the database {database} exists.")
                : (false, $"The database {database} was not found on {server}. It must be created by the database administrator before the installation; this installer never creates databases.");
        }
        catch (SqlException ex)
        {
            return (false, $"Could not connect to {server}/{database} with integrated security: {ex.Message}");
        }
    }

    public long GetFreeDiskBytes(string pathOnTargetDrive)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(pathOnTargetDrive));
            if (string.IsNullOrWhiteSpace(root))
            {
                return -1;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or SystemException)
        {
            return -1;
        }
    }

    public IReadOnlyList<string> GetAvailableHostingBundleInstallers()
    {
        var prereqs = Path.Join(AppContext.BaseDirectory, "prereqs");
        if (!Directory.Exists(prereqs))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(prereqs, "dotnet-hosting*.exe", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> FindRuntimeConfigTexts(string payloadRoot)
    {
        try
        {
            if (!Directory.Exists(payloadRoot))
            {
                return [];
            }

            return Directory
                .EnumerateFiles(payloadRoot, "*.runtimeconfig.json", SearchOption.AllDirectories)
                .Take(MaxRuntimeConfigFiles)
                .Select(path =>
                {
                    try
                    {
                        return File.ReadAllText(path);
                    }
                    catch (IOException)
                    {
                        return string.Empty;
                    }
                })
                .Where(static text => text.Length > 0)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            return null;
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);
}
