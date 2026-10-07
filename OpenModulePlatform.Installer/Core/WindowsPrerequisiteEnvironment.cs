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

    public IReadOnlyList<int> GetInstalledAspNetCoreRuntimeMajors()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var runtimeRoot = Path.Join(programFiles, "dotnet", "shared", "Microsoft.AspNetCore.App");
        if (!Directory.Exists(runtimeRoot))
        {
            return [];
        }

        var majors = new SortedSet<int>();
        foreach (var directory in Directory.EnumerateDirectories(runtimeRoot))
        {
            var name = Path.GetFileName(directory);
            var dotIndex = name.IndexOf('.', StringComparison.Ordinal);
            if (dotIndex <= 0 || !int.TryParse(name[..dotIndex], out var major))
            {
                continue;
            }

            majors.Add(major);
        }

        return majors.ToArray();
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

        // Connect to master first so "the server does not answer" is reported
        // distinctly from "the server answers but the database does not exist".
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = trustServerCertificate,
            ConnectTimeout = 10
        };

        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                "SELECT COUNT(1) FROM sys.databases WHERE name = @databaseName;",
                connection)
            { CommandTimeout = 10 };
            command.Parameters.AddWithValue("@databaseName", database.Trim());
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            return count > 0
                ? (true, $"Connected to {server}; the database {database} exists.")
                : (false, $"The database {database} does not exist on {server}. It is never created by this installer; have the database administrator create it before the installation.");
        }
        catch (SqlException ex)
        {
            return (false, $"SQL Server {server} is unreachable with integrated security: {ex.Message}");
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

    public (bool Found, bool HasPrivateKey)? ProbeLocalMachineCertificate(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return (false, false);
        }

        try
        {
            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.My,
                System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
            store.Open(
                System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly
                | System.Security.Cryptography.X509Certificates.OpenFlags.OpenExistingOnly);
            var matches = store.Certificates.Find(
                System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
                thumbprint.Trim(),
                validOnly: false);
            if (matches.Count == 0)
            {
                return (false, false);
            }

            var hasPrivateKey = false;
            foreach (System.Security.Cryptography.X509Certificates.X509Certificate2 certificate in matches)
            {
                hasPrivateKey |= certificate.HasPrivateKey;
                certificate.Dispose();
            }

            return (true, hasPrivateKey);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or SystemException)
        {
            return null;
        }
    }
}
