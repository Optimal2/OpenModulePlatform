using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.Bootstrapper;

internal static partial class Program
{
    private const string BootstrapPrincipalPlaceholder = "__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__";
    private const int SqlDeadlockErrorNumber = 1205;
    private const int SqlDeadlockRetryCount = 3;
    private const int ServiceStopTimeoutSeconds = 60;
    private const int ArtifactHashBufferSize = 64 * 1024;
    private const string HostAgentWindowsServiceExecutableName = "OpenModulePlatform.HostAgent.WindowsService.exe";
    private static readonly string[] KnownHostAgentServiceNamePrefixes =
    [
        "OMP.HostAgent",
        "OpenModulePlatform.HostAgent"
    ];
    private static readonly IReadOnlyDictionary<string, string> EmptyStringDictionary =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var cli = CliOptions.Parse(args);
            if (cli.RefreshInstallerPackage)
            {
                return await RunInstallerPackageRefreshAsync(cli);
            }

            if (cli.SyncPackageObjects)
            {
                EnsureConsole();
                return await RunSyncPackageObjectsAsync(cli);
            }

            var useGui = cli.Gui || (args.Length == 0 && OperatingSystem.IsWindows() && Environment.UserInteractive);
            if (!useGui)
            {
                EnsureConsole();
            }

            if (cli.ShowHelp)
            {
                WriteUsage();
                return 0;
            }

            if (useGui)
            {
                return RunInstallerGui(cli);
            }

            if (string.IsNullOrWhiteSpace(cli.ConfigPath))
            {
                WriteUsage();
                return 1;
            }

            var configPath = Path.GetFullPath(cli.ConfigPath);
            var config = await ReadJsonAsync<BootstrapConfig>(configPath);
            var payloadRoot = ResolvePayloadRoot(cli, configPath);
            if (cli.Uninstall)
            {
                return await RunUninstallAsync(
                    config,
                    configPath,
                    cli.RemoveRuntimeFiles,
                    cli.RemoveDatabaseObjects,
                    cli.Yes);
            }

            if (cli.SyncPackageObjectsBeforeAction)
            {
                var syncExitCode = await RunPackageObjectSyncForActionAsync(cli, config, configPath, payloadRoot);
                if (syncExitCode != 0)
                {
                    return syncExitCode;
                }
            }

            if (cli.UpgradeOrComplete)
            {
                return await RunUpgradeOrCompleteAsync(
                    config,
                    configPath,
                    payloadRoot,
                    cli.PayloadZipPath,
                    trustVersionNumbers: !cli.FullContentCheck);
            }

            return await RunBootstrapAsync(config, configPath, payloadRoot, cli.PayloadZipPath, cli.Yes);
        }
        catch (JsonException ex)
        {
            // Top-level console boundary: report installer configuration failures as a clean exit code.
            Console.Error.WriteLine("Bootstrap failed.");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (SystemException ex)
        {
            // Top-level console boundary: report any installer failure as a clean exit code.
            Console.Error.WriteLine("Bootstrap failed.");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunBootstrapAsync(CliOptions cli)
    {
        var configPath = Path.GetFullPath(cli.ConfigPath);
        var config = await ReadJsonAsync<BootstrapConfig>(configPath);
        var payloadRoot = ResolvePayloadRoot(cli, configPath);
        return await RunBootstrapAsync(config, configPath, payloadRoot, cli.PayloadZipPath, cli.Yes);
    }

    private static async Task<int> RunBootstrapAsync(
        BootstrapConfig config,
        string configPath,
        string payloadRoot,
        string payloadZipPath,
        bool yes)
    {
        var temporaryPayloadRoot = string.Empty;

        try
        {
            if (!string.IsNullOrWhiteSpace(payloadZipPath))
            {
                temporaryPayloadRoot = Path.Join(
                    Path.GetTempPath(),
                    "OpenModulePlatform.Bootstrapper",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryPayloadRoot);
                ZipFile.ExtractToDirectory(Path.GetFullPath(payloadZipPath), temporaryPayloadRoot, overwriteFiles: true);
                payloadRoot = temporaryPayloadRoot;
            }

            var artifactSelectionMessages = SelectLatestAvailableArtifactPackages(config, payloadRoot);
            WritePlan(config, configPath, payloadRoot);
            WriteArtifactSelectionMessages(artifactSelectionMessages);
            if (!yes && !Confirm("Continue with OpenModulePlatform bootstrap?"))
            {
                Console.WriteLine("Bootstrap cancelled.");
                return 2;
            }

            if (config.Sql.Enabled)
            {
                await RunSqlAsync(config, configPath, payloadRoot);
                await ImportModuleDefinitionsAsync(config, payloadRoot);
                await EnsureRuntimeDatabaseAccessAsync(config);
            }

            var preparedArtifactConfigurationFiles = PrepareArtifacts(
                config,
                configPath,
                payloadRoot,
                ArtifactPreparationMode.InstallOrUpdate);
            await RegisterPackageArtifactsAsync(config);
            await RegisterPreparedArtifactConfigurationFilesAsync(config, preparedArtifactConfigurationFiles);
            await CopyMissingArtifactConfigurationFilesFromPreviousVersionsAsync(
                config,
                preparedArtifactConfigurationFiles);
            PublishAvailableDeploymentObjects(config, payloadRoot);

            if (config.HostAgent.Enabled)
            {
                WriteHostAgentInstallOrUpdateIntent(config);
                EnsureRuntimeFilesystemAccess(config);
                await InstallHostAgentAsync(config, payloadRoot);
            }

            Console.WriteLine();
            Console.WriteLine("OpenModulePlatform bootstrap completed.");
            return 0;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPayloadRoot))
            {
                TryDeleteDirectory(temporaryPayloadRoot);
            }
        }
    }

    private static async Task<int> RunUpgradeOrCompleteAsync(
        BootstrapConfig config,
        string configPath,
        string payloadRoot,
        string payloadZipPath,
        bool trustVersionNumbers = false)
    {
        var temporaryPayloadRoot = string.Empty;

        try
        {
            if (!string.IsNullOrWhiteSpace(payloadZipPath))
            {
                temporaryPayloadRoot = Path.Join(
                    Path.GetTempPath(),
                    "OpenModulePlatform.Bootstrapper",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryPayloadRoot);
                ZipFile.ExtractToDirectory(Path.GetFullPath(payloadZipPath), temporaryPayloadRoot, overwriteFiles: true);
                payloadRoot = temporaryPayloadRoot;
            }

            var artifactSelectionMessages = SelectLatestAvailableArtifactPackages(config, payloadRoot);
            WritePlan(config, configPath, payloadRoot);
            WriteArtifactSelectionMessages(artifactSelectionMessages);

            if (config.Sql.Enabled)
            {
                await ImportModuleDefinitionsAsync(
                    config,
                    payloadRoot,
                    onlyNewerOrChanged: true,
                    trustSameVersion: trustVersionNumbers);
                await EnsureRuntimeDatabaseAccessAsync(config);
            }

            var preparedArtifactConfigurationFiles = PrepareArtifacts(
                config,
                configPath,
                payloadRoot,
                ArtifactPreparationMode.AddMissingOnly);
            await RegisterPackageArtifactsAsync(
                config,
                trustExistingArtifactVersions: trustVersionNumbers);
            await RegisterPreparedArtifactConfigurationFilesAsync(config, preparedArtifactConfigurationFiles);
            await CopyMissingArtifactConfigurationFilesFromPreviousVersionsAsync(
                config,
                preparedArtifactConfigurationFiles);
            PublishAvailableDeploymentObjects(config, payloadRoot, overwrite: false);

            if (!config.HostAgent.Enabled)
            {
                Console.WriteLine("> HostAgent installation is disabled in this profile.");
            }
            else if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("> HostAgent service check requires Windows; skipping HostAgent installation.");
            }
            else if (string.IsNullOrWhiteSpace(config.HostAgent.ServiceName))
            {
                Console.WriteLine("> HostAgent service name is not configured; skipping HostAgent installation.");
            }
            else
            {
                var identity = ResolveBootstrapHostAgentServiceIdentity(config);
                if (ServiceExists(identity.ServiceName))
                {
                    EnsureRuntimeFilesystemAccess(config);
                    await RefreshExistingHostAgentRuntimeSettingsAsync(config, identity);
                    Console.WriteLine("> HostAgent service already exists; refreshed runtime settings and credential store.");
                }
                else if (HostAgentServiceWithPrefixExists(identity.ServiceNamePrefix))
                {
                    EnsureRuntimeFilesystemAccess(config);
                    Console.WriteLine(
                        $"> HostAgent service '{identity.ServiceName}' is missing, but an existing HostAgent service is present; leaving runtime installation unchanged so self-upgrade can complete.");
                }
                else
                {
                    Console.WriteLine($"> HostAgent service '{identity.ServiceName}' is missing; installing it.");
                    EnsureRuntimeFilesystemAccess(config);
                    await InstallHostAgentAsync(config, payloadRoot);
                }
            }

            Console.WriteLine();
            Console.WriteLine("OpenModulePlatform upgrade/complete completed.");
            return 0;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPayloadRoot))
            {
                TryDeleteDirectory(temporaryPayloadRoot);
            }
        }
    }

    private static async Task<int> RunUninstallAsync(
        BootstrapConfig config,
        string configPath,
        bool removeRuntimeFiles,
        bool removeDatabaseObjects,
        bool yes)
    {
        WriteUninstallPlan(config, configPath, removeRuntimeFiles, removeDatabaseObjects);
        if (!yes && !Confirm("Continue with OpenModulePlatform uninstall?"))
        {
            Console.WriteLine("Uninstall cancelled.");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("OpenModulePlatform runtime uninstall currently requires Windows.");
        }

        if (!IsWindowsAdministrator())
        {
            throw new InvalidOperationException("Run the bootstrapper as Administrator to uninstall Windows services or IIS settings.");
        }

        RemoveWindowsServices(config);
        RemoveIisSiteAndAppPools(config.HostAgent);

        if (removeDatabaseObjects)
        {
            await RemoveDatabaseObjectsAsync(config.Sql);
        }

        if (removeRuntimeFiles)
        {
            RemoveRuntimeDirectories(config);
        }

        Console.WriteLine();
        Console.WriteLine("OpenModulePlatform uninstall completed.");
        return 0;
    }

    private static async Task<T> ReadJsonAsync<T>(string path)
        where T : new()
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Configuration file was not found.", path);
        }

        await using var stream = File.OpenRead(path);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        return value ?? new T();
    }

    private static string ResolvePayloadRoot(CliOptions cli, string configPath)
    {
        if (!string.IsNullOrWhiteSpace(cli.PayloadRoot))
        {
            return Path.GetFullPath(cli.PayloadRoot);
        }

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

    private static bool LooksLikeInstallerPackageRoot(string path)
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

    private static void WritePlan(BootstrapConfig config, string configPath, string payloadRoot)
    {
        Console.WriteLine("OpenModulePlatform HostAgent-first bootstrap");
        Console.WriteLine($"Config:         {configPath}");
        Console.WriteLine($"Payload root:   {payloadRoot}");
        Console.WriteLine($"SQL target:     {config.Sql.Server}/{config.Sql.Database}");
        Console.WriteLine($"Artifact store: {config.ArtifactStoreRoot}");
        var hostAgentIdentity = ResolveBootstrapHostAgentServiceIdentity(config);
        Console.WriteLine($"HostAgent:      {hostAgentIdentity.ServiceName} -> {hostAgentIdentity.InstallPath}");
        Console.WriteLine();
    }

    private static void WriteUninstallPlan(
        BootstrapConfig config,
        string configPath,
        bool removeRuntimeFiles,
        bool removeDatabaseObjects)
    {
        Console.WriteLine("OpenModulePlatform HostAgent-first uninstall");
        Console.WriteLine($"Config:           {configPath}");
        Console.WriteLine($"SQL target:       {config.Sql.Server}/{config.Sql.Database}");
        Console.WriteLine($"IIS site:         {config.HostAgent.IisSiteName}");
        Console.WriteLine($"HostAgent:        {config.HostAgent.ServiceName} -> {config.HostAgent.InstallPath}");
        Console.WriteLine($"Services root:    {config.HostAgent.ServicesRoot}");
        Console.WriteLine($"Web apps root:    {config.HostAgent.WebAppsRoot}");
        Console.WriteLine($"Artifact store:   {config.ArtifactStoreRoot}");
        Console.WriteLine($"Runtime files:    {(removeRuntimeFiles ? "remove" : "keep")}");
        Console.WriteLine($"Database objects: {(removeDatabaseObjects ? "remove all user objects; keep database" : "keep")}");
        Console.WriteLine();
    }

    private static bool Confirm(string prompt)
    {
        Console.Write($"{prompt} [Y/N, default N]: ");
        var answer = Console.ReadLine();
        return string.Equals(answer?.Trim(), "Y", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteUsage()
    {
        Console.WriteLine("OpenModulePlatform.Bootstrapper");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --config <bootstrap.json> [--payload-root <path>] [--payload-zip <zip>] [--sync-package-objects-before-action] [--full-content-check] [--yes]");
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --config <bootstrap.json> --upgrade-or-complete [--payload-root <path>] [--payload-zip <zip>] [--sync-package-objects-before-action] [--full-content-check]");
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --config <bootstrap.json> --uninstall [--remove-runtime-files] [--remove-database-objects] [--yes]");
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --gui [--config <bootstrap.json> | --config-dir <configs>] [--payload-root <path>] [--payload-zip <zip>]");
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --refresh-installer-package --config <bootstrap.json> --payload-root <path> [--parent-process-id <pid>] [--restart-gui]");
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --sync-package-objects --config <bootstrap.json> [--payload-root <path>] [--full-content-check]");
        Console.WriteLine();
        Console.WriteLine("The bootstrapper runs initial SQL, prepares ArtifactStore, and installs the HostAgent service.");
    }

    private static async Task RunSqlAsync(BootstrapConfig config, string configPath, string payloadRoot)
    {
        var sql = config.Sql;
        if (string.IsNullOrWhiteSpace(sql.Database))
        {
            throw new InvalidOperationException("Sql:Database must be configured.");
        }

        if (sql.CreateDatabase)
        {
            await EnsureDatabaseAsync(sql);
        }

        foreach (var script in sql.Scripts.Where(static item => item.Enabled))
        {
            if (string.IsNullOrWhiteSpace(script.Path))
            {
                throw new InvalidOperationException("Sql:Scripts contains an enabled entry without Path.");
            }

            var scriptPath = ResolvePackageDataPath(payloadRoot, configPath, script.Path);
            Console.WriteLine($"> SQL {scriptPath}");
            var sqlText = ReadSqlFile(scriptPath, sql, payloadRoot, config.IncludeExampleApps, []);
            await ExecuteSqlBatchesAsync(sql, sql.Database, sqlText, scriptPath);
        }
    }

    private static async Task EnsureRuntimeDatabaseAccessAsync(BootstrapConfig config)
    {
        if (!config.Sql.GrantRuntimeDatabaseAccess)
        {
            return;
        }

        var accountNames = ResolveRuntimeAccountNames(config)
            .SelectMany(ResolveWindowsAccountNameCandidates)
            .Where(static accountName => !IsBuiltInServiceIdentity(accountName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static accountName => accountName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (accountNames.Count == 0)
        {
            return;
        }

        Console.WriteLine("> Runtime database access");
        await using var connection = new SqlConnection(BuildConnectionString(config.Sql, config.Sql.Database));
        await connection.OpenAsync();

        foreach (var accountName in accountNames)
        {
            var granted = await EnsureDatabaseReaderWriterAsync(
                connection,
                accountName,
                config.Sql.CommandTimeoutSeconds);
            Console.WriteLine(granted
                ? $"  {accountName}: db_datareader/db_datawriter."
                : $"  {accountName}: skipped; no matching SQL login exists.");
        }
    }

    private static async Task<bool> EnsureDatabaseReaderWriterAsync(
        SqlConnection connection,
        string accountName,
        int commandTimeoutSeconds)
    {
        const string sql = @"
DECLARE @UserName sysname = @runtime_user_name;
DECLARE @Sql nvarchar(max);

IF DATABASE_PRINCIPAL_ID(@UserName) IS NULL
   AND SUSER_ID(@UserName) IS NOT NULL
BEGIN
    SET @Sql = N'CREATE USER ' + QUOTENAME(@UserName) +
        N' FOR LOGIN ' + QUOTENAME(@UserName) + N';';
    EXEC sys.sp_executesql @Sql;
END;

IF DATABASE_PRINCIPAL_ID(@UserName) IS NOT NULL
BEGIN
    IF ISNULL(IS_ROLEMEMBER(N'db_datareader', @UserName), 0) <> 1
    BEGIN
        SET @Sql = N'ALTER ROLE db_datareader ADD MEMBER ' + QUOTENAME(@UserName) + N';';
        EXEC sys.sp_executesql @Sql;
    END;

    IF ISNULL(IS_ROLEMEMBER(N'db_datawriter', @UserName), 0) <> 1
    BEGIN
        SET @Sql = N'ALTER ROLE db_datawriter ADD MEMBER ' + QUOTENAME(@UserName) + N';';
        EXEC sys.sp_executesql @Sql;
    END;
END;

SELECT CASE WHEN DATABASE_PRINCIPAL_ID(@UserName) IS NULL THEN 0 ELSE 1 END;";

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = commandTimeoutSeconds
        };
        command.Parameters.AddWithValue("@runtime_user_name", accountName);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void EnsureRuntimeFilesystemAccess(BootstrapConfig config)
    {
        if (!OperatingSystem.IsWindows() || !config.HostAgent.Enabled)
        {
            return;
        }

        var hostAgent = config.HostAgent;
        var webAccounts = ResolveWebRuntimeAccountNames(hostAgent)
            .SelectMany(ResolveWindowsAccountNameCandidates)
            .Where(static accountName => !IsBuiltInServiceIdentity(accountName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (webAccounts.Count == 0)
        {
            return;
        }

        Console.WriteLine("> Runtime filesystem access");
        var webReadPaths = new[]
            {
                hostAgent.PortalPhysicalPath,
                hostAgent.WebAppsRoot
            }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(EnumerateDirectoryAndLocalParents)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in webReadPaths)
        {
            EnsureDirectoryAccess(path, webAccounts, "RX", required: false);
        }

        if (!string.IsNullOrWhiteSpace(config.ArtifactStoreRoot))
        {
            EnsureDirectoryAccess(config.ArtifactStoreRoot, webAccounts, "M", required: false);
        }

        var hostAgentSettings = GetJsonObjectProperty(hostAgent.AppSettings, "HostAgent");
        var dataProtectionPath = GetJsonStringProperty(hostAgentSettings, "WebAppDataProtectionKeyPath");
        if (!string.IsNullOrWhiteSpace(dataProtectionPath))
        {
            EnsureDirectoryAccess(dataProtectionPath, webAccounts, "M", required: true);
        }
    }

    private static IEnumerable<string> ResolveRuntimeAccountNames(BootstrapConfig config)
    {
        var hostAgent = config.HostAgent;
        foreach (var accountName in ResolveWebRuntimeAccountNames(hostAgent))
        {
            yield return accountName;
        }

        yield return hostAgent.ServiceAccountName;
        yield return hostAgent.ServiceAppUserName;

        foreach (var identity in hostAgent.ServiceAppIdentityOverrides.Values)
        {
            yield return identity.UserName;
        }
    }

    private static IEnumerable<string> ResolveWebRuntimeAccountNames(HostAgentInstallOptions hostAgent)
    {
        yield return hostAgent.IisAppPoolUserName;
        foreach (var identity in hostAgent.IisAppPoolOverrides.Values)
        {
            yield return identity.UserName;
        }
    }

    private static IEnumerable<string> ResolveWindowsAccountNameCandidates(string? configuredAccountName)
    {
        if (string.IsNullOrWhiteSpace(configuredAccountName))
        {
            yield break;
        }

        var trimmed = configuredAccountName.Trim();
        var normalized = TryNormalizeDomainAccountName(trimmed);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            yield return normalized;
        }

        yield return trimmed;
    }

    private static string TryNormalizeDomainAccountName(string accountName)
    {
        var atIndex = accountName.IndexOf('@', StringComparison.Ordinal);
        if (atIndex > 0 && atIndex < accountName.Length - 1)
        {
            var userName = accountName[..atIndex];
            var domain = NormalizeNetBiosDomainName(accountName[(atIndex + 1)..]);
            return string.IsNullOrWhiteSpace(domain) ? string.Empty : domain + "\\" + userName;
        }

        var slashIndex = accountName.IndexOf('\\', StringComparison.Ordinal);
        if (slashIndex > 0 && slashIndex < accountName.Length - 1)
        {
            var domain = NormalizeNetBiosDomainName(accountName[..slashIndex]);
            return string.IsNullOrWhiteSpace(domain)
                ? string.Empty
                : domain + "\\" + accountName[(slashIndex + 1)..];
        }

        return string.Empty;
    }

    private static string NormalizeNetBiosDomainName(string domainName)
    {
        var trimmed = domainName.Trim();
        if (trimmed.Equals(".", StringComparison.Ordinal))
        {
            return ".";
        }

        var dotIndex = trimmed.IndexOf('.', StringComparison.Ordinal);
        var netBios = dotIndex > 0 ? trimmed[..dotIndex] : trimmed;
        return netBios.ToUpperInvariant();
    }

    private static bool IsBuiltInServiceIdentity(string accountName)
    {
        var normalized = accountName.Trim();
        return normalized.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("LocalService", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NetworkService", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("ApplicationPoolIdentity", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateDirectoryAndLocalParents(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            yield return fullPath;
            yield break;
        }

        var directory = new DirectoryInfo(fullPath);
        var root = directory.Root.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var stack = new Stack<string>();
        for (var current = directory; current is not null; current = current.Parent)
        {
            var currentPath = current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(currentPath, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            stack.Push(current.FullName);
        }

        while (stack.Count > 0)
        {
            yield return stack.Pop();
        }
    }

    private static void EnsureDirectoryAccess(
        string path,
        IReadOnlyCollection<string> accountNames,
        string permission,
        bool required)
    {
        Directory.CreateDirectory(path);

        var granted = false;
        foreach (var accountName in accountNames)
        {
            var result = RunProcess(
                "icacls.exe",
                [path, "/grant", $"{accountName}:(OI)(CI)({permission})"],
                throwOnFailure: false);
            if (result.ExitCode == 0)
            {
                granted = true;
                Console.WriteLine($"  {path}: granted {permission} to {accountName}.");
                break;
            }
        }

        if (!granted && required)
        {
            throw new InvalidOperationException(
                $"Could not grant {permission} access to '{path}' for any configured web runtime account.");
        }

        if (!granted)
        {
            Console.WriteLine($"  {path}: skipped; could not grant {permission} to any configured web runtime account.");
        }
    }

    private static async Task ImportModuleDefinitionsAsync(
        BootstrapConfig config,
        string payloadRoot,
        bool onlyNewerOrChanged = false,
        bool trustSameVersion = false)
    {
        var definitionsRoot = ResolvePackageModuleDefinitionsRoot(payloadRoot);
        if (!Directory.Exists(definitionsRoot))
        {
            return;
        }

        var definitionPaths = Directory.EnumerateFiles(definitionsRoot, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (definitionPaths.Count == 0)
        {
            return;
        }

        var configuredModuleKeys = ResolveConfiguredModuleKeys(config);
        Console.WriteLine("> Module definitions");

        await using var connection = new SqlConnection(BuildConnectionString(config.Sql, config.Sql.Database));
        await connection.OpenAsync();

        foreach (var definitionPath in definitionPaths)
        {
            var definition = await ReadModuleDefinitionAsync(definitionPath);
            if (configuredModuleKeys.Count > 0 && !configuredModuleKeys.Contains(definition.ModuleKey))
            {
                continue;
            }

            if (onlyNewerOrChanged)
            {
                var current = await QueryAppliedModuleDefinitionAsync(
                    connection,
                    definition.ModuleKey,
                    config.Sql.CommandTimeoutSeconds);
                if (current is not null
                    && CompareVersionText(definition.DefinitionVersion, current.DefinitionVersion) < 0)
                {
                    Console.WriteLine(
                        $"  {definition.ModuleKey} {definition.DefinitionVersion} skipped; installed definition {current.DefinitionVersion} is newer.");
                    continue;
                }

                if (current is not null
                    && CompareVersionText(definition.DefinitionVersion, current.DefinitionVersion) == 0
                    && trustSameVersion)
                {
                    Console.WriteLine(
                        $"  {definition.ModuleKey} {definition.DefinitionVersion} already applied; fast mode trusted the installed version and skipped same-version content checks.");
                    continue;
                }

                if (current is not null
                    && CompareVersionText(definition.DefinitionVersion, current.DefinitionVersion) == 0
                    && string.Equals(definition.DefinitionSha256, current.DefinitionSha256, StringComparison.OrdinalIgnoreCase))
                {
                    await ApplyModuleDefinitionCatalogMetadataAsync(
                        connection,
                        null,
                        current.ModuleDefinitionDocumentId,
                        config.Sql.CommandTimeoutSeconds);

                    var skippedRepairCount = await ExecuteModuleDefinitionSqlRepairsAsync(
                        connection,
                        config.Sql,
                        payloadRoot,
                        current.ModuleDefinitionDocumentId,
                        definition);
                    Console.WriteLine(
                        skippedRepairCount > 0
                            ? $"  {definition.ModuleKey} {definition.DefinitionVersion} already applied; executed {skippedRepairCount} SQL repair script(s)."
                            : $"  {definition.ModuleKey} {definition.DefinitionVersion} already applied; skipped.");
                    continue;
                }
            }

            int documentId;
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

            try
            {
                documentId = await UpsertModuleDefinitionDocumentAsync(
                    connection,
                    transaction,
                    definition,
                    Path.GetFileName(definitionPath),
                    config.Sql.CommandTimeoutSeconds);

                await ReplaceModuleDefinitionCompatibilityAsync(
                    connection,
                    transaction,
                    documentId,
                    definition.CompatibleArtifacts,
                    config.Sql.CommandTimeoutSeconds);

                await MarkOnlyModuleDefinitionAppliedAsync(
                    connection,
                    transaction,
                    definition.ModuleKey,
                    documentId,
                    config.Sql.CommandTimeoutSeconds);

                await ApplyModuleDefinitionCatalogMetadataAsync(
                    connection,
                    transaction,
                    documentId,
                    config.Sql.CommandTimeoutSeconds);

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            var repairCount = await ExecuteModuleDefinitionSqlRepairsAsync(
                connection,
                config.Sql,
                payloadRoot,
                documentId,
                definition);

            Console.WriteLine(
                repairCount > 0
                    ? $"  {definition.ModuleKey} {definition.DefinitionVersion}; executed {repairCount} SQL repair script(s)."
                    : $"  {definition.ModuleKey} {definition.DefinitionVersion}");
        }
    }

    private static IReadOnlySet<string> ResolveConfiguredModuleKeys(BootstrapConfig config)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in config.Artifacts.Where(static item => item.Enabled))
        {
            if (artifact.IsExample && !config.IncludeExampleApps)
            {
                continue;
            }

            var identity = ParseConfiguredArtifactIdentity(artifact.Source);
            if (identity is not null)
            {
                keys.Add(identity.ModuleKey);
            }
        }

        return keys;
    }

    private static async Task<AppliedModuleDefinition?> QueryAppliedModuleDefinitionAsync(
        SqlConnection connection,
        string moduleKey,
        int commandTimeoutSeconds)
    {
        const string sql = @"
SELECT TOP (1)
    ModuleDefinitionDocumentId,
    DefinitionVersion,
    DefinitionSha256
FROM omp.ModuleDefinitionDocuments
WHERE ModuleKey = @moduleKey
  AND IsApplied = 1
ORDER BY AppliedUtc DESC, UpdatedUtc DESC, ModuleDefinitionDocumentId DESC;";

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@moduleKey", moduleKey);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new AppliedModuleDefinition(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
    }

    private static async Task<ModuleDefinitionDocument> ReadModuleDefinitionAsync(string path)
    {
        var jsonText = await File.ReadAllTextAsync(path, Encoding.UTF8);
        var root = JsonNode.Parse(jsonText)
            ?? throw new InvalidOperationException($"Module definition file '{path}' is empty.");

        var moduleKey = GetJsonStringProperty(root, "moduleKey");
        var definitionVersion = GetJsonStringProperty(root, "definitionVersion");
        if (string.IsNullOrWhiteSpace(moduleKey) || string.IsNullOrWhiteSpace(definitionVersion))
        {
            throw new InvalidOperationException(
                $"Module definition file '{path}' must contain moduleKey and definitionVersion.");
        }

        var normalizedJson = root.ToJsonString(JsonOptions);
        var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedJson))).ToLowerInvariant();
        var compatibleArtifacts = ReadCompatibleArtifacts(root);

        return new ModuleDefinitionDocument(
            moduleKey,
            definitionVersion,
            GetJsonIntProperty(root, "formatVersion", 1),
            normalizedJson,
            sha256,
            compatibleArtifacts);
    }

    private static IReadOnlyList<ModuleDefinitionCompatibilityEntry> ReadCompatibleArtifacts(JsonNode root)
    {
        if (GetJsonObjectProperty(root, "compatibleArtifacts") is not JsonArray items)
        {
            return [];
        }

        var entries = new List<ModuleDefinitionCompatibilityEntry>();
        foreach (var item in items)
        {
            if (item is not JsonObject)
            {
                continue;
            }

            var appKey = GetJsonStringProperty(item, "appKey");
            var packageType = GetJsonStringProperty(item, "packageType");
            if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(packageType))
            {
                throw new InvalidOperationException("Each compatibleArtifacts item must contain appKey and packageType.");
            }

            entries.Add(
                new ModuleDefinitionCompatibilityEntry(
                    appKey,
                    packageType,
                    NullIfWhiteSpace(GetJsonStringProperty(item, "targetName")),
                    NullIfWhiteSpace(GetJsonStringProperty(item, "relativePathTemplate")),
                    NullIfWhiteSpace(GetJsonStringProperty(item, "minVersion")),
                    NullIfWhiteSpace(GetJsonStringProperty(item, "maxVersion"))));
        }

        return entries;
    }

    private static int CompareVersionText(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        var leftParts = left.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var index = 0; index < count; index++)
        {
            var leftPart = index < leftParts.Length ? leftParts[index] : "0";
            var rightPart = index < rightParts.Length ? rightParts[index] : "0";
            if (int.TryParse(leftPart, out var leftNumber) && int.TryParse(rightPart, out var rightNumber))
            {
                var numberComparison = leftNumber.CompareTo(rightNumber);
                if (numberComparison != 0)
                {
                    return numberComparison;
                }

                continue;
            }

            var textComparison = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            if (textComparison != 0)
            {
                return textComparison;
            }
        }

        return 0;
    }

    private sealed class VersionTextComparer : IComparer<string>
    {
        public static readonly VersionTextComparer Instance = new();

        public int Compare(string? x, string? y)
            => CompareVersionText(x ?? string.Empty, y ?? string.Empty);
    }

    private static async Task<int> UpsertModuleDefinitionDocumentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleDefinitionDocument definition,
        string sourceName,
        int commandTimeoutSeconds)
    {
        const string sql = @"
DECLARE @now datetime2(3) = SYSUTCDATETIME();

UPDATE omp.ModuleDefinitionDocuments
SET FormatVersion = @formatVersion,
    DefinitionJson = @definitionJson,
    DefinitionSha256 = @definitionSha256,
    SourceName = @sourceName,
    IsApplied = 1,
    AppliedUtc = @now,
    UpdatedUtc = @now
WHERE ModuleKey = @moduleKey
  AND DefinitionVersion = @definitionVersion;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO omp.ModuleDefinitionDocuments
    (
        ModuleKey,
        DefinitionVersion,
        FormatVersion,
        DefinitionJson,
        DefinitionSha256,
        SourceName,
        IsApplied,
        AppliedUtc
    )
    VALUES
    (
        @moduleKey,
        @definitionVersion,
        @formatVersion,
        @definitionJson,
        @definitionSha256,
        @sourceName,
        1,
        @now
    );

    SELECT CAST(SCOPE_IDENTITY() AS int);
    RETURN;
END;

SELECT ModuleDefinitionDocumentId
FROM omp.ModuleDefinitionDocuments
WHERE ModuleKey = @moduleKey
  AND DefinitionVersion = @definitionVersion;";

        await using var command = new SqlCommand(sql, connection, transaction);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@moduleKey", definition.ModuleKey);
        command.Parameters.AddWithValue("@definitionVersion", definition.DefinitionVersion);
        command.Parameters.AddWithValue("@formatVersion", definition.FormatVersion);
        command.Parameters.AddWithValue("@definitionJson", definition.DefinitionJson);
        command.Parameters.AddWithValue("@definitionSha256", definition.DefinitionSha256);
        command.Parameters.AddWithValue("@sourceName", sourceName);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task MarkOnlyModuleDefinitionAppliedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string moduleKey,
        int documentId,
        int commandTimeoutSeconds)
    {
        const string sql = @"
UPDATE omp.ModuleDefinitionDocuments
SET IsApplied = CASE WHEN ModuleDefinitionDocumentId = @documentId THEN CONVERT(bit, 1) ELSE CONVERT(bit, 0) END,
    AppliedUtc = CASE WHEN ModuleDefinitionDocumentId = @documentId THEN COALESCE(AppliedUtc, SYSUTCDATETIME()) ELSE AppliedUtc END,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ModuleKey = @moduleKey;";

        await using var command = new SqlCommand(sql, connection, transaction);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@moduleKey", moduleKey);
        command.Parameters.AddWithValue("@documentId", documentId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReplaceModuleDefinitionCompatibilityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int documentId,
        IReadOnlyList<ModuleDefinitionCompatibilityEntry> entries,
        int commandTimeoutSeconds)
    {
        const string deleteSql = @"
DELETE FROM omp.ModuleDefinitionArtifactCompatibility
WHERE ModuleDefinitionDocumentId = @documentId;";

        await using (var delete = new SqlCommand(deleteSql, connection, transaction))
        {
            delete.CommandTimeout = commandTimeoutSeconds;
            delete.Parameters.AddWithValue("@documentId", documentId);
            await delete.ExecuteNonQueryAsync();
        }

        const string insertSql = @"
INSERT INTO omp.ModuleDefinitionArtifactCompatibility
(
    ModuleDefinitionDocumentId,
    AppKey,
    PackageType,
    TargetName,
    RelativePathTemplate,
    MinArtifactVersion,
    MaxArtifactVersion
)
VALUES
(
    @documentId,
    @appKey,
    @packageType,
    @targetName,
    @relativePathTemplate,
    @minArtifactVersion,
    @maxArtifactVersion
);";

        foreach (var entry in entries)
        {
            await using var insert = new SqlCommand(insertSql, connection, transaction);
            insert.CommandTimeout = commandTimeoutSeconds;
            insert.Parameters.AddWithValue("@documentId", documentId);
            insert.Parameters.AddWithValue("@appKey", entry.AppKey);
            insert.Parameters.AddWithValue("@packageType", entry.PackageType);
            insert.Parameters.AddWithValue("@targetName", entry.TargetName is null ? DBNull.Value : entry.TargetName);
            insert.Parameters.AddWithValue("@relativePathTemplate", entry.RelativePathTemplate is null ? DBNull.Value : entry.RelativePathTemplate);
            insert.Parameters.AddWithValue("@minArtifactVersion", entry.MinArtifactVersion is null ? DBNull.Value : entry.MinArtifactVersion);
            insert.Parameters.AddWithValue("@maxArtifactVersion", entry.MaxArtifactVersion is null ? DBNull.Value : entry.MaxArtifactVersion);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task ApplyModuleDefinitionCatalogMetadataAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int documentId,
        int commandTimeoutSeconds)
    {
        const string sql = @"
DECLARE @DefinitionJson nvarchar(max);
DECLARE @ModuleKey nvarchar(100);
DECLARE @ModuleDisplayName nvarchar(200);
DECLARE @ModuleType nvarchar(50);
DECLARE @SchemaName nvarchar(128);
DECLARE @Description nvarchar(500);
DECLARE @SortOrder int;
DECLARE @IsEnabled bit;
DECLARE @ModuleId int;

SELECT @DefinitionJson = DefinitionJson,
       @ModuleKey = ModuleKey
FROM omp.ModuleDefinitionDocuments
WHERE ModuleDefinitionDocumentId = @documentId;

IF @DefinitionJson IS NULL
BEGIN
    THROW 53230, N'Module definition document was not found.', 1;
END;

SELECT @ModuleDisplayName = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.displayName'), N''),
       @ModuleType = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.moduleType'), N''),
       @SchemaName = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.schemaName'), N''),
       @Description = NULLIF(JSON_VALUE(@DefinitionJson, N'$.module.description'), N''),
       @SortOrder = TRY_CONVERT(int, JSON_VALUE(@DefinitionJson, N'$.module.sortOrder')),
       @IsEnabled = TRY_CONVERT(bit, JSON_VALUE(@DefinitionJson, N'$.module.isEnabled'));

MERGE omp.Modules AS target
USING
(
    SELECT @ModuleKey AS ModuleKey,
           COALESCE(@ModuleDisplayName, @ModuleKey) AS DisplayName,
           COALESCE(@ModuleType, N'WebAppModule') AS ModuleType,
           COALESCE(@SchemaName, @ModuleKey) AS SchemaName,
           @Description AS Description,
           COALESCE(@SortOrder, 0) AS SortOrder,
           COALESCE(@IsEnabled, CONVERT(bit, 1)) AS IsEnabled
) AS source
ON target.ModuleKey = source.ModuleKey
WHEN MATCHED THEN
    UPDATE SET DisplayName = source.DisplayName,
               ModuleType = source.ModuleType,
               SchemaName = source.SchemaName,
               Description = source.Description,
               SortOrder = source.SortOrder,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(ModuleKey, DisplayName, ModuleType, SchemaName, Description, SortOrder, IsEnabled)
    VALUES(source.ModuleKey, source.DisplayName, source.ModuleType, source.SchemaName, source.Description, source.SortOrder, source.IsEnabled);

SELECT @ModuleId = ModuleId
FROM omp.Modules
WHERE ModuleKey = @ModuleKey;

IF @ModuleId IS NOT NULL
BEGIN
    ;WITH AppRows AS
    (
        SELECT AppKey,
               COALESCE(NULLIF(DisplayName, N''), AppKey) AS DisplayName,
               COALESCE(NULLIF(AppType, N''), N'WebApp') AS AppType,
               COALESCE(AllowMultipleActiveInstances, CONVERT(bit, 0)) AS AllowMultipleActiveInstances,
               NULLIF(Description, N'') AS Description,
               COALESCE(SortOrder, 0) AS SortOrder,
               COALESCE(IsEnabled, CONVERT(bit, 1)) AS IsEnabled
        FROM OPENJSON(@DefinitionJson, N'$.apps')
        WITH
        (
            AppKey nvarchar(100) N'$.appKey',
            DisplayName nvarchar(200) N'$.displayName',
            AppType nvarchar(50) N'$.appType',
            AllowMultipleActiveInstances bit N'$.allowMultipleActiveInstances',
            Description nvarchar(500) N'$.description',
            SortOrder int N'$.sortOrder',
            IsEnabled bit N'$.isEnabled'
        )
        WHERE AppKey IS NOT NULL
    )
    MERGE omp.Apps AS target
    USING AppRows AS source
    ON target.ModuleId = @ModuleId
    AND target.AppKey = source.AppKey
    WHEN MATCHED THEN
        UPDATE SET DisplayName = source.DisplayName,
                   AppType = source.AppType,
                   AllowMultipleActiveInstances = source.AllowMultipleActiveInstances,
                   Description = source.Description,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(ModuleId, AppKey, DisplayName, AppType, AllowMultipleActiveInstances, Description, SortOrder, IsEnabled)
        VALUES(@ModuleId, source.AppKey, source.DisplayName, source.AppType, source.AllowMultipleActiveInstances, source.Description, source.SortOrder, source.IsEnabled);
END;

IF @ModuleId IS NOT NULL
BEGIN
    ;WITH RequiredModuleInstances AS
    (
        SELECT COALESCE(NULLIF(InstanceKey, N''), N'default') AS InstanceKey,
               COALESCE(NULLIF(ModuleInstanceKey, N''), @ModuleKey) AS ModuleInstanceKey,
               COALESCE(NULLIF(DisplayName, N''), @ModuleDisplayName, @ModuleKey) AS DisplayName,
               NULLIF(Description, N'') AS Description,
               COALESCE(SortOrder, @SortOrder, 0) AS SortOrder,
               COALESCE(IsEnabled, CONVERT(bit, 1)) AS IsEnabled
        FROM OPENJSON(@DefinitionJson, N'$.integrity.requiredOmpRows.moduleInstances')
        WITH
        (
            InstanceKey nvarchar(100) N'$.instanceKey',
            ModuleInstanceKey nvarchar(100) N'$.moduleInstanceKey',
            DisplayName nvarchar(200) N'$.displayName',
            Description nvarchar(500) N'$.description',
            SortOrder int N'$.sortOrder',
            IsEnabled bit N'$.isEnabled'
        )
        WHERE ModuleInstanceKey IS NOT NULL
    ),
    ResolvedModuleInstances AS
    (
        SELECT instance.InstanceId,
               @ModuleId AS ModuleId,
               source.ModuleInstanceKey,
               source.DisplayName,
               source.Description,
               source.SortOrder,
               source.IsEnabled
        FROM RequiredModuleInstances source
        INNER JOIN omp.Instances instance ON instance.InstanceKey = source.InstanceKey
    )
    MERGE omp.ModuleInstances AS target
    USING ResolvedModuleInstances AS source
    ON target.InstanceId = source.InstanceId
    AND target.ModuleInstanceKey = source.ModuleInstanceKey
    WHEN MATCHED THEN
        UPDATE SET ModuleId = source.ModuleId,
                   DisplayName = source.DisplayName,
                   Description = source.Description,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(ModuleInstanceId, InstanceId, ModuleId, ModuleInstanceKey, DisplayName, Description, IsEnabled, SortOrder)
        VALUES(NEWID(), source.InstanceId, source.ModuleId, source.ModuleInstanceKey, source.DisplayName, source.Description, source.IsEnabled, source.SortOrder);

    ;WITH RequiredTemplateModuleInstances AS
    (
        SELECT COALESCE(NULLIF(InstanceTemplateKey, N''), N'default') AS InstanceTemplateKey,
               COALESCE(NULLIF(ModuleInstanceKey, N''), @ModuleKey) AS ModuleInstanceKey,
               COALESCE(NULLIF(DisplayName, N''), @ModuleDisplayName, @ModuleKey) AS DisplayName,
               NULLIF(Description, N'') AS Description,
               COALESCE(SortOrder, @SortOrder, 0) AS SortOrder,
               COALESCE(IsEnabled, CONVERT(bit, 1)) AS IsEnabled
        FROM OPENJSON(@DefinitionJson, N'$.integrity.requiredOmpRows.instanceTemplateModuleInstances')
        WITH
        (
            InstanceTemplateKey nvarchar(100) N'$.instanceTemplateKey',
            ModuleInstanceKey nvarchar(100) N'$.moduleInstanceKey',
            DisplayName nvarchar(200) N'$.displayName',
            Description nvarchar(500) N'$.description',
            SortOrder int N'$.sortOrder',
            IsEnabled bit N'$.isEnabled'
        )
        WHERE ModuleInstanceKey IS NOT NULL
    ),
    ResolvedTemplateModuleInstances AS
    (
        SELECT template.InstanceTemplateId,
               @ModuleId AS ModuleId,
               source.ModuleInstanceKey,
               source.DisplayName,
               source.Description,
               source.SortOrder,
               source.IsEnabled
        FROM RequiredTemplateModuleInstances source
        INNER JOIN omp.InstanceTemplates template ON template.TemplateKey = source.InstanceTemplateKey
    )
    MERGE omp.InstanceTemplateModuleInstances AS target
    USING ResolvedTemplateModuleInstances AS source
    ON target.InstanceTemplateId = source.InstanceTemplateId
    AND target.ModuleInstanceKey = source.ModuleInstanceKey
    WHEN MATCHED THEN
        UPDATE SET ModuleId = source.ModuleId,
                   DisplayName = source.DisplayName,
                   Description = source.Description,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(InstanceTemplateId, ModuleId, ModuleInstanceKey, DisplayName, Description, SortOrder, IsEnabled)
        VALUES(source.InstanceTemplateId, source.ModuleId, source.ModuleInstanceKey, source.DisplayName, source.Description, source.SortOrder, source.IsEnabled);

    ;WITH RequiredAppInstances AS
    (
        SELECT COALESCE(NULLIF(InstanceKey, N''), N'default') AS InstanceKey,
               COALESCE(NULLIF(ModuleInstanceKey, N''), @ModuleKey) AS ModuleInstanceKey,
               AppInstanceKey,
               COALESCE(NULLIF(AppKey, N''), AppInstanceKey) AS AppKey,
               COALESCE(NULLIF(DisplayName, N''), AppInstanceKey) AS DisplayName,
               NULLIF(Description, N'') AS Description,
               NULLIF(RoutePath, N'') AS RoutePath,
               NULLIF(PublicUrl, N'') AS PublicUrl,
               NULLIF(InstallPath, N'') AS InstallPath,
               NULLIF(InstallationName, N'') AS InstallationName,
               NULLIF(HostBinding, N'') AS HostBinding,
               NULLIF(HostKey, N'') AS HostKey,
               NULLIF(COALESCE(TargetHostTemplateKey, HostTemplateKey), N'') AS TargetHostTemplateKey,
               NULLIF(PackageType, N'') AS PackageType,
               NULLIF(TargetName, N'') AS TargetName,
               COALESCE(DesiredState, CONVERT(tinyint, 1)) AS DesiredState,
               COALESCE(SortOrder, 0) AS SortOrder,
               COALESCE(IsEnabled, CONVERT(bit, 1)) AS IsEnabled,
               COALESCE(IsAllowed, CONVERT(bit, 1)) AS IsAllowed
        FROM OPENJSON(@DefinitionJson, N'$.integrity.requiredOmpRows.appInstances')
        WITH
        (
            InstanceKey nvarchar(100) N'$.instanceKey',
            ModuleInstanceKey nvarchar(100) N'$.moduleInstanceKey',
            AppInstanceKey nvarchar(100) N'$.appInstanceKey',
            AppKey nvarchar(100) N'$.appKey',
            DisplayName nvarchar(200) N'$.displayName',
            Description nvarchar(500) N'$.description',
            RoutePath nvarchar(256) N'$.routePath',
            PublicUrl nvarchar(500) N'$.publicUrl',
            InstallPath nvarchar(500) N'$.installPath',
            InstallationName nvarchar(150) N'$.installationName',
            HostBinding nvarchar(100) N'$.hostBinding',
            HostKey nvarchar(128) N'$.hostKey',
            TargetHostTemplateKey nvarchar(100) N'$.targetHostTemplateKey',
            HostTemplateKey nvarchar(100) N'$.hostTemplateKey',
            PackageType nvarchar(50) N'$.packageType',
            TargetName nvarchar(100) N'$.targetName',
            DesiredState tinyint N'$.desiredState',
            SortOrder int N'$.sortOrder',
            IsEnabled bit N'$.isEnabled',
            IsAllowed bit N'$.isAllowed'
        )
        WHERE AppInstanceKey IS NOT NULL
    ),
    ResolvedAppInstances AS
    (
        SELECT moduleInstance.ModuleInstanceId,
               host.HostId,
               targetTemplate.HostTemplateId AS TargetHostTemplateId,
               app.AppId,
               source.AppInstanceKey,
               COALESCE(NULLIF(source.DisplayName, source.AppInstanceKey), app.DisplayName) AS DisplayName,
               source.Description,
               source.RoutePath,
               source.PublicUrl,
               source.InstallPath,
               source.InstallationName,
               source.PackageType,
               source.TargetName,
               source.DesiredState,
               source.SortOrder,
               source.IsEnabled,
               source.IsAllowed,
               CASE
                   WHEN app.AppType IN (N'Portal', N'WebApp') THEN N'web-app'
                   WHEN app.AppType = N'ServiceApp' THEN N'service-app'
                   WHEN app.AppType = N'Worker' THEN N'worker'
                   WHEN app.AppType = N'HostAgent' THEN N'host-agent'
                   WHEN app.AppType = N'WorkerHost' THEN N'worker-host'
                   ELSE NULL
               END AS DefaultPackageType
        FROM RequiredAppInstances source
        INNER JOIN omp.Instances instance ON instance.InstanceKey = source.InstanceKey
        INNER JOIN omp.ModuleInstances moduleInstance
            ON moduleInstance.InstanceId = instance.InstanceId
           AND moduleInstance.ModuleInstanceKey = source.ModuleInstanceKey
        INNER JOIN omp.Apps app ON app.ModuleId = @ModuleId AND app.AppKey = source.AppKey
        LEFT JOIN omp.Hosts host
            ON host.InstanceId = instance.InstanceId
           AND host.HostKey = source.HostKey
        LEFT JOIN omp.HostTemplates targetTemplate ON targetTemplate.TemplateKey = source.TargetHostTemplateKey
        WHERE (source.HostKey IS NULL OR host.HostId IS NOT NULL)
          AND (source.TargetHostTemplateKey IS NULL OR targetTemplate.HostTemplateId IS NOT NULL)
          AND (source.HostBinding IS NULL
               OR source.HostBinding = N'host-neutral'
               OR source.HostKey IS NOT NULL
               OR source.TargetHostTemplateKey IS NOT NULL)
    ),
    AppInstancesWithArtifact AS
    (
        SELECT source.*,
               artifact.ArtifactId
        FROM ResolvedAppInstances source
        OUTER APPLY
        (
            SELECT TOP (1) item.ArtifactId
            FROM omp.Artifacts item
            WHERE item.AppId = source.AppId
              AND item.IsEnabled = 1
              AND item.PackageType = COALESCE(source.PackageType, source.DefaultPackageType)
              AND (source.TargetName IS NULL OR item.TargetName = source.TargetName)
            ORDER BY
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 4)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 3)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 2)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 1)), 0) DESC,
                item.Version DESC,
                item.ArtifactId DESC
        ) artifact
    )
    MERGE omp.AppInstances AS target
    USING AppInstancesWithArtifact AS source
    ON target.ModuleInstanceId = source.ModuleInstanceId
    AND target.AppInstanceKey = source.AppInstanceKey
    WHEN MATCHED THEN
        UPDATE SET HostId = source.HostId,
                   TargetHostTemplateId = source.TargetHostTemplateId,
                   AppId = source.AppId,
                   DisplayName = source.DisplayName,
                   Description = source.Description,
                   RoutePath = source.RoutePath,
                   PublicUrl = source.PublicUrl,
                   InstallPath = source.InstallPath,
                   InstallationName = source.InstallationName,
                   ArtifactId = COALESCE(source.ArtifactId, target.ArtifactId),
                   DesiredState = source.DesiredState,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   IsAllowed = source.IsAllowed,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(AppInstanceId, ModuleInstanceId, HostId, TargetHostTemplateId, AppId, AppInstanceKey, DisplayName, Description, RoutePath, PublicUrl, InstallPath, InstallationName, ArtifactId, IsEnabled, IsAllowed, DesiredState, SortOrder)
        VALUES(NEWID(), source.ModuleInstanceId, source.HostId, source.TargetHostTemplateId, source.AppId, source.AppInstanceKey, source.DisplayName, source.Description, source.RoutePath, source.PublicUrl, source.InstallPath, source.InstallationName, source.ArtifactId, source.IsEnabled, source.IsAllowed, source.DesiredState, source.SortOrder);

    ;WITH RequiredTemplateAppInstances AS
    (
        SELECT COALESCE(NULLIF(InstanceTemplateKey, N''), N'default') AS InstanceTemplateKey,
               COALESCE(NULLIF(ModuleInstanceKey, N''), @ModuleKey) AS ModuleInstanceKey,
               AppInstanceKey,
               COALESCE(NULLIF(AppKey, N''), AppInstanceKey) AS AppKey,
               COALESCE(NULLIF(DisplayName, N''), AppInstanceKey) AS DisplayName,
               NULLIF(Description, N'') AS Description,
               NULLIF(RoutePath, N'') AS RoutePath,
               NULLIF(PublicUrl, N'') AS PublicUrl,
               NULLIF(InstallPath, N'') AS InstallPath,
               NULLIF(InstallationName, N'') AS InstallationName,
               NULLIF(HostBinding, N'') AS HostBinding,
               NULLIF(HostKey, N'') AS HostKey,
               NULLIF(COALESCE(TargetHostTemplateKey, HostTemplateKey), N'') AS TargetHostTemplateKey,
               NULLIF(PackageType, N'') AS PackageType,
               NULLIF(TargetName, N'') AS TargetName,
               COALESCE(DesiredState, CONVERT(tinyint, 1)) AS DesiredState,
               COALESCE(SortOrder, 0) AS SortOrder,
               COALESCE(IsEnabled, CONVERT(bit, 1)) AS IsEnabled,
               COALESCE(IsAllowed, CONVERT(bit, 1)) AS IsAllowed
        FROM OPENJSON(@DefinitionJson, N'$.integrity.requiredOmpRows.instanceTemplateAppInstances')
        WITH
        (
            InstanceTemplateKey nvarchar(100) N'$.instanceTemplateKey',
            ModuleInstanceKey nvarchar(100) N'$.moduleInstanceKey',
            AppInstanceKey nvarchar(100) N'$.appInstanceKey',
            AppKey nvarchar(100) N'$.appKey',
            DisplayName nvarchar(200) N'$.displayName',
            Description nvarchar(500) N'$.description',
            RoutePath nvarchar(256) N'$.routePath',
            PublicUrl nvarchar(500) N'$.publicUrl',
            InstallPath nvarchar(500) N'$.installPath',
            InstallationName nvarchar(150) N'$.installationName',
            HostBinding nvarchar(100) N'$.hostBinding',
            HostKey nvarchar(128) N'$.hostKey',
            TargetHostTemplateKey nvarchar(100) N'$.targetHostTemplateKey',
            HostTemplateKey nvarchar(100) N'$.hostTemplateKey',
            PackageType nvarchar(50) N'$.packageType',
            TargetName nvarchar(100) N'$.targetName',
            DesiredState tinyint N'$.desiredState',
            SortOrder int N'$.sortOrder',
            IsEnabled bit N'$.isEnabled',
            IsAllowed bit N'$.isAllowed'
        )
        WHERE AppInstanceKey IS NOT NULL
    ),
    ResolvedTemplateAppInstances AS
    (
        SELECT templateModule.InstanceTemplateModuleInstanceId,
               templateHost.InstanceTemplateHostId,
               targetTemplate.HostTemplateId AS TargetHostTemplateId,
               app.AppId,
               source.AppInstanceKey,
               COALESCE(NULLIF(source.DisplayName, source.AppInstanceKey), app.DisplayName) AS DisplayName,
               source.Description,
               source.RoutePath,
               source.PublicUrl,
               source.InstallPath,
               source.InstallationName,
               source.PackageType,
               source.TargetName,
               source.DesiredState,
               source.SortOrder,
               source.IsEnabled,
               source.IsAllowed,
               CASE
                   WHEN app.AppType IN (N'Portal', N'WebApp') THEN N'web-app'
                   WHEN app.AppType = N'ServiceApp' THEN N'service-app'
                   WHEN app.AppType = N'Worker' THEN N'worker'
                   WHEN app.AppType = N'HostAgent' THEN N'host-agent'
                   WHEN app.AppType = N'WorkerHost' THEN N'worker-host'
                   ELSE NULL
               END AS DefaultPackageType
        FROM RequiredTemplateAppInstances source
        INNER JOIN omp.InstanceTemplates template ON template.TemplateKey = source.InstanceTemplateKey
        INNER JOIN omp.InstanceTemplateModuleInstances templateModule
            ON templateModule.InstanceTemplateId = template.InstanceTemplateId
           AND templateModule.ModuleInstanceKey = source.ModuleInstanceKey
        INNER JOIN omp.Apps app ON app.ModuleId = @ModuleId AND app.AppKey = source.AppKey
        LEFT JOIN omp.InstanceTemplateHosts templateHost
            ON templateHost.InstanceTemplateId = template.InstanceTemplateId
           AND templateHost.HostKey = source.HostKey
        LEFT JOIN omp.HostTemplates targetTemplate ON targetTemplate.TemplateKey = source.TargetHostTemplateKey
        WHERE (source.HostKey IS NULL OR templateHost.InstanceTemplateHostId IS NOT NULL)
          AND (source.TargetHostTemplateKey IS NULL OR targetTemplate.HostTemplateId IS NOT NULL)
          AND (source.HostBinding IS NULL
               OR source.HostBinding = N'host-neutral'
               OR source.HostKey IS NOT NULL
               OR source.TargetHostTemplateKey IS NOT NULL)
    ),
    TemplateAppInstancesWithArtifact AS
    (
        SELECT source.*,
               artifact.ArtifactId
        FROM ResolvedTemplateAppInstances source
        OUTER APPLY
        (
            SELECT TOP (1) item.ArtifactId
            FROM omp.Artifacts item
            WHERE item.AppId = source.AppId
              AND item.IsEnabled = 1
              AND item.PackageType = COALESCE(source.PackageType, source.DefaultPackageType)
              AND (source.TargetName IS NULL OR item.TargetName = source.TargetName)
            ORDER BY
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 4)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 3)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 2)), 0) DESC,
                COALESCE(TRY_CONVERT(int, PARSENAME(item.Version, 1)), 0) DESC,
                item.Version DESC,
                item.ArtifactId DESC
        ) artifact
    )
    MERGE omp.InstanceTemplateAppInstances AS target
    USING TemplateAppInstancesWithArtifact AS source
    ON target.InstanceTemplateModuleInstanceId = source.InstanceTemplateModuleInstanceId
    AND target.AppInstanceKey = source.AppInstanceKey
    WHEN MATCHED THEN
        UPDATE SET InstanceTemplateHostId = source.InstanceTemplateHostId,
                   TargetHostTemplateId = source.TargetHostTemplateId,
                   AppId = source.AppId,
                   DisplayName = source.DisplayName,
                   Description = source.Description,
                   RoutePath = source.RoutePath,
                   PublicUrl = source.PublicUrl,
                   InstallPath = source.InstallPath,
                   InstallationName = source.InstallationName,
                   DesiredArtifactId = COALESCE(source.ArtifactId, target.DesiredArtifactId),
                   DesiredState = source.DesiredState,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   IsAllowed = source.IsAllowed,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(InstanceTemplateModuleInstanceId, InstanceTemplateHostId, TargetHostTemplateId, AppId, AppInstanceKey, DisplayName, Description, RoutePath, PublicUrl, InstallPath, InstallationName, DesiredArtifactId, DesiredState, SortOrder, IsEnabled, IsAllowed)
        VALUES(source.InstanceTemplateModuleInstanceId, source.InstanceTemplateHostId, source.TargetHostTemplateId, source.AppId, source.AppInstanceKey, source.DisplayName, source.Description, source.RoutePath, source.PublicUrl, source.InstallPath, source.InstallationName, source.ArtifactId, source.DesiredState, source.SortOrder, source.IsEnabled, source.IsAllowed);
END;";

        await using var command = new SqlCommand(sql, connection);
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@documentId", documentId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecuteModuleDefinitionSqlRepairsAsync(
        SqlConnection connection,
        SqlBootstrapOptions sql,
        string payloadRoot,
        int documentId,
        ModuleDefinitionDocument definition)
    {
        var scripts = ReadPortableSqlScripts(definition.DefinitionJson)
            .OrderBy(static script => script.Order)
            .ThenBy(static script => script.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scripts.Count == 0)
        {
            return 0;
        }

        await AcquireModuleDefinitionSqlExecutionLockAsync(connection, sql.CommandTimeoutSeconds);

        var validationScripts = scripts.Where(IsValidationScript).ToList();
        if (validationScripts.Count > 0)
        {
            var needsRepair = false;
            foreach (var validationScript in validationScripts)
            {
                var originalValidationSql = ResolvePortableSqlText(validationScript)
                    ?? throw new InvalidOperationException(
                        $"Module definition validation script '{validationScript.Key}' has no SQL content.");
                var validationSha256 = ComputeTextSha256(originalValidationSql);
                if (!string.IsNullOrWhiteSpace(validationScript.Sha256)
                    && !string.Equals(validationScript.Sha256, validationSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Module definition validation script '{validationScript.Key}' content does not match its declared SHA-256.");
                }

                var validationSql = PreprocessSql(
                    originalValidationSql,
                    sql,
                    validationScript.Path ?? validationScript.Source ?? validationScript.Key,
                    payloadRoot);
                var validationSafety = ValidateReadOnlyModuleDefinitionSql(validationSql);
                if (validationSafety is not null)
                {
                    throw new InvalidOperationException(
                        $"Module definition validation script '{validationScript.Key}' was blocked: {validationSafety}");
                }

                try
                {
                    var validation = await ExecuteModuleDefinitionValidationSqlAsync(
                        connection,
                        validationSql,
                        sql.CommandTimeoutSeconds);
                    needsRepair = needsRepair || !validation.IsHealthy;
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    Console.WriteLine(
                        $"Validation script '{validationScript.Key}' failed and the module will be repaired: {ex.Message}");
                    needsRepair = true;
                }
            }

            if (!needsRepair)
            {
                return 0;
            }
        }

        var executed = 0;
        foreach (var script in scripts.Where(static script => !IsValidationScript(script)))
        {
            if (!string.Equals(script.Execution, "idempotent", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Module definition SQL script '{script.Key}' is not idempotent and cannot be executed by the bootstrapper.");
            }

            var originalSqlText = ResolvePortableSqlText(script);
            if (string.IsNullOrWhiteSpace(originalSqlText))
            {
                continue;
            }

            var scriptSha256 = ComputeTextSha256(originalSqlText);
            if (!string.IsNullOrWhiteSpace(script.Sha256)
                && !string.Equals(script.Sha256, scriptSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Module definition SQL script '{script.Key}' content does not match its declared SHA-256.");
            }

            var sqlText = PreprocessSql(
                originalSqlText,
                sql,
                script.Path ?? script.Source ?? script.Key,
                payloadRoot);
            var safety = ValidateSafeModuleDefinitionSql(sqlText);
            if (safety is not null)
            {
                throw new InvalidOperationException($"Module definition SQL script '{script.Key}' was blocked: {safety}");
            }

            var executionId = await InsertModuleDefinitionSqlExecutionAsync(
                connection,
                documentId,
                script,
                scriptSha256,
                sql.CommandTimeoutSeconds);

            try
            {
                await ExecuteModuleDefinitionSqlBatchesAsync(
                    connection,
                    sql,
                    sqlText,
                    $"module definition '{definition.ModuleKey}' script '{script.Key}'");
                await CompleteModuleDefinitionSqlExecutionAsync(
                    connection,
                    executionId,
                    "Succeeded",
                    null,
                    sql.CommandTimeoutSeconds);
                executed++;
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                await CompleteModuleDefinitionSqlExecutionAsync(
                    connection,
                    executionId,
                    "Failed",
                    ex.Message,
                    sql.CommandTimeoutSeconds);
                throw new InvalidOperationException($"Module definition SQL script '{script.Key}' failed: {ex.Message}", ex);
            }
        }

        return executed;
    }

    private static bool IsInstallerManagedModuleDefinitionSql(string definitionJson)
    {
        var root = JsonNode.Parse(definitionJson) as JsonObject;
        return root is not null
            && string.Equals(GetJsonStringProperty(root, "definitionType"), "platform-core", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<PortableModuleDefinitionSqlScript> ReadPortableSqlScripts(string definitionJson)
    {
        var root = JsonNode.Parse(definitionJson);
        if (GetJsonObjectProperty(root, "sqlScripts") is not JsonArray items)
        {
            return [];
        }

        var scripts = new List<PortableModuleDefinitionSqlScript>();
        foreach (var item in items.OfType<JsonObject>())
        {
            var key = GetJsonStringProperty(item, "key");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            scripts.Add(new PortableModuleDefinitionSqlScript(
                key,
                GetJsonStringProperty(item, "phase") is { Length: > 0 } phase ? phase : "setup",
                GetJsonStringProperty(item, "scope") is { Length: > 0 } scope ? scope : "module",
                GetJsonIntProperty(item, "order", 0),
                GetJsonStringProperty(item, "execution") is { Length: > 0 } execution ? execution : "idempotent",
                NullIfWhiteSpace(GetJsonStringProperty(item, "path")),
                NullIfWhiteSpace(GetJsonStringProperty(item, "source")),
                NullIfWhiteSpace(GetJsonStringProperty(item, "inlineSql")),
                NullIfWhiteSpace(GetJsonStringProperty(item, "contentEncoding")),
                NullIfWhiteSpace(GetJsonStringProperty(item, "content")),
                NullIfWhiteSpace(GetJsonStringProperty(item, "sha256"))));
        }

        return scripts;
    }

    private static bool IsValidationScript(PortableModuleDefinitionSqlScript script)
        => string.Equals(script.Phase, "validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Phase, "validation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Execution, "validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Execution, "validation", StringComparison.OrdinalIgnoreCase);

    private static string? ResolvePortableSqlText(PortableModuleDefinitionSqlScript script)
    {
        if (!string.IsNullOrWhiteSpace(script.InlineSql))
        {
            return script.InlineSql;
        }

        if (string.IsNullOrWhiteSpace(script.Content))
        {
            return null;
        }

        if (string.Equals(script.ContentEncoding, "base64-utf8", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(script.Content));
        }

        return script.Content;
    }

    private static string ComputeTextSha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task AcquireModuleDefinitionSqlExecutionLockAsync(
        SqlConnection connection,
        int commandTimeoutSeconds)
    {
        const string sql = @"
DECLARE @Result int;
EXEC @Result = sys.sp_getapplock
    @Resource = N'omp.module-definition-sql-repair',
    @LockMode = N'Exclusive',
    @LockOwner = N'Session',
    @LockTimeout = 0;
SELECT @Result;";

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = commandTimeoutSeconds;
        var result = Convert.ToInt32(await command.ExecuteScalarAsync());
        if (result < 0)
        {
            throw new InvalidOperationException("Another module definition SQL repair is already running.");
        }
    }

    private static async Task<long> InsertModuleDefinitionSqlExecutionAsync(
        SqlConnection connection,
        int documentId,
        PortableModuleDefinitionSqlScript script,
        string scriptSha256,
        int commandTimeoutSeconds)
    {
        const string sql = @"
INSERT INTO omp.ModuleDefinitionSqlExecutions
(
    ModuleDefinitionDocumentId,
    ScriptKey,
    ScriptPhase,
    ScriptOrder,
    ScriptSha256,
    ExecutionStatus
)
VALUES
(
    @documentId,
    @scriptKey,
    @scriptPhase,
    @scriptOrder,
    @scriptSha256,
    N'Running'
);

SELECT CAST(SCOPE_IDENTITY() AS bigint);";

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@documentId", documentId);
        command.Parameters.AddWithValue("@scriptKey", script.Key);
        command.Parameters.AddWithValue("@scriptPhase", script.Phase);
        command.Parameters.AddWithValue("@scriptOrder", script.Order);
        command.Parameters.AddWithValue("@scriptSha256", scriptSha256);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task CompleteModuleDefinitionSqlExecutionAsync(
        SqlConnection connection,
        long executionId,
        string status,
        string? errorMessage,
        int commandTimeoutSeconds)
    {
        const string sql = @"
UPDATE omp.ModuleDefinitionSqlExecutions
SET ExecutionStatus = @executionStatus,
    CompletedUtc = SYSUTCDATETIME(),
    ErrorMessage = @errorMessage
WHERE ModuleDefinitionSqlExecutionId = @executionId;";

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@executionId", executionId);
        command.Parameters.AddWithValue("@executionStatus", status);
        command.Parameters.AddWithValue("@errorMessage", Truncate(errorMessage ?? string.Empty, 4000));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteModuleDefinitionSqlBatchesAsync(
        SqlConnection connection,
        SqlBootstrapOptions sql,
        string sqlText,
        string sourceName)
    {
        var batchNumber = 0;
        foreach (var batch in SplitSqlBatches(sqlText))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            batchNumber++;
            await ExecuteSqlBatchWithRetryAsync(
                connection,
                batch,
                sql.CommandTimeoutSeconds,
                sourceName,
                sql.Database,
                batchNumber);
        }
    }

    private static async Task<ModuleDefinitionValidationResult> ExecuteModuleDefinitionValidationSqlAsync(
        SqlConnection connection,
        string sqlText,
        int commandTimeoutSeconds)
    {
        ModuleDefinitionValidationResult? result = null;
        foreach (var batch in SplitSqlBatches(sqlText))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var command = new SqlCommand(batch, connection)
            {
                CommandTimeout = commandTimeoutSeconds
            };
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                if (await reader.ReadAsync())
                {
                    result = ReadModuleDefinitionValidationResult(reader);
                }
            }
            while (await reader.NextResultAsync());
        }

        return result ?? new ModuleDefinitionValidationResult(
            false,
            "The validation script did not return a result row.");
    }

    private static ModuleDefinitionValidationResult ReadModuleDefinitionValidationResult(SqlDataReader reader)
    {
        var healthyOrdinal = TryGetOrdinal(reader, "IsHealthy") ?? 0;
        if (healthyOrdinal >= reader.FieldCount)
        {
            throw new InvalidOperationException("The validation result must contain an IsHealthy column or at least one column.");
        }

        var messageOrdinal = TryGetOrdinal(reader, "Message");
        var isHealthy = ConvertValidationBoolean(reader.GetValue(healthyOrdinal))
            ?? throw new InvalidOperationException("The validation result IsHealthy value must be true/false or 1/0.");
        var message = messageOrdinal.HasValue && !reader.IsDBNull(messageOrdinal.Value)
            ? Convert.ToString(reader.GetValue(messageOrdinal.Value))
            : null;

        return new ModuleDefinitionValidationResult(isHealthy, message);
    }

    private static int? TryGetOrdinal(SqlDataReader reader, string name)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (string.Equals(reader.GetName(index), name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }

    private static bool? ConvertValidationBoolean(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        if (value is bool boolean)
        {
            return boolean;
        }

        if (value is byte or short or int or long or decimal)
        {
            return Convert.ToDecimal(value) != 0m;
        }

        var text = Convert.ToString(value)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.ToLowerInvariant() switch
        {
            "1" or "true" or "ok" or "healthy" or "pass" or "passed" => true,
            "0" or "false" or "error" or "unhealthy" or "fail" or "failed" => false,
            _ => null
        };
    }

    private static string? ValidateSafeModuleDefinitionSql(string sqlText)
    {
        if (ModuleDefinitionUseDatabaseDirectiveRegex().IsMatch(sqlText))
        {
            return "Module definition SQL must not contain USE database directives.";
        }

        if (ModuleDefinitionDropObjectRegex().IsMatch(sqlText))
        {
            return "The script contains DROP DATABASE, DROP SCHEMA, or DROP TABLE.";
        }

        if (ModuleDefinitionTruncateTableRegex().IsMatch(sqlText))
        {
            return "The script contains TRUNCATE TABLE.";
        }

        var unsafeDeleteStatement = ModuleDefinitionDeleteStatementRegex().Matches(sqlText)
            .Cast<Match>()
            .Select(static match => match.Groups["statement"].Value)
            .FirstOrDefault(static statement => !ModuleDefinitionWhereClauseRegex().IsMatch(statement));
        if (unsafeDeleteStatement is not null)
        {
            return "The script contains DELETE without a WHERE clause.";
        }

        return null;
    }

    private static string? ValidateReadOnlyModuleDefinitionSql(string sqlText)
    {
        var safety = ValidateSafeModuleDefinitionSql(sqlText);
        if (safety is not null)
        {
            return safety;
        }

        return ModuleDefinitionReadOnlyBlockedCommandRegex().IsMatch(sqlText)
            ? "Validation SQL must be read-only and return an IsHealthy result."
            : null;
    }

    private static async Task EnsureDatabaseAsync(SqlBootstrapOptions sql)
    {
        Console.WriteLine($"> SQL ensure database {sql.Database}");
        var createSql = """
DECLARE @DatabaseName sysname = @databaseName;
DECLARE @sql nvarchar(max);

IF DB_ID(@DatabaseName) IS NULL
BEGIN
    SET @sql = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName);
    EXEC sys.sp_executesql @sql;
END
""";

        await using var connection = new SqlConnection(BuildConnectionString(sql, "master"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = createSql;
        command.CommandTimeout = sql.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("@databaseName", sql.Database.Trim());
        await command.ExecuteNonQueryAsync();
    }

    private static string ReadSqlFile(
        string path,
        SqlBootstrapOptions options,
        string payloadRoot,
        bool includeExampleApps,
        HashSet<string> includeStack)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("SQL script was not found.", fullPath);
        }

        if (!includeStack.Add(fullPath))
        {
            throw new InvalidOperationException($"Recursive SQL include detected: {fullPath}");
        }

        try
        {
            var builder = new StringBuilder();
            foreach (var line in File.ReadLines(fullPath, Encoding.UTF8))
            {
                var include = SqlCmdIncludeRegex().Match(line);
                if (include.Success)
                {
                    var includePath = include.Groups["path"].Value.Trim().Trim('"');
                    var resolvedInclude = Path.GetFullPath(Path.Join(Path.GetDirectoryName(fullPath)!, includePath));
                    if (!includeExampleApps && IsExampleSqlPath(resolvedInclude, payloadRoot))
                    {
                        continue;
                    }

                    builder.AppendLine(ReadSqlFile(resolvedInclude, options, payloadRoot, includeExampleApps, includeStack));
                    continue;
                }

                builder.AppendLine(line);
            }

            return PreprocessSql(builder.ToString(), options, fullPath, payloadRoot);
        }
        finally
        {
            includeStack.Remove(fullPath);
        }
    }

    private static string PreprocessSql(
        string sqlText,
        SqlBootstrapOptions options,
        string scriptPath,
        string payloadRoot)
    {
        var result = UseDatabaseRegex().Replace(
            sqlText,
            "USE " + ConvertToSqlBracketName(options.Database));

        result = PatchBootstrapPrincipal(result, options);

        var artifactVersion = ResolveArtifactVersionOverride(options, scriptPath, payloadRoot);
        if (!string.IsNullOrWhiteSpace(artifactVersion))
        {
            var versionLiteral = ConvertToSqlUnicodeLiteral(artifactVersion);
            result = ArtifactVersionDeclarationRegex().Replace(
                result,
                $"DECLARE @ArtifactVersion nvarchar(50) = {versionLiteral};",
                1);
        }

        foreach (var item in ResolveArtifactVersionVariableOverrides(options, scriptPath, payloadRoot))
        {
            result = PatchSqlNVarCharDeclaration(result, item.Key, item.Value);
        }

        return result;
    }

    private static bool IsExampleSqlPath(string path, string payloadRoot)
    {
        var relative = NormalizePathForMatch(Path.GetRelativePath(payloadRoot, path));
        return relative.StartsWith("sql/examples/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("examples/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/examples/", StringComparison.OrdinalIgnoreCase);
    }

    private static string PatchBootstrapPrincipal(string sqlText, SqlBootstrapOptions options)
    {
        if (!sqlText.Contains(BootstrapPrincipalPlaceholder, StringComparison.Ordinal))
        {
            return sqlText;
        }

        if (string.IsNullOrWhiteSpace(options.BootstrapPortalAdminPrincipal))
        {
            throw new InvalidOperationException("Sql:BootstrapPortalAdminPrincipal must be configured for bootstrap scripts.");
        }

        var principalLiteral = ConvertToSqlUnicodeLiteral(options.BootstrapPortalAdminPrincipal.Trim());
        var principalType = string.IsNullOrWhiteSpace(options.BootstrapPortalAdminPrincipalType)
            ? "ADUser"
            : options.BootstrapPortalAdminPrincipalType.Trim();
        var principalTypeLiteral = ConvertToSqlUnicodeLiteral(principalType);

        var result = BootstrapPrincipalDeclarationRegex().Replace(
            sqlText,
            $"DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = {principalLiteral};");

        return BootstrapPrincipalTypeDeclarationRegex().Replace(
            result,
            $"DECLARE @BootstrapPortalAdminPrincipalType nvarchar(50) = {principalTypeLiteral};");
    }

    private static string? ResolveArtifactVersionOverride(
        SqlBootstrapOptions options,
        string scriptPath,
        string payloadRoot)
    {
        if (options.ArtifactVersionOverrides.Count == 0)
        {
            return null;
        }

        var relative = NormalizeScriptPathForOverrideMatch(scriptPath, payloadRoot);
        foreach (var item in options.ArtifactVersionOverrides)
        {
            var key = NormalizePathForMatch(item.Key);
            var keyFileName = Path.GetFileName(key);
            if (relative.Equals(key, StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(relative).Equals(key, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(relative).Equals(keyFileName, StringComparison.OrdinalIgnoreCase))
            {
                return item.Value;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> ResolveArtifactVersionVariableOverrides(
        SqlBootstrapOptions options,
        string scriptPath,
        string payloadRoot)
    {
        if (options.ArtifactVersionVariableOverrides.Count == 0)
        {
            return EmptyStringDictionary;
        }

        var relative = NormalizeScriptPathForOverrideMatch(scriptPath, payloadRoot);
        foreach (var item in options.ArtifactVersionVariableOverrides)
        {
            var key = NormalizePathForMatch(item.Key);
            var keyFileName = Path.GetFileName(key);
            if (relative.Equals(key, StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(relative).Equals(key, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(relative).Equals(keyFileName, StringComparison.OrdinalIgnoreCase))
            {
                return item.Value;
            }
        }

        return EmptyStringDictionary;
    }

    private static string NormalizeScriptPathForOverrideMatch(string scriptPath, string payloadRoot)
    {
        if (Path.IsPathFullyQualified(scriptPath))
        {
            return NormalizePathForMatch(Path.GetRelativePath(payloadRoot, scriptPath));
        }

        return NormalizePathForMatch(scriptPath);
    }

    private static string PatchSqlNVarCharDeclaration(
        string sqlText,
        string variableName,
        string value)
    {
        if (string.IsNullOrWhiteSpace(variableName) || string.IsNullOrWhiteSpace(value))
        {
            return sqlText;
        }

        var sanitizedVariableName = variableName.Trim().TrimStart('@');
        var pattern = @"(?im)^\s*DECLARE\s+@" + Regex.Escape(sanitizedVariableName) + @"\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';\s*$";
        var replacement = $"DECLARE @{sanitizedVariableName} nvarchar(50) = {ConvertToSqlUnicodeLiteral(value)};";

        return Regex.Replace(sqlText, pattern, replacement, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    private static async Task ExecuteSqlBatchesAsync(
        SqlBootstrapOptions sql,
        string database,
        string sqlText,
        string sourceName)
    {
        await using var connection = new SqlConnection(BuildConnectionString(sql, database));
        await connection.OpenAsync();

        var batchNumber = 0;
        foreach (var batch in SplitSqlBatches(sqlText))
        {
            batchNumber++;
            await ExecuteSqlBatchWithRetryAsync(connection, batch, sql.CommandTimeoutSeconds, sourceName, database, batchNumber);
        }
    }

    private static async Task ExecuteSqlBatchWithRetryAsync(
        SqlConnection connection,
        string batch,
        int commandTimeoutSeconds,
        string sourceName,
        string database,
        int batchNumber)
    {
        for (var attempt = 1; attempt <= SqlDeadlockRetryCount + 1; attempt++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = commandTimeoutSeconds;

            try
            {
                await command.ExecuteNonQueryAsync();
                return;
            }
            catch (SqlException ex) when (IsDeadlock(ex) && attempt <= SqlDeadlockRetryCount)
            {
                var delay = TimeSpan.FromSeconds(attempt * 2);
                Console.WriteLine(
                    $"> SQL deadlock in batch {batchNumber}; retrying attempt {attempt}/{SqlDeadlockRetryCount} after {delay.TotalSeconds:n0}s.");
                await Task.Delay(delay);
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException(
                    $"SQL failed in '{sourceName}' batch {batchNumber} on database '{database}'. {ex.Message}",
                    ex);
            }
        }
    }

    private static bool IsDeadlock(SqlException ex)
        => ex.Errors.Cast<SqlError>().Any(static error => error.Number == SqlDeadlockErrorNumber);

    private static IEnumerable<string> SplitSqlBatches(string sqlText)
    {
        using var reader = new StringReader(sqlText);
        var builder = new StringBuilder();

        while (reader.ReadLine() is { } line)
        {
            var match = GoBatchRegex().Match(line);
            if (match.Success)
            {
                var batch = builder.ToString();
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    var repeat = 1;
                    if (match.Groups["repeat"].Success
                        && !int.TryParse(match.Groups["repeat"].Value, out repeat))
                    {
                        throw new InvalidOperationException($"Invalid GO repeat count: {match.Groups["repeat"].Value}");
                    }

                    for (var i = 0; i < repeat; i++)
                    {
                        yield return batch;
                    }
                }

                builder.Clear();
                continue;
            }

            builder.AppendLine(line);
        }

        var lastBatch = builder.ToString();
        if (!string.IsNullOrWhiteSpace(lastBatch))
        {
            yield return lastBatch;
        }
    }

    private static string BuildConnectionString(SqlBootstrapOptions sql, string database)
    {
        if (string.IsNullOrWhiteSpace(sql.Server))
        {
            throw new InvalidOperationException("Sql:Server must be configured.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = sql.Server.Trim(),
            InitialCatalog = database.Trim(),
            TrustServerCertificate = sql.TrustServerCertificate
        };

        if (sql.IntegratedSecurity)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(sql.UserId))
            {
                throw new InvalidOperationException("Sql:UserId must be configured when IntegratedSecurity is false.");
            }

            builder.IntegratedSecurity = false;
            builder.UserID = sql.UserId;
            builder.Password = sql.Password ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    private static async Task RemoveDatabaseObjectsAsync(SqlBootstrapOptions sql)
    {
        if (string.IsNullOrWhiteSpace(sql.Database))
        {
            throw new InvalidOperationException("Sql:Database must be configured before database object cleanup.");
        }

        Console.WriteLine($"> SQL remove all user objects from {sql.Server}/{sql.Database}");
        await ExecuteSqlBatchesAsync(
            sql,
            sql.Database,
            CreateDropDatabaseObjectsSql(),
            "OpenModulePlatform database object cleanup");
    }

    private static string CreateDropDatabaseObjectsSql()
        => """
SET NOCOUNT ON;

-- Full uninstall removes user-created database objects but deliberately leaves
-- the configured database itself in place so ownership, files, and SQL Server
-- permissions remain under operator control.
DECLARE @MaxDropPasses int = 15;
DECLARE @Pass int = 0;
DECLARE @Remaining int = 1;
DECLARE @sql nvarchar(max);
DECLARE @schemaName sysname;
DECLARE @objectName sysname;
DECLARE @constraintName sysname;
DECLARE @type char(2);

WHILE @Pass < @MaxDropPasses AND @Remaining > 0
BEGIN
    SET @Pass += 1;

    DECLARE foreign_key_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT
        SCHEMA_NAME(parent.schema_id),
        parent.name,
        fk.name
    FROM sys.foreign_keys AS fk
    JOIN sys.tables AS parent ON parent.object_id = fk.parent_object_id;

    OPEN foreign_key_cursor;
    FETCH NEXT FROM foreign_key_cursor INTO @schemaName, @objectName, @constraintName;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            SET @sql = N'ALTER TABLE ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@objectName)
                + N' DROP CONSTRAINT ' + QUOTENAME(@constraintName) + N';';
            EXEC sys.sp_executesql @sql;
        END TRY
        BEGIN CATCH
            -- Dependencies can disappear in later passes. The final check below
            -- reports any remaining objects after all attempts have been made.
        END CATCH;

        FETCH NEXT FROM foreign_key_cursor INTO @schemaName, @objectName, @constraintName;
    END
    CLOSE foreign_key_cursor;
    DEALLOCATE foreign_key_cursor;

    DECLARE object_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT s.name, o.name, o.type
    FROM sys.objects AS o
    JOIN sys.schemas AS s ON s.schema_id = o.schema_id
    WHERE s.name NOT IN (N'sys', N'INFORMATION_SCHEMA')
      AND o.is_ms_shipped = 0
      AND o.type IN ('V', 'P', 'TR', 'FN', 'IF', 'TF', 'U')
    ORDER BY CASE o.type
        WHEN 'V' THEN 1
        WHEN 'P' THEN 2
        WHEN 'TR' THEN 3
        WHEN 'FN' THEN 4
        WHEN 'IF' THEN 5
        WHEN 'TF' THEN 6
        WHEN 'U' THEN 7
        ELSE 8
    END, s.name, o.name;

    OPEN object_cursor;
    FETCH NEXT FROM object_cursor INTO @schemaName, @objectName, @type;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            SET @sql = CASE @type
                WHEN 'V' THEN N'DROP VIEW '
                WHEN 'P' THEN N'DROP PROCEDURE '
                WHEN 'TR' THEN N'DROP TRIGGER '
                WHEN 'U' THEN N'DROP TABLE '
                ELSE N'DROP FUNCTION '
            END + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@objectName) + N';';
            EXEC sys.sp_executesql @sql;
        END TRY
        BEGIN CATCH
        END CATCH;

        FETCH NEXT FROM object_cursor INTO @schemaName, @objectName, @type;
    END
    CLOSE object_cursor;
    DEALLOCATE object_cursor;

    DECLARE sequence_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT s.name, seq.name
    FROM sys.sequences AS seq
    JOIN sys.schemas AS s ON s.schema_id = seq.schema_id
    WHERE s.name NOT IN (N'sys', N'INFORMATION_SCHEMA')
    ORDER BY s.name, seq.name;

    OPEN sequence_cursor;
    FETCH NEXT FROM sequence_cursor INTO @schemaName, @objectName;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            SET @sql = N'DROP SEQUENCE ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@objectName) + N';';
            EXEC sys.sp_executesql @sql;
        END TRY
        BEGIN CATCH
        END CATCH;

        FETCH NEXT FROM sequence_cursor INTO @schemaName, @objectName;
    END
    CLOSE sequence_cursor;
    DEALLOCATE sequence_cursor;

    DECLARE synonym_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT s.name, syn.name
    FROM sys.synonyms AS syn
    JOIN sys.schemas AS s ON s.schema_id = syn.schema_id
    WHERE s.name NOT IN (N'sys', N'INFORMATION_SCHEMA')
    ORDER BY s.name, syn.name;

    OPEN synonym_cursor;
    FETCH NEXT FROM synonym_cursor INTO @schemaName, @objectName;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            SET @sql = N'DROP SYNONYM ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@objectName) + N';';
            EXEC sys.sp_executesql @sql;
        END TRY
        BEGIN CATCH
        END CATCH;

        FETCH NEXT FROM synonym_cursor INTO @schemaName, @objectName;
    END
    CLOSE synonym_cursor;
    DEALLOCATE synonym_cursor;

    DECLARE schema_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT s.name
    FROM sys.schemas AS s
    WHERE s.name NOT IN (N'dbo', N'guest', N'sys', N'INFORMATION_SCHEMA')
      AND s.principal_id <> DATABASE_PRINCIPAL_ID(N'sys')
    ORDER BY s.name;

    OPEN schema_cursor;
    FETCH NEXT FROM schema_cursor INTO @schemaName;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            IF NOT EXISTS (
                SELECT 1
                FROM sys.objects
                WHERE schema_id = SCHEMA_ID(@schemaName)
            )
            BEGIN
                SET @sql = N'DROP SCHEMA ' + QUOTENAME(@schemaName) + N';';
                EXEC sys.sp_executesql @sql;
            END
        END TRY
        BEGIN CATCH
        END CATCH;

        FETCH NEXT FROM schema_cursor INTO @schemaName;
    END
    CLOSE schema_cursor;
    DEALLOCATE schema_cursor;

    SELECT @Remaining =
        (SELECT COUNT(*)
         FROM sys.objects AS o
         JOIN sys.schemas AS s ON s.schema_id = o.schema_id
         WHERE s.name NOT IN (N'sys', N'INFORMATION_SCHEMA')
           AND o.is_ms_shipped = 0)
        +
        (SELECT COUNT(*)
         FROM sys.sequences AS seq
         JOIN sys.schemas AS s ON s.schema_id = seq.schema_id
         WHERE s.name NOT IN (N'sys', N'INFORMATION_SCHEMA'))
        +
        (SELECT COUNT(*)
         FROM sys.synonyms AS syn
         JOIN sys.schemas AS s ON s.schema_id = syn.schema_id
         WHERE s.name NOT IN (N'sys', N'INFORMATION_SCHEMA'))
        +
        (SELECT COUNT(*)
         FROM sys.schemas AS s
         WHERE s.name NOT IN (N'dbo', N'guest', N'sys', N'INFORMATION_SCHEMA')
           AND s.principal_id <> DATABASE_PRINCIPAL_ID(N'sys'));
END;

IF @Remaining > 0
BEGIN
    DECLARE @message nvarchar(2048) =
        N'Database object cleanup did not remove every user object after '
        + CONVERT(nvarchar(10), @MaxDropPasses)
        + N' passes. Remove remaining dependencies manually and retry.';
    THROW 53220, @message, 1;
END;
""";

    private static IReadOnlyList<PreparedArtifactConfigurationFiles> PrepareArtifacts(
        BootstrapConfig config,
        string configPath,
        string payloadRoot,
        ArtifactPreparationMode mode)
    {
        if (string.IsNullOrWhiteSpace(config.ArtifactStoreRoot))
        {
            throw new InvalidOperationException("ArtifactStoreRoot must be configured.");
        }

        var artifactStoreRoot = Path.GetFullPath(config.ArtifactStoreRoot.Trim());
        Directory.CreateDirectory(artifactStoreRoot);
        var preparedConfigurationFiles = new List<PreparedArtifactConfigurationFiles>();

        foreach (var artifact in config.Artifacts.Where(static item => item.Enabled))
        {
            if (artifact.IsExample && !config.IncludeExampleApps)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(artifact.Source) || string.IsNullOrWhiteSpace(artifact.Target))
            {
                throw new InvalidOperationException("Artifacts contains an enabled entry without Source or Target.");
            }

            var source = ResolvePackageDataPath(payloadRoot, configPath, artifact.Source);
            var target = CombineUnderRoot(artifactStoreRoot, artifact.Target);
            if (mode == ArtifactPreparationMode.AddMissingOnly
                && (File.Exists(target) || Directory.Exists(target)))
            {
                if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var configurationFiles = ExtractArtifactPackageConfigurationFiles(
                        source,
                        artifactStoreRoot);
                    if (configurationFiles.Count > 0)
                    {
                        preparedConfigurationFiles.Add(new PreparedArtifactConfigurationFiles(
                            artifact.Target,
                            configurationFiles));
                    }
                }

                Console.WriteLine($"> Artifact {artifact.Target} already exists; skipped.");
                continue;
            }

            Console.WriteLine($"> Artifact {artifact.Target}");

            if (Directory.Exists(source))
            {
                if (artifact.Overwrite && (File.Exists(target) || Directory.Exists(target)))
                {
                    TryDeleteFileOrDirectory(target);
                }

                CopyDirectory(source, target);
            }
            else if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var stagingPath = Path.Join(
                    artifactStoreRoot,
                    ".bootstrapper-artifact-staging",
                    Guid.NewGuid().ToString("N"));

                try
                {
                    var package = new ArtifactPackageExtractor()
                        .Extract(source, stagingPath);

                    if (artifact.Overwrite && (File.Exists(target) || Directory.Exists(target)))
                    {
                        TryDeleteFileOrDirectory(target);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.Move(package.ArtifactContentPath, target);

                    if (package.ConfigurationFiles.Count > 0)
                    {
                        preparedConfigurationFiles.Add(new PreparedArtifactConfigurationFiles(
                            artifact.Target,
                            package.ConfigurationFiles));
                    }
                }
                finally
                {
                    TryDeleteDirectory(stagingPath);
                }
            }
            else if (File.Exists(source))
            {
                if (artifact.Overwrite && (File.Exists(target) || Directory.Exists(target)))
                {
                    TryDeleteFileOrDirectory(target);
                }

                Directory.CreateDirectory(target);
                File.Copy(source, Path.Join(target, Path.GetFileName(source)), overwrite: true);
            }
            else
            {
                throw new FileNotFoundException("Artifact payload was not found.", source);
            }

            if (artifact.RemoveRuntimeConfigurationFiles)
            {
                RemoveRuntimeConfigurationFiles(target);
            }
        }

        return preparedConfigurationFiles;
    }

    private static IReadOnlyList<string> SelectLatestAvailableArtifactPackages(
        BootstrapConfig config,
        string payloadRoot)
    {
        var availablePackages = DiscoverAvailableArtifactPackages(payloadRoot);
        if (availablePackages.Count == 0)
        {
            return [];
        }

        var messages = new List<string>();
        foreach (var artifact in config.Artifacts.Where(static item => item.Enabled))
        {
            var currentIdentity = ParseConfiguredArtifactIdentity(artifact.Source);
            if (currentIdentity is null)
            {
                continue;
            }

            var latest = FindLatestAvailableArtifactPackage(availablePackages, currentIdentity);
            if (latest is null)
            {
                continue;
            }

            if (CompareVersionText(latest.Identity.Version, currentIdentity.Version) <= 0
                && File.Exists(ResolvePackageDataPath(payloadRoot, artifact.Source)))
            {
                continue;
            }

            if (!TryReplaceArtifactTargetVersion(
                    artifact.Target,
                    currentIdentity.Version,
                    latest.Identity.Version,
                    out var latestTarget))
            {
                messages.Add(
                    $"> Artifact {artifact.Target}: kept configured version {currentIdentity.Version}; could not map target path to latest available version {latest.Identity.Version}.");
                continue;
            }

            var oldSource = artifact.Source;
            var oldTarget = artifact.Target;
            artifact.Source = latest.PackageRelativePath;
            artifact.Target = latestTarget;
            messages.Add($"> Artifact {oldTarget}: selected latest available package {artifact.Target} from {Path.GetFileName(latest.PackageRelativePath)}.");

            if (IsHostAgentArtifact(currentIdentity)
                && (string.IsNullOrWhiteSpace(config.HostAgent.PackagePath)
                    || string.Equals(NormalizePathForMatch(config.HostAgent.PackagePath), NormalizePathForMatch(oldSource), StringComparison.OrdinalIgnoreCase)
                    || CompareVersionText(latest.Identity.Version, currentIdentity.Version) > 0))
            {
                config.HostAgent.PackagePath = latest.PackageRelativePath;
                messages.Add($"> HostAgent package path: selected latest available package {Path.GetFileName(latest.PackageRelativePath)}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.HostAgent.PackagePath))
        {
            var currentHostAgentIdentity = ParseConfiguredArtifactIdentity(config.HostAgent.PackagePath);
            if (currentHostAgentIdentity is not null && IsHostAgentArtifact(currentHostAgentIdentity))
            {
                var latestHostAgent = FindLatestAvailableArtifactPackage(availablePackages, currentHostAgentIdentity);
                if (latestHostAgent is not null
                    && CompareVersionText(latestHostAgent.Identity.Version, currentHostAgentIdentity.Version) > 0)
                {
                    config.HostAgent.PackagePath = latestHostAgent.PackageRelativePath;
                    messages.Add($"> HostAgent package path: selected latest available package {Path.GetFileName(latestHostAgent.PackageRelativePath)}.");
                }
            }
        }

        return messages;
    }

    private static void WriteArtifactSelectionMessages(IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        Console.WriteLine("> Latest available artifact selection");
        foreach (var message in messages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(message);
        }

        Console.WriteLine();
    }

    private static IReadOnlyList<AvailableArtifactPackage> DiscoverAvailableArtifactPackages(string payloadRoot)
    {
        var artifactRoot = ResolvePackageArtifactsRoot(payloadRoot);
        if (!Directory.Exists(artifactRoot))
        {
            return [];
        }

        var packages = new List<AvailableArtifactPackage>();
        foreach (var packagePath in Directory.EnumerateFiles(artifactRoot, "*.zip", SearchOption.TopDirectoryOnly))
        {
            var packageRelativePath = NormalizePathForMatch(Path.GetRelativePath(payloadRoot, packagePath));
            var identity = ParseConfiguredArtifactIdentity(packageRelativePath);
            if (identity is null)
            {
                continue;
            }

            packages.Add(new AvailableArtifactPackage(identity, packageRelativePath));
        }

        return packages;
    }

    private static AvailableArtifactPackage? FindLatestAvailableArtifactPackage(
        IReadOnlyList<AvailableArtifactPackage> packages,
        ConfiguredArtifactIdentity identity)
        => packages
            .Where(package => IsSameArtifactSlot(package.Identity, identity))
            .OrderByDescending(package => package.Identity.Version, VersionTextComparer.Instance)
            .FirstOrDefault();

    private static bool IsSameArtifactSlot(ConfiguredArtifactIdentity left, ConfiguredArtifactIdentity right)
        => left.ModuleKey.Equals(right.ModuleKey, StringComparison.OrdinalIgnoreCase)
            && left.AppKey.Equals(right.AppKey, StringComparison.OrdinalIgnoreCase)
            && left.PackageType.Equals(right.PackageType, StringComparison.OrdinalIgnoreCase)
            && left.TargetName.Equals(right.TargetName, StringComparison.OrdinalIgnoreCase);

    private static bool IsHostAgentArtifact(ConfiguredArtifactIdentity identity)
        => identity.PackageType.Equals("host-agent", StringComparison.OrdinalIgnoreCase);

    private static bool TryReplaceArtifactTargetVersion(
        string target,
        string currentVersion,
        string latestVersion,
        out string latestTarget)
    {
        latestTarget = target;
        var normalizedTarget = NormalizePathForMatch(target);
        var normalizedCurrentVersion = NormalizePathForMatch(currentVersion);
        if (!normalizedTarget.EndsWith("/" + normalizedCurrentVersion, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        latestTarget = normalizedTarget[..^normalizedCurrentVersion.Length] + latestVersion;
        return true;
    }

    private static IReadOnlyList<ArtifactPackageConfigurationFile> ExtractArtifactPackageConfigurationFiles(
        string source,
        string artifactStoreRoot)
    {
        var stagingPath = Path.Join(
            artifactStoreRoot,
            ".bootstrapper-artifact-config-staging",
            Guid.NewGuid().ToString("N"));

        try
        {
            var package = new ArtifactPackageExtractor()
                .Extract(source, stagingPath);
            return package.ConfigurationFiles;
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    private static async Task RegisterPackageArtifactsAsync(
        BootstrapConfig config,
        bool trustExistingArtifactVersions = false)
    {
        if (!config.Sql.Enabled)
        {
            Console.WriteLine("> SQL disabled; skipping artifact metadata registration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ArtifactStoreRoot))
        {
            return;
        }

        var artifactStoreRoot = Path.GetFullPath(config.ArtifactStoreRoot.Trim());
        await using var connection = new SqlConnection(BuildConnectionString(config.Sql, config.Sql.Database));
        await connection.OpenAsync();

        var registered = 0;
        foreach (var artifact in config.Artifacts.Where(static item => item.Enabled))
        {
            if (artifact.IsExample && !config.IncludeExampleApps)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(artifact.Source) || string.IsNullOrWhiteSpace(artifact.Target))
            {
                continue;
            }

            var identity = ParseConfiguredArtifactIdentity(artifact.Source);
            if (identity is null)
            {
                Console.WriteLine($"> Artifact metadata {artifact.Target}: skipped because Source filename does not use the standard artifact package name.");
                continue;
            }

            var appId = await ResolveArtifactAppIdAsync(connection, identity.ModuleKey, identity.AppKey);
            if (appId is null)
            {
                throw new InvalidOperationException(
                    $"Cannot register artifact '{Path.GetFileName(artifact.Source)}' because app '{identity.ModuleKey}/{identity.AppKey}' is not registered.");
            }

            var targetPath = CombineUnderRoot(artifactStoreRoot, artifact.Target);
            if (!Directory.Exists(targetPath))
            {
                throw new DirectoryNotFoundException(
                    $"Cannot register artifact '{Path.GetFileName(artifact.Source)}' because target path '{targetPath}' does not exist.");
            }

            var relativePath = NormalizePathForMatch(artifact.Target);
            var artifactId = trustExistingArtifactVersions
                ? await TryReuseExistingArtifactMetadataAsync(
                    connection,
                    appId.Value,
                    identity.Version,
                    identity.PackageType,
                    identity.TargetName,
                    relativePath)
                : null;
            if (artifactId is null)
            {
                var sha256 = ComputeDirectorySha256(targetPath);
                artifactId = await UpsertArtifactMetadataAsync(
                    connection,
                    appId.Value,
                    identity.Version,
                    identity.PackageType,
                    identity.TargetName,
                    relativePath,
                    sha256);
            }
            else
            {
                Console.WriteLine($"> Artifact metadata {artifact.Target}: fast mode reused existing metadata for version {identity.Version}.");
            }

            var updates = await ApplyConfiguredArtifactToMatchingApplicationsAsync(
                connection,
                artifactId.Value,
                identity.PackageType);
            if (updates.TemplateAppRowsUpdated + updates.AppInstanceRowsUpdated + updates.WorkerInstanceRowsUpdated > 0)
            {
                Console.WriteLine(
                    $"> Artifact desired state {artifact.Target}: updated {updates.TemplateAppRowsUpdated} template row(s), {updates.AppInstanceRowsUpdated} app instance row(s), {updates.WorkerInstanceRowsUpdated} worker row(s).");
            }

            if (identity.PackageType.Equals("host-agent", StringComparison.OrdinalIgnoreCase))
            {
                var hostAgentDesiredRows = await ApplyConfiguredHostAgentArtifactToCurrentHostAsync(
                    connection,
                    artifactId.Value,
                    config.HostAgent);
                if (hostAgentDesiredRows > 0)
                {
                    Console.WriteLine(
                        $"> HostAgent desired state {artifact.Target}: updated {hostAgentDesiredRows} host row(s).");
                }
            }

            registered++;
        }

        if (registered > 0)
        {
            Console.WriteLine($"> Artifact metadata registered or updated: {registered}");
        }
    }

    private static ConfiguredArtifactIdentity? ParseConfiguredArtifactIdentity(string source)
    {
        var fileName = Path.GetFileNameWithoutExtension(source);
        var parts = fileName.Split(["__"], StringSplitOptions.None);
        return parts.Length == 5
            && parts.All(static part => !string.IsNullOrWhiteSpace(part))
            ? new ConfiguredArtifactIdentity(parts[0], parts[1], parts[2], parts[3], parts[4])
            : null;
    }

    private static async Task<int?> ResolveArtifactAppIdAsync(
        SqlConnection connection,
        string moduleKey,
        string appKey)
    {
        const string sql = """
SELECT TOP (1) app.AppId
FROM omp.Apps app
INNER JOIN omp.Modules module ON module.ModuleId = app.ModuleId
WHERE module.ModuleKey = @moduleKey
  AND app.AppKey = @appKey
  AND module.IsEnabled = 1
  AND app.IsEnabled = 1
ORDER BY app.AppId;
""";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@moduleKey", moduleKey);
        command.Parameters.AddWithValue("@appKey", appKey);

        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static async Task<int?> TryReuseExistingArtifactMetadataAsync(
        SqlConnection connection,
        int appId,
        string version,
        string packageType,
        string targetName,
        string relativePath)
    {
        const string sql = """
DECLARE @artifactId int;

SELECT TOP (1) @artifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @appId
  AND Version = @version
  AND PackageType = @packageType
  AND ((TargetName = @targetName) OR (TargetName IS NULL AND @targetName IS NULL))
  AND RelativePath = @relativePath
ORDER BY ArtifactId;

IF @artifactId IS NOT NULL
BEGIN
    UPDATE omp.Artifacts
    SET IsEnabled = 1,
        UpdatedUtc = CASE WHEN IsEnabled = 0 THEN SYSUTCDATETIME() ELSE UpdatedUtc END
    WHERE ArtifactId = @artifactId;
END;

SELECT @artifactId;
""";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@appId", System.Data.SqlDbType.Int).Value = appId;
        command.Parameters.Add("@version", System.Data.SqlDbType.NVarChar, 50).Value = version;
        command.Parameters.Add("@packageType", System.Data.SqlDbType.NVarChar, 50).Value = packageType;
        command.Parameters.Add("@targetName", System.Data.SqlDbType.NVarChar, 200).Value =
            string.IsNullOrWhiteSpace(targetName) ? DBNull.Value : targetName.Trim();
        command.Parameters.Add("@relativePath", System.Data.SqlDbType.NVarChar, 512).Value = relativePath;

        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static async Task<int> UpsertArtifactMetadataAsync(
        SqlConnection connection,
        int appId,
        string version,
        string packageType,
        string targetName,
        string relativePath,
        string sha256)
    {
        const string sql = """
DECLARE @artifactId int;

UPDATE omp.Artifacts
SET RelativePath = @relativePath,
    Sha256 = @sha256,
    IsEnabled = 1,
    UpdatedUtc = SYSUTCDATETIME()
WHERE AppId = @appId
  AND Version = @version
  AND PackageType = @packageType
  AND ((TargetName = @targetName) OR (TargetName IS NULL AND @targetName IS NULL));

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO omp.Artifacts
    (
        AppId,
        Version,
        PackageType,
        TargetName,
        RelativePath,
        Sha256,
        IsEnabled
    )
    VALUES
    (
        @appId,
        @version,
        @packageType,
        @targetName,
        @relativePath,
        @sha256,
        1
    );

    SET @artifactId = CAST(SCOPE_IDENTITY() AS int);
END;
ELSE
BEGIN
    SELECT TOP (1) @artifactId = ArtifactId
    FROM omp.Artifacts
    WHERE AppId = @appId
      AND Version = @version
      AND PackageType = @packageType
      AND ((TargetName = @targetName) OR (TargetName IS NULL AND @targetName IS NULL))
    ORDER BY ArtifactId;
END;

SELECT @artifactId;
""";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@appId", appId);
        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@packageType", packageType);
        command.Parameters.AddWithValue("@targetName", string.IsNullOrWhiteSpace(targetName) ? DBNull.Value : targetName.Trim());
        command.Parameters.AddWithValue("@relativePath", relativePath);
        command.Parameters.AddWithValue("@sha256", sha256);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<(int TemplateAppRowsUpdated, int AppInstanceRowsUpdated, int WorkerInstanceRowsUpdated)> ApplyConfiguredArtifactToMatchingApplicationsAsync(
        SqlConnection connection,
        int artifactId,
        string packageType)
    {
        const string sql = """
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @appId int;
DECLARE @appType nvarchar(50);
DECLARE @templateAppRowsUpdated int = 0;
DECLARE @appInstanceRowsUpdated int = 0;
DECLARE @workerInstanceRowsUpdated int = 0;

SELECT @appId = app.AppId,
       @appType = app.AppType
FROM omp.Artifacts artifact
INNER JOIN omp.Apps app ON app.AppId = artifact.AppId
WHERE artifact.ArtifactId = @artifactId
  AND artifact.IsEnabled = 1
  AND app.IsEnabled = 1;

IF @appId IS NOT NULL
   AND
   (
       (@packageType = N'web-app' AND @appType IN (N'Portal', N'WebApp'))
       OR (@packageType = N'service-app' AND @appType = N'ServiceApp')
       OR (@packageType = N'worker' AND @appType = N'Worker')
       OR (@packageType = N'host-agent' AND @appType = N'HostAgent')
       OR (@packageType = N'worker-host' AND @appType = N'WorkerHost')
   )
BEGIN
    UPDATE omp.InstanceTemplateAppInstances
    SET DesiredArtifactId = @artifactId,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppId = @appId
      AND IsEnabled = 1
      AND ISNULL(DesiredArtifactId, -1) <> @artifactId;

    SET @templateAppRowsUpdated = @@ROWCOUNT;

    UPDATE omp.AppInstances
    SET ArtifactId = @artifactId,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppId = @appId
      AND IsEnabled = 1
      AND ISNULL(ArtifactId, -1) <> @artifactId;

    SET @appInstanceRowsUpdated = @@ROWCOUNT;

    UPDATE worker
    SET ArtifactId = @artifactId,
        UpdatedUtc = SYSUTCDATETIME()
    FROM omp.WorkerInstances worker
    INNER JOIN omp.AppInstances appInstance ON appInstance.AppInstanceId = worker.AppInstanceId
    WHERE appInstance.AppId = @appId
      AND worker.IsEnabled = 1
      AND worker.ArtifactId IS NOT NULL
      AND worker.ArtifactId <> @artifactId;

    SET @workerInstanceRowsUpdated = @@ROWCOUNT;
END;

SELECT @templateAppRowsUpdated,
       @appInstanceRowsUpdated,
       @workerInstanceRowsUpdated;
""";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@artifactId", artifactId);
        command.Parameters.AddWithValue("@packageType", packageType.Trim());

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (0, 0, 0);
        }

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static async Task<int> ApplyConfiguredHostAgentArtifactToCurrentHostAsync(
        SqlConnection connection,
        int artifactId,
        HostAgentInstallOptions hostAgent)
    {
        const string sql = """
DECLARE @hostId uniqueidentifier;
DECLARE @changes table(ActionName nvarchar(10) NOT NULL);

SELECT @hostId = HostId
FROM omp.Hosts
WHERE HostKey = @hostKey
  AND IsEnabled = 1;

IF @hostId IS NULL
BEGIN
    SELECT 0;
    RETURN;
END;

MERGE omp.HostAgentDesiredStates AS target
USING
(
    SELECT
        @hostId AS HostId,
        @artifactId AS ArtifactId,
        NULLIF(@serviceNamePrefix, N'') AS ServiceNamePrefix,
        NULLIF(@installRoot, N'') AS InstallRoot
) AS source
ON target.HostId = source.HostId
WHEN MATCHED AND
(
       target.ArtifactId <> source.ArtifactId
    OR (source.ServiceNamePrefix IS NOT NULL AND ISNULL(target.ServiceNamePrefix, N'') <> source.ServiceNamePrefix)
    OR (source.InstallRoot IS NOT NULL AND ISNULL(target.InstallRoot, N'') <> source.InstallRoot)
    OR target.IsEnabled = 0
)
    THEN UPDATE SET
        ArtifactId = source.ArtifactId,
        ServiceNamePrefix = COALESCE(source.ServiceNamePrefix, target.ServiceNamePrefix),
        InstallRoot = COALESCE(source.InstallRoot, target.InstallRoot),
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(HostId, ArtifactId, ServiceNamePrefix, InstallRoot, IsEnabled)
    VALUES(source.HostId, source.ArtifactId, source.ServiceNamePrefix, source.InstallRoot, 1)
OUTPUT $action INTO @changes;

SELECT COUNT(1) FROM @changes;
""";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@artifactId", artifactId);
        command.Parameters.AddWithValue("@hostKey", ResolveHostAgentHostKey(hostAgent));
        command.Parameters.AddWithValue("@serviceNamePrefix", ResolveHostAgentServiceNamePrefix(hostAgent.ServiceName));
        command.Parameters.AddWithValue("@installRoot", string.IsNullOrWhiteSpace(hostAgent.ServicesRoot) ? string.Empty : hostAgent.ServicesRoot.Trim());

        var value = await command.ExecuteScalarAsync();
        return value is int count ? count : 0;
    }

    private static string ResolveHostAgentHostKey(HostAgentInstallOptions hostAgent)
    {
        if (!string.IsNullOrWhiteSpace(hostAgent.HostKey))
        {
            return hostAgent.HostKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(hostAgent.HostName))
        {
            return hostAgent.HostName.Trim();
        }

        return Environment.MachineName;
    }

    private static HostAgentBootstrapServiceIdentity ResolveBootstrapHostAgentServiceIdentity(BootstrapConfig config)
    {
        var hostAgent = config.HostAgent;
        var prefix = ResolveHostAgentServiceNamePrefix(hostAgent.ServiceName);
        var version = ResolveConfiguredHostAgentArtifactVersion(config);
        var serviceName = string.IsNullOrWhiteSpace(version)
            ? prefix
            : $"{prefix}.{SanitizeWindowsServiceNamePart(version)}";
        var installPath = ResolveBootstrapHostAgentInstallPath(hostAgent, version);
        var displayName = ResolveSystemServiceDisplayName(
            hostAgent.DisplayName,
            prefix,
            version);

        return new HostAgentBootstrapServiceIdentity(
            prefix,
            serviceName,
            installPath,
            displayName,
            version);
    }

    private static void WriteHostAgentInstallOrUpdateIntent(BootstrapConfig config)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(config.HostAgent.ServiceName))
        {
            return;
        }

        var identity = ResolveBootstrapHostAgentServiceIdentity(config);
        if (ServiceExists(identity.ServiceName))
        {
            Console.WriteLine(
                $"> HostAgent service '{identity.ServiceName}' already exists; full install/update will stop and reconfigure it from this profile.");
            return;
        }

        if (HostAgentServiceWithPrefixExists(identity.ServiceNamePrefix))
        {
            Console.WriteLine(
                $"> HostAgent service '{identity.ServiceName}' is missing, but another HostAgent service is present; full install/update will configure '{identity.ServiceName}' from this profile and may remove target-path duplicates.");
            return;
        }

        Console.WriteLine($"> HostAgent service '{identity.ServiceName}' is missing; installing it.");
    }

    private static string ResolveBootstrapHostAgentInstallPath(
        HostAgentInstallOptions hostAgent,
        string? version)
    {
        var configuredInstallPath = string.IsNullOrWhiteSpace(hostAgent.InstallPath)
            ? string.Empty
            : Path.GetFullPath(hostAgent.InstallPath.Trim());
        if (string.IsNullOrWhiteSpace(version))
        {
            return configuredInstallPath;
        }

        var folderName = "HostAgent-" + SanitizeWindowsPathPart(version);
        if (!string.IsNullOrWhiteSpace(configuredInstallPath)
            && Path.GetFileName(configuredInstallPath).Equals(folderName, StringComparison.OrdinalIgnoreCase))
        {
            return configuredInstallPath;
        }

        var root = !string.IsNullOrWhiteSpace(hostAgent.ServicesRoot)
            ? Path.GetFullPath(hostAgent.ServicesRoot.Trim())
            : Path.GetDirectoryName(configuredInstallPath) ?? configuredInstallPath;
        return Path.Join(root, folderName);
    }

    private static string ResolveHostAgentServiceNamePrefix(string serviceName)
    {
        var trimmed = string.IsNullOrWhiteSpace(serviceName)
            ? "OMP.HostAgent"
            : serviceName.Trim().TrimEnd('.');

        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 1; index < parts.Length; index++)
        {
            var suffix = string.Join('.', parts.Skip(index));
            if (Version.TryParse(suffix, out _))
            {
                return string.Join('.', parts.Take(index));
            }
        }

        return trimmed;
    }

    private static string? ResolveConfiguredHostAgentArtifactVersion(BootstrapConfig config)
        => config.Artifacts
            .Where(static artifact => artifact.Enabled)
            .Select(static artifact =>
                TryResolveHostAgentVersionFromTarget(artifact.Target)
                ?? TryResolveHostAgentVersionFromPackageName(artifact.Source))
            .FirstOrDefault(static version => !string.IsNullOrWhiteSpace(version))
            ?? TryResolveHostAgentVersionFromPackageName(config.HostAgent.PackagePath);

    private static string? TryResolveHostAgentVersionFromTarget(string target)
    {
        var normalized = NormalizePathFragment(target);
        const string hostAgentTargetPrefix = "omp-hostagent/hostagent/";
        if (!normalized.StartsWith(hostAgentTargetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = normalized[hostAgentTargetPrefix.Length..].Trim('/');
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private static string? TryResolveHostAgentVersionFromPackageName(string source)
    {
        var fileName = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(fileName)
            || !fileName.Contains("__host-agent__", StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var parts = withoutExtension.Split("__", StringSplitOptions.None);
        return parts.Length >= 5 && !string.IsNullOrWhiteSpace(parts[^1])
            ? parts[^1]
            : null;
    }

    private static string NormalizePathFragment(string value)
        => value.Trim().Replace('\\', '/').TrimStart('/');

    private static string SanitizeWindowsServiceNamePart(string value)
    {
        var chars = value.Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'
                ? ch
                : '_')
            .ToArray();
        var sanitized = new string(chars).Trim('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static string SanitizeWindowsPathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value.Trim()
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(chars).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static string ComputeDirectorySha256(string path)
    {
        using var sha = SHA256.Create();
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relative);
            sha.TransformBlock(relativeBytes, 0, relativeBytes.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);

            using var stream = File.OpenRead(file);
            var buffer = new byte[ArtifactHashBufferSize];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static void PublishAvailableDeploymentObjects(
        BootstrapConfig config,
        string payloadRoot,
        bool overwrite = true)
    {
        if (string.IsNullOrWhiteSpace(config.ArtifactStoreRoot))
        {
            return;
        }

        var artifactStoreRoot = Path.GetFullPath(config.ArtifactStoreRoot.Trim());
        var availableRoot = Path.Join(artifactStoreRoot, "_available");
        var definitionsCopied = CopyAvailableDeploymentObjects(
            ResolvePackageModuleDefinitionsRoot(payloadRoot),
            Path.Join(availableRoot, "module-definitions"),
            "*.json",
            overwrite);
        var artifactsCopied = CopyAvailableDeploymentObjects(
            ResolvePackageArtifactsRoot(payloadRoot),
            Path.Join(availableRoot, "artifacts"),
            "*.zip",
            overwrite);
        var hostConfigsCopied = CopyAvailableDeploymentObjects(
            ResolvePackageHostConfigurationsRoot(payloadRoot),
            Path.Join(availableRoot, "host-configs"),
            "*.*",
            overwrite,
            static path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var configOverlaysCopied = CopyAvailableDeploymentObjects(
            ResolvePackageConfigOverlaysRoot(payloadRoot),
            Path.Join(availableRoot, "config-overlays"),
            "*.*",
            overwrite,
            static path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var widgetsCopied = CopyAvailableDeploymentObjects(
            ResolvePackageWidgetsRoot(payloadRoot),
            Path.Join(availableRoot, "widgets"),
            "*.json",
            overwrite);
        var widgetDataCopied = CopyAvailableDeploymentObjects(
            ResolvePackageWidgetDataRoot(payloadRoot),
            Path.Join(availableRoot, "widget-data"),
            "*.zip",
            overwrite);

        if (definitionsCopied > 0 || artifactsCopied > 0 || hostConfigsCopied > 0 || configOverlaysCopied > 0 || widgetsCopied > 0 || widgetDataCopied > 0)
        {
            Console.WriteLine(
                $"> Available package library: {definitionsCopied} module definition(s), {artifactsCopied} artifact package(s), {hostConfigsCopied} host config(s), {configOverlaysCopied} config overlay(s), {widgetsCopied} widget(s), {widgetDataCopied} widget data package(s)");
        }
    }

    private static int CopyAvailableDeploymentObjects(
        string sourceRoot,
        string targetRoot,
        string searchPattern,
        bool overwrite,
        Func<string, bool>? filter = null)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return 0;
        }

        Directory.CreateDirectory(targetRoot);
        var copied = 0;
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, searchPattern, SearchOption.TopDirectoryOnly))
        {
            if (filter is not null && !filter(sourcePath))
            {
                continue;
            }

            var targetPath = Path.Join(targetRoot, Path.GetFileName(sourcePath));
            if (!overwrite && File.Exists(targetPath))
            {
                continue;
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
            copied++;
        }

        return copied;
    }

    private static async Task RegisterPreparedArtifactConfigurationFilesAsync(
        BootstrapConfig config,
        IReadOnlyList<PreparedArtifactConfigurationFiles> preparedConfigurationFiles)
    {
        if (preparedConfigurationFiles.Count == 0)
        {
            return;
        }

        if (!config.Sql.Enabled)
        {
            Console.WriteLine("> SQL disabled; skipping artifact configuration file registration.");
            return;
        }

        await using var connection = new SqlConnection(BuildConnectionString(config.Sql, config.Sql.Database));
        await connection.OpenAsync();

        foreach (var prepared in preparedConfigurationFiles)
        {
            var artifactId = await ResolveArtifactIdByRelativePathAsync(
                connection,
                prepared.ArtifactRelativePath);

            await ReplaceArtifactConfigurationFilesAsync(
                connection,
                artifactId,
                prepared.ConfigurationFiles);

            Console.WriteLine(
                $"> Artifact config files {prepared.ArtifactRelativePath}: {prepared.ConfigurationFiles.Count}");
        }
    }

    private static async Task CopyMissingArtifactConfigurationFilesFromPreviousVersionsAsync(
        BootstrapConfig config,
        IReadOnlyList<PreparedArtifactConfigurationFiles> explicitlyPreparedConfigurationFiles)
    {
        if (!config.Sql.Enabled)
        {
            return;
        }

        var explicitTargets = explicitlyPreparedConfigurationFiles
            .Select(static item => NormalizePathForMatch(item.ArtifactRelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(BuildConnectionString(config.Sql, config.Sql.Database));
        await connection.OpenAsync();

        foreach (var artifact in config.Artifacts.Where(static item => item.Enabled))
        {
            if (artifact.IsExample && !config.IncludeExampleApps)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(artifact.Target))
            {
                continue;
            }

            var artifactRelativePath = NormalizePathForMatch(artifact.Target);
            if (explicitTargets.Contains(artifactRelativePath))
            {
                continue;
            }

            var copied = await CopyMissingArtifactConfigurationFilesFromPreviousVersionAsync(
                connection,
                artifactRelativePath,
                config.Sql.CommandTimeoutSeconds);
            if (copied > 0)
            {
                Console.WriteLine(
                    $"> Artifact config files {artifactRelativePath}: copied {copied} from latest previous artifact version.");
            }
        }
    }

    private static async Task<int> CopyMissingArtifactConfigurationFilesFromPreviousVersionAsync(
        SqlConnection connection,
        string artifactRelativePath,
        int commandTimeoutSeconds)
    {
        var target = await QueryArtifactConfigurationCopyTargetAsync(
            connection,
            artifactRelativePath,
            commandTimeoutSeconds);
        if (target is null || target.ConfigurationFileCount > 0)
        {
            return 0;
        }

        var candidates = await QueryArtifactConfigurationCopyCandidatesAsync(
            connection,
            target.ArtifactId,
            commandTimeoutSeconds);
        var sourceCandidates = candidates
            .Where(candidate => CompareVersionText(candidate.Version, target.Version) < 0)
            .ToList();
        var source = sourceCandidates.Count == 0
            ? null
            : sourceCandidates.Aggregate(static (current, candidate) =>
                CompareVersionText(candidate.Version, current.Version) > 0
                    || (CompareVersionText(candidate.Version, current.Version) == 0
                        && candidate.ArtifactId > current.ArtifactId)
                    ? candidate
                    : current);
        if (source is null)
        {
            return 0;
        }

        const string sql = """
INSERT INTO omp.ArtifactConfigurationFiles
(
    ArtifactId,
    RelativePath,
    FileContent,
    IsEnabled
)
SELECT @targetArtifactId,
       sourceFile.RelativePath,
       sourceFile.FileContent,
       sourceFile.IsEnabled
FROM omp.ArtifactConfigurationFiles sourceFile
WHERE sourceFile.ArtifactId = @sourceArtifactId
  AND NOT EXISTS
  (
      SELECT 1
      FROM omp.ArtifactConfigurationFiles existing
      WHERE existing.ArtifactId = @targetArtifactId
        AND existing.RelativePath = sourceFile.RelativePath
  );
""";

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@targetArtifactId", target.ArtifactId);
        command.Parameters.AddWithValue("@sourceArtifactId", source.ArtifactId);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<ArtifactConfigurationCopyTarget?> QueryArtifactConfigurationCopyTargetAsync(
        SqlConnection connection,
        string artifactRelativePath,
        int commandTimeoutSeconds)
    {
        const string sql = """
SELECT a.ArtifactId,
       a.Version,
       COUNT(cf.ArtifactConfigurationFileId) AS ConfigurationFileCount
FROM omp.Artifacts a
LEFT JOIN omp.ArtifactConfigurationFiles cf
    ON cf.ArtifactId = a.ArtifactId
WHERE a.RelativePath = @relativePath
  AND a.IsEnabled = 1
GROUP BY a.ArtifactId,
         a.Version
ORDER BY a.ArtifactId;
""";

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@relativePath", artifactRelativePath);

        var rows = new List<ArtifactConfigurationCopyTarget>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ArtifactConfigurationCopyTarget(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidOperationException(
                $"Cannot copy artifact configuration files because multiple enabled artifact rows have RelativePath '{artifactRelativePath}'.")
        };
    }

    private static async Task<IReadOnlyList<ArtifactConfigurationCopyCandidate>> QueryArtifactConfigurationCopyCandidatesAsync(
        SqlConnection connection,
        int targetArtifactId,
        int commandTimeoutSeconds)
    {
        const string sql = """
SELECT candidate.ArtifactId,
       candidate.Version
FROM omp.Artifacts target
INNER JOIN omp.Artifacts candidate
    ON candidate.AppId = target.AppId
   AND candidate.PackageType = target.PackageType
   AND ISNULL(candidate.TargetName, N'') = ISNULL(target.TargetName, N'')
WHERE target.ArtifactId = @targetArtifactId
  AND candidate.ArtifactId <> target.ArtifactId
  AND candidate.IsEnabled = 1
  AND EXISTS
  (
      SELECT 1
      FROM omp.ArtifactConfigurationFiles sourceFile
      WHERE sourceFile.ArtifactId = candidate.ArtifactId
  );
""";

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@targetArtifactId", targetArtifactId);

        var rows = new List<ArtifactConfigurationCopyCandidate>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ArtifactConfigurationCopyCandidate(
                reader.GetInt32(0),
                reader.GetString(1)));
        }

        return rows;
    }

    private static async Task<int> ResolveArtifactIdByRelativePathAsync(
        SqlConnection connection,
        string artifactRelativePath)
    {
        const string sql = """
SELECT ArtifactId
FROM omp.Artifacts
WHERE RelativePath = @relativePath
  AND IsEnabled = 1
ORDER BY ArtifactId;
""";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@relativePath", NormalizePathForMatch(artifactRelativePath));

        var artifactIds = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            artifactIds.Add(reader.GetInt32(0));
        }

        return artifactIds.Count switch
        {
            1 => artifactIds[0],
            0 => throw new InvalidOperationException(
                $"Cannot register artifact configuration files because no enabled artifact row has RelativePath '{artifactRelativePath}'."),
            _ => throw new InvalidOperationException(
                $"Cannot register artifact configuration files because multiple enabled artifact rows have RelativePath '{artifactRelativePath}'.")
        };
    }

    private static async Task ReplaceArtifactConfigurationFilesAsync(
        SqlConnection connection,
        int artifactId,
        IReadOnlyList<ArtifactPackageConfigurationFile> configurationFiles)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            await using (var delete = new SqlCommand(
                "DELETE FROM omp.ArtifactConfigurationFiles WHERE ArtifactId = @artifactId;",
                connection,
                transaction))
            {
                delete.Parameters.AddWithValue("@artifactId", artifactId);
                await delete.ExecuteNonQueryAsync();
            }

            const string insertSql = """
INSERT INTO omp.ArtifactConfigurationFiles
(
    ArtifactId,
    RelativePath,
    FileContent,
    IsEnabled
)
VALUES
(
    @artifactId,
    @relativePath,
    @fileContent,
    1
);
""";

            foreach (var configurationFile in configurationFiles)
            {
                await using var insert = new SqlCommand(insertSql, connection, transaction);
                insert.Parameters.AddWithValue("@artifactId", artifactId);
                insert.Parameters.AddWithValue("@relativePath", configurationFile.RelativePath);
                insert.Parameters.AddWithValue("@fileContent", configurationFile.FileContent);
                await insert.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task RefreshExistingHostAgentRuntimeSettingsAsync(
        BootstrapConfig config,
        HostAgentBootstrapServiceIdentity serviceIdentity)
    {
        if (!Directory.Exists(serviceIdentity.InstallPath))
        {
            Console.WriteLine(
                $"> HostAgent service exists, but install path was not found; runtime settings were not refreshed. Path={serviceIdentity.InstallPath}");
            return;
        }

        Console.WriteLine("> Refresh HostAgent runtime settings and credential store");
        await WriteHostAgentSettingsAsync(config, serviceIdentity.InstallPath, serviceIdentity);

        if (config.HostAgent.StartService)
        {
            StopService(serviceIdentity.ServiceName);
            StartService(serviceIdentity.ServiceName);
        }
    }

    private static async Task InstallHostAgentAsync(BootstrapConfig config, string payloadRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("HostAgent Windows service installation requires Windows.");
        }

        if (!IsWindowsAdministrator())
        {
            throw new InvalidOperationException("Run the bootstrapper as Administrator to install the HostAgent Windows service.");
        }

        var hostAgent = config.HostAgent;
        ValidateHostAgentServiceAccount(hostAgent);
        var serviceIdentity = ResolveBootstrapHostAgentServiceIdentity(config);
        if (string.IsNullOrWhiteSpace(hostAgent.ServiceName))
        {
            throw new InvalidOperationException("HostAgent:ServiceName must be configured.");
        }

        if (string.IsNullOrWhiteSpace(hostAgent.InstallPath))
        {
            throw new InvalidOperationException("HostAgent:InstallPath must be configured.");
        }

        if (string.IsNullOrWhiteSpace(hostAgent.PackagePath))
        {
            throw new InvalidOperationException("HostAgent:PackagePath must be configured.");
        }

        var packagePath = ResolvePackageDataPath(payloadRoot, hostAgent.PackagePath);
        var installPath = serviceIdentity.InstallPath;
        var stagingRoot = Path.Join(Path.GetTempPath(), "OMP.HostAgent", Guid.NewGuid().ToString("N"));
        var sourceDirectory = packagePath;

        try
        {
            if (File.Exists(packagePath) && packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(stagingRoot);
                var extraction = new ArtifactPackageExtractor().Extract(packagePath, stagingRoot);
                sourceDirectory = extraction.ArtifactContentPath;
            }

            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException($"HostAgent package folder was not found: {sourceDirectory}");
            }

            var hostAgentServices = EnumerateHostAgentWindowsServices(serviceIdentity.ServiceNamePrefix, installPath);
            foreach (var service in hostAgentServices)
            {
                if (string.Equals(service.Name, serviceIdentity.ServiceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var usesTargetInstallPath = !string.IsNullOrWhiteSpace(service.ExecutablePath)
                    && IsSameOrChildPath(installPath, service.ExecutablePath);
                var isConfiguredBaseServiceName = !string.IsNullOrWhiteSpace(hostAgent.ServiceName)
                    && string.Equals(service.Name, hostAgent.ServiceName, StringComparison.OrdinalIgnoreCase);
                if (!usesTargetInstallPath && !isConfiguredBaseServiceName)
                {
                    continue;
                }

                Console.WriteLine($"> Remove duplicate HostAgent service {service.Name}");
                DeleteWindowsService(service.Name);
            }

            var serviceExists = ServiceExists(serviceIdentity.ServiceName);
            if (serviceExists)
            {
                StopService(serviceIdentity.ServiceName);
            }

            if (Directory.Exists(installPath) && hostAgent.BackupExistingInstall)
            {
                var backupPath = CreateBackupPath(installPath);
                Console.WriteLine($"> Backup HostAgent {installPath} -> {backupPath}");
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                CopyDirectory(installPath, backupPath);
            }

            Console.WriteLine($"> Install HostAgent {installPath}");
            CopyDirectory(sourceDirectory, installPath);
            await WriteHostAgentSettingsAsync(config, installPath, serviceIdentity);

            var executablePath = Path.Join(installPath, "OpenModulePlatform.HostAgent.WindowsService.exe");
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException("HostAgent executable was not found after installation.", executablePath);
            }

            RunHostAgentOnce(executablePath, installPath, serviceIdentity.ServiceName);
            var serviceAccountPassword = ResolveInstallerSecret(
                hostAgent.ServiceAccountPassword,
                config,
                "HostAgent:ServiceAccountPassword");

            if (serviceExists)
            {
                ConfigureService(hostAgent, serviceIdentity, executablePath, serviceAccountPassword);
            }
            else
            {
                CreateService(hostAgent, serviceIdentity, executablePath, serviceAccountPassword);
            }

            if (hostAgent.StartService)
            {
                StartService(serviceIdentity.ServiceName);
            }
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static async Task WriteHostAgentSettingsAsync(
        BootstrapConfig config,
        string installPath,
        HostAgentBootstrapServiceIdentity serviceIdentity)
    {
        var hostAgent = config.HostAgent;
        var settings = hostAgent.AppSettings?.DeepClone()
            ?? CreateDefaultHostAgentSettings(config, serviceIdentity);
        var credentialPlan = CreateHostAgentCredentialPlan(config, installPath);

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SqlConnectionString"] = BuildConnectionString(config.Sql, config.Sql.Database),
            ["ArtifactStoreRoot"] = Path.GetFullPath(config.ArtifactStoreRoot.Trim()),
            ["HostAgent.InstallPath"] = serviceIdentity.InstallPath,
            ["HostAgent.LocalArtifactCacheRoot"] = hostAgent.LocalArtifactCacheRoot,
            ["HostAgent.HostKey"] = hostAgent.HostKey,
            ["HostAgent.HostName"] = hostAgent.HostName,
            ["HostAgent.ServiceName"] = serviceIdentity.ServiceName
        };

        ReplaceTokens(settings, tokens);
        SynchronizeHostAgentSettings(settings, config, credentialPlan, serviceIdentity);

        var fileName = string.IsNullOrWhiteSpace(hostAgent.SettingsFileName)
            ? "appsettings.Production.json"
            : hostAgent.SettingsFileName.Trim();
        var path = CombineUnderRoot(installPath, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await WriteHostAgentCredentialStoreAsync(credentialPlan);

        await File.WriteAllTextAsync(
            path,
            settings.ToJsonString(JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void RunHostAgentOnce(string executablePath, string installPath, string serviceName)
    {
        Console.WriteLine("> Run HostAgent once");
        RunProcess(executablePath, ["--run-once", $"--service-name={serviceName}"], workingDirectory: installPath);
    }

    private static void SynchronizeHostAgentSettings(
        JsonNode settings,
        BootstrapConfig config,
        HostAgentCredentialBootstrapPlan credentialPlan,
        HostAgentBootstrapServiceIdentity serviceIdentity)
    {
        if (settings is not JsonObject root)
        {
            throw new InvalidOperationException("HostAgent appsettings root must be a JSON object.");
        }

        var hostAgent = config.HostAgent;
        var connectionStrings = GetOrCreateJsonObject(root, "ConnectionStrings");
        connectionStrings["OmpDb"] = BuildConnectionString(config.Sql, config.Sql.Database);

        var hostAgentSettings = GetOrCreateJsonObject(root, "HostAgent");
        hostAgentSettings["ServiceName"] = serviceIdentity.ServiceName;
        if (!string.IsNullOrWhiteSpace(serviceIdentity.Version))
        {
            hostAgentSettings["Version"] = serviceIdentity.Version;
        }

        hostAgentSettings["HostKey"] = hostAgent.HostKey;
        hostAgentSettings["HostName"] = hostAgent.HostName;
        hostAgentSettings["RefreshSeconds"] = hostAgent.RefreshSeconds;
        hostAgentSettings["CentralArtifactRoot"] = Path.GetFullPath(config.ArtifactStoreRoot.Trim());
        hostAgentSettings["LocalArtifactCacheRoot"] = hostAgent.LocalArtifactCacheRoot;
        hostAgentSettings["DeployWebApps"] = hostAgent.DeployWebApps;
        hostAgentSettings["IisSiteName"] = hostAgent.IisSiteName;
        hostAgentSettings["EnsureIisSite"] = hostAgent.EnsureIisSite;
        hostAgentSettings["IisBindingProtocol"] = hostAgent.IisBindingProtocol;
        hostAgentSettings["IisBindingPort"] = hostAgent.IisBindingPort;
        hostAgentSettings["IisBindingHostHeader"] = hostAgent.IisBindingHostHeader;
        hostAgentSettings["IisBindingCertificateThumbprint"] = hostAgent.IisBindingCertificateThumbprint;
        hostAgentSettings["IisBindingCertificateSerialNumber"] = hostAgent.IisBindingCertificateSerialNumber;
        hostAgentSettings["IisBindingCertificateStoreName"] = hostAgent.IisBindingCertificateStoreName;
        hostAgentSettings["WebAppsRoot"] = hostAgent.WebAppsRoot;
        hostAgentSettings["PortalPhysicalPath"] = hostAgent.PortalPhysicalPath;
        hostAgentSettings["IisAppPoolNamePrefix"] = hostAgent.IisAppPoolNamePrefix;
        hostAgentSettings["IisAppPoolUserName"] = hostAgent.IisAppPoolUserName;
        hostAgentSettings["IisAppPoolPasswordCredentialKey"] = credentialPlan.DefaultIisAppPoolCredentialKey;
        hostAgentSettings.Remove("IisAppPoolPassword");
        if (credentialPlan.IisAppPoolOverrides.Count > 0)
        {
            hostAgentSettings["IisAppPoolOverrides"] = JsonSerializer.SerializeToNode(
                credentialPlan.IisAppPoolOverrides,
                JsonOptions);
        }
        else
        {
            hostAgentSettings.Remove("IisAppPoolOverrides");
        }

        hostAgentSettings["DeployServiceApps"] = hostAgent.DeployServiceApps;
        hostAgentSettings["ServicesRoot"] = hostAgent.ServicesRoot;
        hostAgentSettings["ServiceAppUserName"] = string.IsNullOrWhiteSpace(hostAgent.ServiceAppUserName)
            ? hostAgent.ServiceAccountName
            : hostAgent.ServiceAppUserName;
        hostAgentSettings["ServiceAppPasswordCredentialKey"] = credentialPlan.DefaultServiceAppCredentialKey;
        hostAgentSettings.Remove("ServiceAppPassword");
        if (credentialPlan.ServiceAppIdentityOverrides.Count > 0)
        {
            hostAgentSettings["ServiceAppIdentityOverrides"] = JsonSerializer.SerializeToNode(
                credentialPlan.ServiceAppIdentityOverrides,
                JsonOptions);
        }
        else
        {
            hostAgentSettings.Remove("ServiceAppIdentityOverrides");
        }

        var selfUpgrade = GetOrCreateJsonObject(hostAgentSettings, "SelfUpgrade");
        selfUpgrade["InstallRoot"] = hostAgent.ServicesRoot;
        selfUpgrade["ServiceNamePrefix"] = serviceIdentity.ServiceNamePrefix;
        selfUpgrade["ServiceAccountName"] = hostAgent.ServiceAccountName;
        selfUpgrade["ServiceAccountPasswordCredentialKey"] = credentialPlan.ServiceAccountCredentialKey;
        selfUpgrade.Remove("ServiceAccountPassword");

        var credentialStore = GetOrCreateJsonObject(hostAgentSettings, "CredentialStore");
        credentialStore["AutomationMode"] = credentialPlan.StoreSettings.AutomationMode;
        credentialStore["FilePath"] = credentialPlan.StoreSettings.FilePath;
        credentialStore["ProtectionScope"] = credentialPlan.StoreSettings.ProtectionScope;
        credentialStore["EntropyPurpose"] = credentialPlan.StoreSettings.EntropyPurpose;
    }

    private static HostAgentCredentialBootstrapPlan CreateHostAgentCredentialPlan(
        BootstrapConfig config,
        string installPath)
    {
        var hostAgent = config.HostAgent;
        var credentials = new List<HostAgentPlainTextCredential>();
        var overrides = new Dictionary<string, HostAgentIisAppPoolIdentitySettings>(StringComparer.OrdinalIgnoreCase);

        var serviceAccountPassword = ResolveInstallerSecret(
            hostAgent.ServiceAccountPassword,
            config,
            "HostAgent:ServiceAccountPassword");
        var serviceAccountCredentialKey = ResolveCredentialKey(
            hostAgent.ServiceAccountCredentialKey,
            serviceAccountPassword,
            "hostagent:self-upgrade");
        AddCredentialIfConfigured(
            credentials,
            serviceAccountCredentialKey,
            hostAgent.ServiceAccountName,
            serviceAccountPassword);

        var defaultIisPassword = ResolveInstallerSecret(
            hostAgent.IisAppPoolPassword,
            config,
            "HostAgent:IisAppPoolPassword");
        var defaultIisCredentialKey = ResolveCredentialKey(
            hostAgent.IisAppPoolPasswordCredentialKey,
            defaultIisPassword,
            "iis:default");
        AddCredentialIfConfigured(
            credentials,
            defaultIisCredentialKey,
            hostAgent.IisAppPoolUserName,
            defaultIisPassword);

        var serviceAppPassword = string.IsNullOrWhiteSpace(hostAgent.ServiceAppPassword)
            ? serviceAccountPassword
            : ResolveInstallerSecret(
                hostAgent.ServiceAppPassword,
                config,
                "HostAgent:ServiceAppPassword");
        var serviceAppCredentialKey = ResolveCredentialKey(
            hostAgent.ServiceAppPasswordCredentialKey,
            serviceAppPassword,
            "service-app:default");
        var serviceAppUserName = string.IsNullOrWhiteSpace(hostAgent.ServiceAppUserName)
            ? hostAgent.ServiceAccountName
            : hostAgent.ServiceAppUserName;
        if (string.IsNullOrWhiteSpace(hostAgent.ServiceAppPasswordCredentialKey)
            && string.Equals(serviceAppUserName, hostAgent.ServiceAccountName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(serviceAppPassword, serviceAccountPassword, StringComparison.Ordinal))
        {
            serviceAppCredentialKey = serviceAccountCredentialKey;
        }

        AddCredentialIfConfigured(
            credentials,
            serviceAppCredentialKey,
            serviceAppUserName,
            serviceAppPassword);

        var serviceAppOverrides = new Dictionary<string, HostAgentServiceAppIdentitySettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in hostAgent.ServiceAppIdentityOverrides)
        {
            var configured = pair.Value;
            if (configured is null)
            {
                continue;
            }

            var overridePassword = ResolveInstallerSecret(
                configured.Password,
                config,
                $"HostAgent:ServiceAppIdentityOverrides:{pair.Key}:Password");
            var overrideCredentialKey = ResolveCredentialKey(
                configured.PasswordCredentialKey,
                overridePassword,
                "service-app:override:" + SanitizeCredentialKey(pair.Key));

            AddCredentialIfConfigured(
                credentials,
                overrideCredentialKey,
                configured.UserName,
                overridePassword);

            serviceAppOverrides[pair.Key] = new HostAgentServiceAppIdentitySettings
            {
                UserName = configured.UserName,
                PasswordCredentialKey = overrideCredentialKey
            };
        }

        foreach (var pair in hostAgent.IisAppPoolOverrides)
        {
            var configured = pair.Value;
            if (configured is null)
            {
                continue;
            }

            var overridePassword = ResolveInstallerSecret(
                configured.Password,
                config,
                $"HostAgent:IisAppPoolOverrides:{pair.Key}:Password");
            var overrideCredentialKey = ResolveCredentialKey(
                configured.PasswordCredentialKey,
                overridePassword,
                "iis:override:" + SanitizeCredentialKey(pair.Key));

            AddCredentialIfConfigured(
                credentials,
                overrideCredentialKey,
                configured.UserName,
                overridePassword);

            overrides[pair.Key] = new HostAgentIisAppPoolIdentitySettings
            {
                UserName = configured.UserName,
                PasswordCredentialKey = overrideCredentialKey
            };
        }

        var hasCredentialReferences = !string.IsNullOrWhiteSpace(serviceAccountCredentialKey)
            || !string.IsNullOrWhiteSpace(defaultIisCredentialKey)
            || !string.IsNullOrWhiteSpace(serviceAppCredentialKey)
            || serviceAppOverrides.Values.Any(static value => !string.IsNullOrWhiteSpace(value.PasswordCredentialKey))
            || overrides.Values.Any(static value => !string.IsNullOrWhiteSpace(value.PasswordCredentialKey));
        var storeSettings = CreateCredentialStoreSettings(
            hostAgent.CredentialStore,
            installPath,
            credentials.Count > 0 || hasCredentialReferences);

        if (credentials.Count > 0 && !storeSettings.IsEnabled())
        {
            throw new InvalidOperationException(
                "HostAgent credential store must be enabled when installer credentials are configured.");
        }

        return new HostAgentCredentialBootstrapPlan(
            storeSettings,
            serviceAccountCredentialKey,
            defaultIisCredentialKey,
            serviceAppCredentialKey,
            serviceAppOverrides,
            overrides,
            credentials);
    }

    private static HostAgentCredentialStoreSettings CreateCredentialStoreSettings(
        HostAgentCredentialStoreBootstrapOptions options,
        string installPath,
        bool isRequired)
    {
        var automationMode = string.IsNullOrWhiteSpace(options.AutomationMode)
            ? (isRequired ? HostAgentCredentialAutomationModes.Full : HostAgentCredentialAutomationModes.Disabled)
            : options.AutomationMode.Trim();

        var filePath = string.IsNullOrWhiteSpace(options.FilePath) && isRequired
            ? Path.Join(Path.GetFullPath(installPath), "hostagent.credentials.json")
            : options.FilePath?.Trim() ?? string.Empty;

        var settings = new HostAgentCredentialStoreSettings
        {
            AutomationMode = automationMode,
            FilePath = filePath,
            ProtectionScope = string.IsNullOrWhiteSpace(options.ProtectionScope)
                ? HostAgentCredentialProtectionScopes.LocalMachine
                : options.ProtectionScope.Trim(),
            EntropyPurpose = string.IsNullOrWhiteSpace(options.EntropyPurpose)
                ? "OpenModulePlatform.HostAgent.CredentialStore.v1"
                : options.EntropyPurpose.Trim()
        };
        settings.Validate();
        return settings;
    }

    private static async Task WriteHostAgentCredentialStoreAsync(HostAgentCredentialBootstrapPlan credentialPlan)
    {
        if (credentialPlan.Credentials.Count == 0)
        {
            return;
        }

        var settings = credentialPlan.StoreSettings;
        settings.Validate();
        var path = settings.ResolveFilePath();
        var document = File.Exists(path)
            ? JsonSerializer.Deserialize<HostAgentCredentialStoreDocument>(
                await File.ReadAllTextAsync(path),
                JsonOptions) ?? new HostAgentCredentialStoreDocument()
            : new HostAgentCredentialStoreDocument();

        document.Credentials ??= new Dictionary<string, HostAgentStoredCredentialEntry>(StringComparer.OrdinalIgnoreCase);
        document.Credentials = new Dictionary<string, HostAgentStoredCredentialEntry>(
            document.Credentials,
            StringComparer.OrdinalIgnoreCase);

        foreach (var credential in credentialPlan.Credentials)
        {
            document.Credentials[credential.Key] = new HostAgentStoredCredentialEntry
            {
                UserName = credential.UserName,
                EncryptedPassword = HostAgentCredentialStoreService.ProtectPassword(credential.Password, settings),
                ProtectionProvider = "WindowsDpapi",
                ProtectionScope = settings.ProtectionScope,
                Description = "Written by OpenModulePlatform bootstrapper.",
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }

        document.UpdatedUtc = DateTimeOffset.UtcNow;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(document, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AddCredentialIfConfigured(
        List<HostAgentPlainTextCredential> credentials,
        string key,
        string userName,
        string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Credential key must be configured when a password is configured.");
        }

        credentials.Add(new HostAgentPlainTextCredential(key.Trim(), userName.Trim(), password));
    }

    private static string ResolveCredentialKey(string configuredKey, string password, string defaultKey)
        => !string.IsNullOrWhiteSpace(configuredKey)
            ? configuredKey.Trim()
            : string.IsNullOrWhiteSpace(password)
                ? string.Empty
                : defaultKey;

    private static string SanitizeCredentialKey(string value)
    {
        var chars = value
            .Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_')
            .ToArray();
        var result = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(result) ? Guid.NewGuid().ToString("N") : result;
    }

    private static string ResolveInstallerSecret(
        string? value,
        BootstrapConfig config,
        string fieldName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!IsPortableEncryptedSecret(value))
        {
            return value;
        }

        var key = ResolvePortableEncryptionKey(config, fieldName);
        return UnprotectPortableInstallerSecret(value.Trim(), key);
    }

    private static bool IsPortableEncryptedSecret(string value)
        => value.TrimStart().StartsWith("enc:aesgcm:v1:", StringComparison.Ordinal);

    private static byte[] ResolvePortableEncryptionKey(BootstrapConfig config, string fieldName)
    {
        var envName = config.Security.PortableEncryptionKeyEnvironmentVariable?.Trim();
        var keyText = !string.IsNullOrWhiteSpace(envName)
            ? Environment.GetEnvironmentVariable(envName)
            : null;
        keyText = string.IsNullOrWhiteSpace(keyText)
            ? config.Security.PortableEncryptionKey
            : keyText;

        if (string.IsNullOrWhiteSpace(keyText))
        {
            throw new InvalidOperationException(
                $"{fieldName} is encrypted, but Security:PortableEncryptionKey or Security:PortableEncryptionKeyEnvironmentVariable is not configured.");
        }

        var trimmed = keyText.Trim();
        if (trimmed.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = Convert.FromBase64String(trimmed["base64:".Length..]);
            if (decoded.Length != 32)
            {
                throw new InvalidOperationException("Security:PortableEncryptionKey base64 value must decode to 32 bytes.");
            }

            return decoded;
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
    }

    private static string UnprotectPortableInstallerSecret(string encryptedValue, byte[] key)
    {
        var parts = encryptedValue.Trim().Split(':');
        if (parts.Length != 6
            || parts[0] != "enc"
            || parts[1] != "aesgcm"
            || parts[2] != "v1")
        {
            throw new InvalidOperationException("Encrypted installer secret has an unsupported format.");
        }

        var nonce = Convert.FromBase64String(parts[3]);
        var cipherText = Convert.FromBase64String(parts[4]);
        var tag = Convert.FromBase64String(parts[5]);
        var plainText = new byte[cipherText.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, cipherText, tag, plainText);
        return Encoding.UTF8.GetString(plainText);
    }

    private static string ProtectPortableInstallerSecret(string value, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainText = Encoding.UTF8.GetBytes(value);
        var cipherText = new byte[plainText.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plainText, cipherText, tag);
        return string.Join(
            ':',
            "enc",
            "aesgcm",
            "v1",
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(cipherText),
            Convert.ToBase64String(tag));
    }

    private static JsonObject GetOrCreateJsonObject(JsonObject parent, string propertyName)
    {
        foreach (var property in parent.ToArray())
        {
            if (!property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value is JsonObject existing)
            {
                return existing;
            }

            parent.Remove(property.Key);
            break;
        }

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    private static JsonNode CreateDefaultHostAgentSettings(
        BootstrapConfig config,
        HostAgentBootstrapServiceIdentity serviceIdentity)
    {
        var hostAgent = config.HostAgent;
        var localArtifactCacheRoot = string.IsNullOrWhiteSpace(hostAgent.LocalArtifactCacheRoot)
            ? Path.Join(serviceIdentity.InstallPath, "ArtifactCache")
            : hostAgent.LocalArtifactCacheRoot.Trim();

        return JsonNode.Parse(
            JsonSerializer.Serialize(
                new
                {
                    ConnectionStrings = new
                    {
                        OmpDb = "{SqlConnectionString}"
                    },
                    HostAgent = new
                    {
                        hostAgent.ServiceName,
                        hostAgent.HostKey,
                        hostAgent.HostName,
                        RefreshSeconds = hostAgent.RefreshSeconds,
                        CentralArtifactRoot = "{ArtifactStoreRoot}",
                        LocalArtifactCacheRoot = localArtifactCacheRoot,
                        MaterializeTemplates = true,
                        ProcessHostDeployments = true,
                        ProvisionAppInstanceArtifacts = true,
                        ProvisionExplicitRequirements = true,
                        ArtifactZipImport = new
                        {
                            IsEnabled = false,
                            ImportPath = string.Empty,
                            ProcessedPath = string.Empty,
                            FailedPath = string.Empty,
                            MaxFilesPerCycle = 10,
                            CopyConfigurationFilesFromPreviousVersion = true
                        },
                        DeployWebApps = hostAgent.DeployWebApps,
                        IisSiteName = hostAgent.IisSiteName,
                        EnsureIisSite = hostAgent.EnsureIisSite,
                        IisBindingProtocol = hostAgent.IisBindingProtocol,
                        IisBindingPort = hostAgent.IisBindingPort,
                        IisBindingHostHeader = hostAgent.IisBindingHostHeader,
                        IisBindingCertificateThumbprint = hostAgent.IisBindingCertificateThumbprint,
                        IisBindingCertificateSerialNumber = hostAgent.IisBindingCertificateSerialNumber,
                        IisBindingCertificateStoreName = hostAgent.IisBindingCertificateStoreName,
                        WebAppsRoot = hostAgent.WebAppsRoot,
                        PortalPhysicalPath = hostAgent.PortalPhysicalPath,
                        IisAppPoolNamePrefix = hostAgent.IisAppPoolNamePrefix,
                        IisAppPoolUserName = hostAgent.IisAppPoolUserName,
                        IisAppPoolPasswordCredentialKey = string.Empty,
                        IisAppPoolOverrides = new Dictionary<string, object>(),
                        DeployServiceApps = hostAgent.DeployServiceApps,
                        ServicesRoot = hostAgent.ServicesRoot,
                        ServiceAppUserName = string.IsNullOrWhiteSpace(hostAgent.ServiceAppUserName)
                            ? hostAgent.ServiceAccountName
                            : hostAgent.ServiceAppUserName,
                        ServiceAppPasswordCredentialKey = string.Empty,
                        ServiceAppIdentityOverrides = new Dictionary<string, object>(),
                        SelfUpgrade = new
                        {
                            IsEnabled = true,
                            InstallRoot = hostAgent.ServicesRoot,
                            ServiceNamePrefix = hostAgent.ServiceName,
                            ServiceAccountName = hostAgent.ServiceAccountName,
                            ServiceAccountPasswordCredentialKey = string.Empty,
                            TakeoverStopTimeoutSeconds = 45,
                            DeletePreviousServiceAfterTakeover = true,
                            StartPreparedService = true
                        },
                        CredentialStore = new
                        {
                            AutomationMode = "Disabled",
                            FilePath = string.Empty,
                            ProtectionScope = "LocalMachine",
                            EntropyPurpose = "OpenModulePlatform.HostAgent.CredentialStore.v1"
                        },
                        EnableRpc = true
                    }
                },
                JsonOptions))!;
    }

    private static void ReplaceTokens(JsonNode node, IReadOnlyDictionary<string, string> tokens)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is null)
                {
                    continue;
                }

                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    obj[property.Key] = ReplaceTokenText(text, tokens);
                    continue;
                }

                ReplaceTokens(property.Value, tokens);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    array[i] = ReplaceTokenText(text, tokens);
                }
                else if (array[i] is not null)
                {
                    ReplaceTokens(array[i]!, tokens);
                }
            }
        }
    }

    private static string ReplaceTokenText(string value, IReadOnlyDictionary<string, string> tokens)
    {
        var result = value;
        foreach (var token in tokens)
        {
            result = result.Replace("{" + token.Key + "}", token.Value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void ValidateHostAgentServiceAccount(HostAgentInstallOptions hostAgent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var account = NormalizeWindowsAccount(hostAgent.ServiceAccountName);
        if (string.IsNullOrWhiteSpace(account) || IsBuiltInServiceAccount(account))
        {
            return;
        }

        if (IsCurrentWindowsIdentity(account) && IsWindowsAdministrator())
        {
            return;
        }

        var canCheckDirectMembership = TryGetLocalAdministratorMembers(out var members, out var checkError);
        if (!canCheckDirectMembership)
        {
            Console.WriteLine($"WARNING: Could not verify whether '{account}' is a local administrator: {checkError}");
            return;
        }

        var isDirectMember = members.Any(member => WindowsAccountEquals(member, account));
        if (isDirectMember)
        {
            return;
        }

        if (IsLocalMachineAccount(account))
        {
            throw new InvalidOperationException(
                $"HostAgent service account '{account}' is not listed as a local administrator. Add it to the local Administrators group before installing HostAgent, or choose another service account.");
        }

        Console.WriteLine(
            $"WARNING: Could not confirm that HostAgent service account '{account}' is a direct local administrator. If access is granted through a nested domain group this may be fine; otherwise add it before installation.");
    }

    private static bool TryGetLocalAdministratorMembers(
        out List<string> members,
        out string error)
    {
        members = [];
        error = string.Empty;

        try
        {
            var adminGroup = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                .Translate(typeof(NTAccount))
                .Value
                .Split('\\')
                .Last();

            var result = RunProcess("net", ["localgroup", adminGroup], throwOnFailure: false);
            if (result.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim();
                return false;
            }

            members.AddRange(
                result.StdOut
                    .Split([Environment.NewLine], StringSplitOptions.None)
                    .Select(static rawLine => rawLine.Trim())
                    .SkipWhile(static line => !line.StartsWith("---", StringComparison.Ordinal))
                    .Skip(1)
                    .Where(static line => line.Length > 0
                        && !line.Contains("command completed", StringComparison.OrdinalIgnoreCase)
                        && !line.Contains("kommandot slutf", StringComparison.OrdinalIgnoreCase))
                    .Select(NormalizeWindowsAccount));

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsCurrentWindowsIdentity(string account)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return WindowsAccountEquals(identity.Name, account);
    }

    private static bool IsLocalMachineAccount(string account)
    {
        var normalized = NormalizeWindowsAccount(account);
        var prefix = Environment.MachineName + "\\";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBuiltInServiceAccount(string account)
    {
        var normalized = NormalizeWindowsAccount(account);
        return normalized.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool WindowsAccountEquals(string left, string right)
        => NormalizeWindowsAccount(left).Equals(NormalizeWindowsAccount(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWindowsAccount(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith(".\\", StringComparison.Ordinal))
        {
            return Environment.MachineName + "\\" + trimmed[2..];
        }

        if (!trimmed.Contains('\\', StringComparison.Ordinal)
            && !trimmed.Contains('@', StringComparison.Ordinal)
            && !IsBuiltInServiceAccountName(trimmed))
        {
            return Environment.MachineName + "\\" + trimmed;
        }

        return trimmed;
    }

    private static bool IsBuiltInServiceAccountName(string value)
        => value.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || value.Equals("LocalService", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NetworkService", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] ProductOwnedServiceNamePrefixes =
    [
        "OMP.",
        "OpenModulePlatform."
    ];

    private static void RemoveWindowsServices(BootstrapConfig config)
    {
        var serviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddWindowsServiceName(serviceNames, config.HostAgent.ServiceName);
        foreach (var serviceName in config.HostAgent.AdditionalServiceNamesToRemove ?? [])
        {
            AddWindowsServiceName(serviceNames, serviceName);
        }

        var runtimeRoots = GetServiceRuntimeRoots(config.HostAgent);
        foreach (var serviceName in EnumerateWindowsServiceNames())
        {
            var executablePath = GetWindowsServiceExecutablePath(serviceName);
            if (IsProductOwnedServiceName(serviceName)
                || (!string.IsNullOrWhiteSpace(executablePath)
                    && runtimeRoots.Any(root => IsSameOrChildPath(root, executablePath))))
            {
                serviceNames.Add(serviceName);
            }
        }

        if (serviceNames.Count == 0)
        {
            Console.WriteLine("> No configured Windows services to remove.");
            return;
        }

        Console.WriteLine("> Remove Windows services");
        foreach (var serviceName in serviceNames)
        {
            DeleteWindowsService(serviceName);
        }
    }

    private static void AddWindowsServiceName(HashSet<string> serviceNames, string? serviceName)
    {
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            serviceNames.Add(serviceName.Trim());
        }
    }

    private static bool IsProductOwnedServiceName(string serviceName)
        => ProductOwnedServiceNamePrefixes.Any(prefix => serviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static List<string> GetServiceRuntimeRoots(HostAgentInstallOptions hostAgent)
    {
        var roots = new List<string>();
        AddRuntimeRoot(roots, hostAgent.InstallPath);
        AddRuntimeRoot(roots, hostAgent.ServicesRoot);
        return roots;
    }

    private static void AddRuntimeRoot(List<string> roots, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path.Trim());
        if (!roots.Any(root => root.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            roots.Add(fullPath);
        }
    }

    private static IEnumerable<string> EnumerateWindowsServiceNames()
    {
        var result = RunProcess(GetScPath(), ["query", "state=", "all"], throwOnFailure: false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"sc.exe failed with exit code {result.ExitCode} while listing Windows services: {result.StdOut}{result.StdErr}");
        }

        return result.StdOut
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(static rawLine => rawLine.Trim())
            .Where(static line => line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            .Select(static line => line["SERVICE_NAME:".Length..].Trim())
            .Where(static serviceName => !string.IsNullOrWhiteSpace(serviceName));
    }

    private static string GetWindowsServiceExecutablePath(string serviceName)
    {
        var result = RunProcess(GetScPath(), ["qc", serviceName], throwOnFailure: false);
        if (result.ExitCode != 0)
        {
            return string.Empty;
        }

        var line = result.StdOut
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(static rawLine => rawLine.Trim())
            .FirstOrDefault(static line => line.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase));
        if (line is not null)
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                return string.Empty;
            }

            return ExtractExecutablePath(line[(separator + 1)..]);
        }

        return string.Empty;
    }

    private static string ExtractExecutablePath(string pathName)
    {
        var trimmed = pathName.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
            {
                return Path.GetFullPath(trimmed[1..endQuote]);
            }
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            return Path.GetFullPath(trimmed[..(exeIndex + 4)]);
        }

        var firstToken = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstToken) ? string.Empty : Path.GetFullPath(firstToken);
    }

    private static void DeleteWindowsService(string serviceName)
    {
        if (!ServiceExists(serviceName))
        {
            return;
        }

        StopService(serviceName);
        Console.WriteLine($"  delete service {serviceName}");
        var result = RunProcess(GetScPath(), ["delete", serviceName], throwOnFailure: false);
        if (result.ExitCode != 0 && !IsScServiceNotFound(result))
        {
            throw new InvalidOperationException(
                $"sc.exe failed with exit code {result.ExitCode} while deleting Windows service '{serviceName}': {result.StdOut}{result.StdErr}");
        }
    }

    private static bool IsScServiceNotFound(ProcessResult result)
    {
        var text = result.StdOut + Environment.NewLine + result.StdErr;
        return result.ExitCode == 1060
            || text.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase)
            || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveIisSiteAndAppPools(HostAgentInstallOptions hostAgent)
    {
        var appCmdPath = TryGetAppCmdPath();
        if (string.IsNullOrWhiteSpace(appCmdPath))
        {
            Console.WriteLine("> IIS appcmd.exe was not found. Skipping IIS cleanup.");
            return;
        }

        Console.WriteLine("> Remove IIS site and app pools");
        var siteName = hostAgent.IisSiteName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(siteName)
            && RunProcess(appCmdPath, ["list", "site", $"/name:{siteName}"], throwOnFailure: false).ExitCode == 0)
        {
            Console.WriteLine($"  delete IIS site {siteName}");
            RunProcess(appCmdPath, ["delete", "site", $"/site.name:{siteName}"]);
        }

        var appPoolPrefix = hostAgent.IisAppPoolNamePrefix?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(appPoolPrefix))
        {
            Console.WriteLine("  app pool prefix is empty; skipping app pool cleanup.");
            return;
        }

        var appPoolsResult = RunProcess(appCmdPath, ["list", "apppool", "/text:name"], throwOnFailure: false);
        if (appPoolsResult.ExitCode != 0)
        {
            if (IsEmptyAppCmdListResult(appPoolsResult))
            {
                Console.WriteLine("  no IIS app pools found.");
                return;
            }

            throw new InvalidOperationException(
                $"appcmd.exe failed with exit code {appPoolsResult.ExitCode} while listing IIS app pools: {appPoolsResult.StdOut}{appPoolsResult.StdErr}");
        }

        foreach (var appPool in appPoolsResult.StdOut.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!appPool.StartsWith(appPoolPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Console.WriteLine($"  delete IIS app pool {appPool}");
            RunProcess(appCmdPath, ["delete", "apppool", $"/apppool.name:{appPool}"]);
        }
    }

    private static bool IsEmptyAppCmdListResult(ProcessResult result)
        => result.ExitCode == 1
            && string.IsNullOrWhiteSpace(result.StdOut)
            && string.IsNullOrWhiteSpace(result.StdErr);

    private static string TryGetAppCmdPath()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var appCmdPath = Path.Join(windows, "System32", "inetsrv", "appcmd.exe");
        return File.Exists(appCmdPath) ? appCmdPath : string.Empty;
    }

    private static void RemoveRuntimeDirectories(BootstrapConfig config)
    {
        Console.WriteLine("> Remove runtime directories");
        var paths = GetRuntimeDirectories(config)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length)
            .ToArray();

        foreach (var path in paths)
        {
            EnsureSafeRuntimeDeletePath(path);
            if (!Directory.Exists(path))
            {
                continue;
            }

            Console.WriteLine($"  remove {path}");
            TryDeleteDirectory(path);
        }
    }

    private static IEnumerable<string> GetRuntimeDirectories(BootstrapConfig config)
    {
        var hostAgent = config.HostAgent;
        var configuredPaths = new[]
            {
                hostAgent.PortalPhysicalPath,
                hostAgent.WebAppsRoot,
                hostAgent.LocalArtifactCacheRoot,
                config.ArtifactStoreRoot,
                hostAgent.InstallPath,
                hostAgent.ServicesRoot
            }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim());

        var hostAgentSettings = GetJsonObjectProperty(hostAgent.AppSettings, "HostAgent");
        var dataProtectionPath = GetJsonStringProperty(hostAgentSettings, "WebAppDataProtectionKeyPath");

        var artifactZipImport = GetJsonObjectProperty(hostAgentSettings, "ArtifactZipImport");
        var importPaths = new[] { "ImportPath", "ProcessedPath", "FailedPath" }
            .Select(property => GetJsonStringProperty(artifactZipImport, property))
            .Where(static path => !string.IsNullOrWhiteSpace(path));

        return configuredPaths
            .Concat(string.IsNullOrWhiteSpace(dataProtectionPath) ? [] : [dataProtectionPath])
            .Concat(importPaths);
    }

    private static JsonNode? GetJsonObjectProperty(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        return obj.FirstOrDefault(property => property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string GetJsonStringProperty(JsonNode? node, string propertyName)
    {
        var value = GetJsonObjectProperty(node, propertyName);
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;
    }

    private static int GetJsonIntProperty(JsonNode? node, string propertyName, int defaultValue)
    {
        var value = GetJsonObjectProperty(node, propertyName);
        if (value is not JsonValue jsonValue)
        {
            return defaultValue;
        }

        if (jsonValue.TryGetValue<int>(out var number))
        {
            return number;
        }

        return jsonValue.TryGetValue<string>(out var text)
            && int.TryParse(text, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static void EnsureSafeRuntimeDeletePath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root)
            || Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to remove unsafe runtime directory path: '{path}'.");
        }
    }

    private static bool ServiceExists(string serviceName)
    {
        var result = RunProcess(GetScPath(), ["query", serviceName], throwOnFailure: false);
        return result.ExitCode == 0;
    }

    private static bool HostAgentServiceWithPrefixExists(string serviceNamePrefix)
    {
        var prefix = ResolveHostAgentServiceNamePrefix(serviceNamePrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        return EnumerateHostAgentWindowsServices(prefix, installPath: null).Count > 0;
    }

    private static IReadOnlyList<WindowsServiceCandidate> EnumerateHostAgentWindowsServices(
        string serviceNamePrefix,
        string? installPath)
    {
        var prefixes = GetKnownHostAgentServiceNamePrefixes(serviceNamePrefix);
        return EnumerateWindowsServiceNames()
            .Select(serviceName => new WindowsServiceCandidate(
                serviceName,
                GetWindowsServiceExecutablePath(serviceName)))
            .Where(service =>
                IsKnownHostAgentServiceName(service.Name, prefixes)
                || IsHostAgentWindowsServiceExecutable(service.ExecutablePath, installPath))
            .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlySet<string> GetKnownHostAgentServiceNamePrefixes(string serviceNamePrefix)
    {
        var prefixes = new HashSet<string>(KnownHostAgentServiceNamePrefixes, StringComparer.OrdinalIgnoreCase);
        var prefix = ResolveHostAgentServiceNamePrefix(serviceNamePrefix);
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            prefixes.Add(prefix);
        }

        return prefixes;
    }

    private static bool IsKnownHostAgentServiceName(string serviceName, IEnumerable<string> serviceNamePrefixes)
        => serviceNamePrefixes.Any(prefix =>
            serviceName.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || serviceName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    private static bool IsHostAgentWindowsServiceExecutable(string executablePath, string? installPath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var fullExecutablePath = Path.GetFullPath(executablePath);
        if (!Path.GetFileName(fullExecutablePath).Equals(HostAgentWindowsServiceExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(installPath)
            || IsSameOrChildPath(installPath, fullExecutablePath);
    }

    private static void StopService(string serviceName)
    {
        var state = QueryServiceState(serviceName);
        if (!state.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
            && !state.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)
            && !state.Contains("PAUSED", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Console.WriteLine($"> Stop service {serviceName}");
        RunProcess(GetScPath(), ["stop", serviceName], throwOnFailure: false);
        WaitForServiceState(serviceName, "STOPPED", TimeSpan.FromSeconds(ServiceStopTimeoutSeconds));
    }

    private static void StartService(string serviceName)
    {
        Console.WriteLine($"> Start service {serviceName}");
        RunProcess(GetScPath(), ["start", serviceName]);
    }

    private static void CreateService(
        HostAgentInstallOptions hostAgent,
        HostAgentBootstrapServiceIdentity serviceIdentity,
        string executablePath,
        string serviceAccountPassword)
    {
        Console.WriteLine($"> Create service {serviceIdentity.ServiceName}");
        var arguments = CreateServiceArguments("create", hostAgent, serviceIdentity, executablePath, serviceAccountPassword);
        RunProcess(GetScPath(), arguments);
        SetServiceDescription(hostAgent, serviceIdentity.ServiceName);
    }

    private static void ConfigureService(
        HostAgentInstallOptions hostAgent,
        HostAgentBootstrapServiceIdentity serviceIdentity,
        string executablePath,
        string serviceAccountPassword)
    {
        Console.WriteLine($"> Configure service {serviceIdentity.ServiceName}");
        var arguments = CreateServiceArguments("config", hostAgent, serviceIdentity, executablePath, serviceAccountPassword);
        RunProcess(GetScPath(), arguments);
        SetServiceDescription(hostAgent, serviceIdentity.ServiceName);
    }

    private static string[] CreateServiceArguments(
        string verb,
        HostAgentInstallOptions hostAgent,
        HostAgentBootstrapServiceIdentity serviceIdentity,
        string executablePath,
        string serviceAccountPassword)
    {
        var arguments = new List<string>
        {
            verb,
            serviceIdentity.ServiceName,
            "binPath=",
            CreateHostAgentServiceBinaryPath(executablePath, serviceIdentity.ServiceName),
            "start=",
            "auto",
            "DisplayName=",
            serviceIdentity.DisplayName
        };

        if (!string.IsNullOrWhiteSpace(hostAgent.ServiceAccountName))
        {
            arguments.Add("obj=");
            arguments.Add(hostAgent.ServiceAccountName.Trim());

            if (!string.IsNullOrWhiteSpace(serviceAccountPassword))
            {
                arguments.Add("password=");
                arguments.Add(serviceAccountPassword);
            }
        }

        return [.. arguments];
    }

    private static string CreateHostAgentServiceBinaryPath(string executablePath, string serviceName)
    {
        var quotedExecutablePath = "\"" + executablePath.Trim().Trim('"') + "\"";
        return string.IsNullOrWhiteSpace(serviceName)
            ? quotedExecutablePath
            : $"{quotedExecutablePath} --service-name={serviceName.Trim()}";
    }

    private static void SetServiceDescription(HostAgentInstallOptions hostAgent, string serviceName)
    {
        if (!string.IsNullOrWhiteSpace(hostAgent.Description))
        {
            RunProcess(GetScPath(), ["description", serviceName, hostAgent.Description]);
        }
    }

    private static string ResolveSystemServiceDisplayName(
        string configuredDisplayName,
        string serviceName,
        string? version = null)
    {
        var displayName = string.IsNullOrWhiteSpace(configuredDisplayName)
            ? serviceName.Trim()
            : configuredDisplayName.Trim();

        if (displayName.StartsWith("OMP", StringComparison.OrdinalIgnoreCase))
        {
            return AppendDisplayVersion(displayName, version);
        }

        if (displayName.StartsWith("OpenModulePlatform ", StringComparison.OrdinalIgnoreCase))
        {
            return AppendDisplayVersion("OMP " + displayName["OpenModulePlatform ".Length..], version);
        }

        return AppendDisplayVersion("OMP " + displayName, version);
    }

    private static string AppendDisplayVersion(string displayName, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)
            || displayName.EndsWith(" " + version.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return displayName;
        }

        return $"{displayName} {version.Trim()}";
    }

    private static void WaitForServiceState(string serviceName, string expectedState, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (QueryServiceState(serviceName).Contains(expectedState, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(1000);
        }

        throw new TimeoutException($"Service '{serviceName}' did not reach state '{expectedState}' within {timeout.TotalSeconds:n0} seconds.");
    }

    private static string QueryServiceState(string serviceName)
    {
        var result = RunProcess(GetScPath(), ["query", serviceName], throwOnFailure: false);
        return result.StdOut + Environment.NewLine + result.StdErr;
    }

    private static ProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        bool throwOnFailure = true,
        string? workingDirectory = null,
        TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            info.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start process: {fileName}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = true;
        if (timeout is { } maxWait)
        {
            exited = process.WaitForExit(GetProcessTimeoutMilliseconds(maxWait));
        }
        else
        {
            process.WaitForExit();
        }

        if (!exited)
        {
            var timeoutMessage = $"{Path.GetFileName(fileName)} timed out after {timeout!.Value.TotalSeconds:n0} seconds.";
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                var killFailureMessage = $"{timeoutMessage} Could not terminate the process: {ex.Message}";
                if (throwOnFailure)
                {
                    throw new TimeoutException(killFailureMessage, ex);
                }

                return new ProcessResult(-1, string.Empty, killFailureMessage);
            }

            var timedOutStdout = stdoutTask.GetAwaiter().GetResult();
            var timedOutStderr = stderrTask.GetAwaiter().GetResult();
            var timedOutError = string.IsNullOrWhiteSpace(timedOutStderr)
                ? timeoutMessage
                : timedOutStderr + Environment.NewLine + timeoutMessage;
            if (throwOnFailure)
            {
                throw new TimeoutException($"{timeoutMessage}: {timedOutStdout}{timedOutError}");
            }

            return new ProcessResult(-1, timedOutStdout, timedOutError);
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} failed with exit code {process.ExitCode}: {stdout}{stderr}");
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static int GetProcessTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout.TotalMilliseconds <= 0)
        {
            return 1;
        }

        if (timeout.TotalMilliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)Math.Ceiling(timeout.TotalMilliseconds);
    }

    private static string GetScPath()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Join(windows, "System32", "sc.exe");
    }

    private static string CreateBackupPath(string installPath)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installPath))
            ?? throw new InvalidOperationException($"Cannot resolve parent folder for {installPath}.");
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(installPath));
        return Path.Join(parent + "Backups", $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}");
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var relativeDirectory in Directory
            .EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(directory => Path.GetRelativePath(sourceDirectory, directory)))
        {
            Directory.CreateDirectory(Path.Join(targetDirectory, relativeDirectory));
        }

        foreach (var relativeFile in Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(sourceDirectory, file)))
        {
            var target = Path.Join(targetDirectory, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Join(sourceDirectory, relativeFile), target, overwrite: true);
        }
    }

    private static void RemoveRuntimeConfigurationFiles(string root)
    {
        var runtimeConfigurationFiles = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var fileName = Path.GetFileName(file);
                return fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)
                    || (fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)
                        && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    || fileName.Equals("odv.site.config.js", StringComparison.OrdinalIgnoreCase);
            });

        foreach (var file in runtimeConfigurationFiles)
        {
            File.Delete(file);
        }
    }

    private static string ResolvePath(string root, string path)
        => Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Join(root, path));

    private static string ResolvePackageDataPath(string packageRoot, string path)
        => ResolvePath(packageRoot, path);

    private static string ResolvePackageDataPath(string packageRoot, string configPath, string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var candidate = EnumerateHostAndGlobalDataRoots(packageRoot, configPath)
            .Select(root => Path.GetFullPath(Path.Join(root, path)))
            .FirstOrDefault(candidate => File.Exists(candidate) || Directory.Exists(candidate));

        return candidate ?? ResolvePackageDataPath(packageRoot, path);
    }

    private static string ResolvePackageModuleDefinitionsRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "module-definitions"));

    private static string ResolvePackageArtifactsRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "artifacts"));

    private static string ResolvePackageHostConfigurationsRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "host-configs"));

    private static string ResolvePackageConfigOverlaysRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "config-overlays"));

    private static string ResolvePackageWidgetsRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "widgets"));

    private static string ResolvePackageWidgetDataRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "widget-data"));

    private static IEnumerable<string> EnumerateHostAndGlobalDataRoots(string packageRoot, string configPath)
    {
        var configKey = Path.GetFileNameWithoutExtension(configPath);
        if (!string.IsNullOrWhiteSpace(configKey))
        {
            yield return Path.Join(packageRoot, "data", "hosts", configKey);
            yield return Path.Join(packageRoot, "data", "profiles", configKey);
        }

        yield return Path.Join(packageRoot, "data", "global");
    }

    private static string CombineUnderRoot(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root.Trim());
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, relative.Trim().Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrChildPath(fullRoot, fullPath))
        {
            throw new InvalidOperationException($"Path '{relative}' escapes root path '{fullRoot}'.");
        }

        return fullPath;
    }

    private static bool IsSameOrChildPath(string rootPath, string candidatePath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));

        return candidate.Equals(root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static void TryDeleteFileOrDirectory(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else
        {
            TryDeleteDirectory(path);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string NormalizePathForMatch(string path)
        => path.Replace('\\', '/').TrimStart('/').Trim();

    private static void EnsureConsole()
    {
        // Intentionally managed-only: older versions attached the WinExe bootstrapper
        // to the parent console with kernel32, but the installer now uses GUI status
        // and log files as its supported diagnostic surface.
    }

    private static string ConvertToSqlBracketName(string value)
        => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string ConvertToSqlUnicodeLiteral(string value)
        => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [GeneratedRegex(@"^\s*:r\s+(?<path>.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SqlCmdIncludeRegex();

    [GeneratedRegex(@"(?im)^\s*USE\s+\[OpenModulePlatform\]\s*;?\s*$")]
    private static partial Regex UseDatabaseRegex();

    [GeneratedRegex(@"(?im)^\s*GO(?:\s+(?<repeat>[0-9]+))?\s*(?:--.*)?$")]
    private static partial Regex GoBatchRegex();

    [GeneratedRegex(@"(?m)^\s*DECLARE\s+@ArtifactVersion\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';\s*$")]
    private static partial Regex ArtifactVersionDeclarationRegex();

    [GeneratedRegex(@"DECLARE\s+@BootstrapPortalAdminPrincipal\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';")]
    private static partial Regex BootstrapPrincipalDeclarationRegex();

    [GeneratedRegex(@"DECLARE\s+@BootstrapPortalAdminPrincipalType\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';")]
    private static partial Regex BootstrapPrincipalTypeDeclarationRegex();

    [GeneratedRegex(@"(?im)^\s*USE\s+(?:\[[^\]]+\]|[A-Za-z0-9_]+)\s*;?\s*$")]
    private static partial Regex ModuleDefinitionUseDatabaseDirectiveRegex();

    [GeneratedRegex(@"(?is)\bDROP\s+(?:DATABASE|SCHEMA|TABLE)\b")]
    private static partial Regex ModuleDefinitionDropObjectRegex();

    [GeneratedRegex(@"(?is)\bTRUNCATE\s+TABLE\b")]
    private static partial Regex ModuleDefinitionTruncateTableRegex();

    [GeneratedRegex(@"(?is)(?:\A|(?<=\n)|;)[^\S\r\n]*DELETE\b(?<statement>.*?)(?:;|\r?\n\s*GO\b|\z)")]
    private static partial Regex ModuleDefinitionDeleteStatementRegex();

    [GeneratedRegex(@"(?is)\bWHERE\b")]
    private static partial Regex ModuleDefinitionWhereClauseRegex();

    [GeneratedRegex(@"(?is)\b(?:INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC(?:UTE)?|GRANT|REVOKE|DENY)\b")]
    private static partial Regex ModuleDefinitionReadOnlyBlockedCommandRegex();

}

internal sealed record PreparedArtifactConfigurationFiles(
    string ArtifactRelativePath,
    IReadOnlyList<ArtifactPackageConfigurationFile> ConfigurationFiles);

internal sealed record ArtifactConfigurationCopyTarget(
    int ArtifactId,
    string Version,
    int ConfigurationFileCount);

internal sealed record ArtifactConfigurationCopyCandidate(
    int ArtifactId,
    string Version);

internal sealed record ConfiguredArtifactIdentity(
    string ModuleKey,
    string AppKey,
    string PackageType,
    string TargetName,
    string Version);

internal sealed record AvailableArtifactPackage(
    ConfiguredArtifactIdentity Identity,
    string PackageRelativePath);

internal sealed class CliOptions
{
    public string ConfigPath { get; private init; } = string.Empty;

    public string ConfigDirectory { get; private init; } = string.Empty;

    public string PayloadRoot { get; private init; } = string.Empty;

    public string PayloadZipPath { get; private init; } = string.Empty;

    public bool Yes { get; private init; }

    public bool Gui { get; private init; }

    public bool ShowHelp { get; private init; }

    public bool RefreshInstallerPackage { get; private init; }

    public bool SyncPackageObjects { get; private init; }

    public bool SyncPackageObjectsBeforeAction { get; private init; }

    public bool FullContentCheck { get; private init; }

    public bool UpgradeOrComplete { get; private init; }

    public bool Uninstall { get; private init; }

    public bool RemoveRuntimeFiles { get; private init; }

    public bool RemoveDatabaseObjects { get; private init; }

    public int ParentProcessId { get; private init; }

    public bool RestartGui { get; private init; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptionsBuilder();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
                case "-c":
                case "--config":
                    options.ConfigPath = ReadValue(args, ref i, arg);
                    break;
                case "--config-dir":
                    options.ConfigDirectory = ReadValue(args, ref i, arg);
                    break;
                case "--payload-root":
                    options.PayloadRoot = ReadValue(args, ref i, arg);
                    break;
                case "--payload-zip":
                    options.PayloadZipPath = ReadValue(args, ref i, arg);
                    break;
                case "-y":
                case "--yes":
                    options.Yes = true;
                    break;
                case "--gui":
                    options.Gui = true;
                    break;
                case "--refresh-installer-package":
                    options.RefreshInstallerPackage = true;
                    break;
                case "--sync-package-objects":
                    options.SyncPackageObjects = true;
                    break;
                case "--sync-package-objects-before-action":
                    options.SyncPackageObjectsBeforeAction = true;
                    break;
                case "--full-content-check":
                    options.FullContentCheck = true;
                    break;
                case "--upgrade-or-complete":
                    options.UpgradeOrComplete = true;
                    break;
                case "--uninstall":
                    options.Uninstall = true;
                    break;
                case "--remove-runtime-files":
                    options.RemoveRuntimeFiles = true;
                    break;
                case "--remove-database-objects":
                    options.RemoveDatabaseObjects = true;
                    break;
                case "--parent-process-id":
                    options.ParentProcessId = int.Parse(ReadValue(args, ref i, arg));
                    break;
                case "--restart-gui":
                    options.RestartGui = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument: {arg}");
            }
        }

        return options.ToOptions();
    }

    private static string ReadValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {name}.");
        }

        index++;
        return args[index];
    }

    private sealed class CliOptionsBuilder
    {
        public string ConfigPath { get; set; } = string.Empty;

        public string ConfigDirectory { get; set; } = string.Empty;

        public string PayloadRoot { get; set; } = string.Empty;

        public string PayloadZipPath { get; set; } = string.Empty;

        public bool Yes { get; set; }

        public bool Gui { get; set; }

        public bool ShowHelp { get; set; }

        public bool RefreshInstallerPackage { get; set; }

        public bool SyncPackageObjects { get; set; }

        public bool SyncPackageObjectsBeforeAction { get; set; }

        public bool FullContentCheck { get; set; }

        public bool UpgradeOrComplete { get; set; }

        public bool Uninstall { get; set; }

        public bool RemoveRuntimeFiles { get; set; }

        public bool RemoveDatabaseObjects { get; set; }

        public int ParentProcessId { get; set; }

        public bool RestartGui { get; set; }

        public CliOptions ToOptions()
        {
            var selectedModes = new[] { UpgradeOrComplete, Uninstall, RefreshInstallerPackage, SyncPackageObjects }
                .Count(static item => item);
            if (selectedModes > 1)
            {
                throw new InvalidOperationException("Choose only one operation mode: bootstrap, upgrade/complete, uninstall, refresh-installer-package, or sync-package-objects.");
            }

            if ((RemoveRuntimeFiles || RemoveDatabaseObjects) && !Uninstall)
            {
                throw new InvalidOperationException("--remove-runtime-files and --remove-database-objects can only be used with --uninstall.");
            }

            if (SyncPackageObjectsBeforeAction && (SyncPackageObjects || RefreshInstallerPackage || Uninstall))
            {
                throw new InvalidOperationException("--sync-package-objects-before-action can only be used with bootstrap or --upgrade-or-complete.");
            }

            return new()
            {
                ConfigPath = ConfigPath,
                ConfigDirectory = ConfigDirectory,
                PayloadRoot = PayloadRoot,
                PayloadZipPath = PayloadZipPath,
                Yes = Yes,
                Gui = Gui,
                ShowHelp = ShowHelp,
                RefreshInstallerPackage = RefreshInstallerPackage,
                SyncPackageObjects = SyncPackageObjects,
                SyncPackageObjectsBeforeAction = SyncPackageObjectsBeforeAction,
                FullContentCheck = FullContentCheck,
                UpgradeOrComplete = UpgradeOrComplete,
                Uninstall = Uninstall,
                RemoveRuntimeFiles = RemoveRuntimeFiles,
                RemoveDatabaseObjects = RemoveDatabaseObjects,
                ParentProcessId = ParentProcessId,
                RestartGui = RestartGui
            };
        }
    }
}

internal sealed record ModuleDefinitionDocument(
    string ModuleKey,
    string DefinitionVersion,
    int FormatVersion,
    string DefinitionJson,
    string DefinitionSha256,
    IReadOnlyList<ModuleDefinitionCompatibilityEntry> CompatibleArtifacts);

internal sealed record ModuleDefinitionCompatibilityEntry(
    string AppKey,
    string PackageType,
    string? TargetName,
    string? RelativePathTemplate,
    string? MinArtifactVersion,
    string? MaxArtifactVersion);

internal sealed record AppliedModuleDefinition(
    int ModuleDefinitionDocumentId,
    string DefinitionVersion,
    string DefinitionSha256);

internal sealed record PortableModuleDefinitionSqlScript(
    string Key,
    string Phase,
    string Scope,
    int Order,
    string Execution,
    string? Path,
    string? Source,
    string? InlineSql,
    string? ContentEncoding,
    string? Content,
    string? Sha256);

internal sealed record ModuleDefinitionValidationResult(bool IsHealthy, string? Message);

internal enum ArtifactPreparationMode
{
    InstallOrUpdate,
    AddMissingOnly
}

internal sealed class BootstrapConfig
{
    public BootstrapProfileOptions Profile { get; set; } = new();

    public BootstrapSecurityOptions Security { get; set; } = new();

    public SqlBootstrapOptions Sql { get; set; } = new();

    public DeveloperSourceOptions DeveloperSource { get; set; } = new();

    public string ArtifactStoreRoot { get; set; } = string.Empty;

    public bool IncludeExampleApps { get; set; } = true;

    public List<ArtifactPayloadOptions> Artifacts { get; set; } = [];

    public HostAgentInstallOptions HostAgent { get; set; } = new();
}

internal sealed class BootstrapProfileOptions
{
    public string DisplayName { get; set; } = string.Empty;

    public List<string> MachineNames { get; set; } = [];
}

internal sealed class BootstrapSecurityOptions
{
    public string PortableEncryptionKey { get; set; } = string.Empty;

    public string PortableEncryptionKeyEnvironmentVariable { get; set; } = string.Empty;
}

internal sealed class DeveloperSourceOptions
{
    public string SourceRoot { get; set; } = string.Empty;

    public string PackageConfigPath { get; set; } = string.Empty;

    public string PackageOutputRoot { get; set; } = string.Empty;
}

internal sealed class SqlBootstrapOptions
{
    public bool Enabled { get; set; } = true;

    public string Server { get; set; } = "localhost";

    public string Database { get; set; } = "OpenModulePlatform";

    public bool IntegratedSecurity { get; set; } = true;

    public string UserId { get; set; } = string.Empty;

    public string? Password { get; set; }

    public bool TrustServerCertificate { get; set; } = true;

    public bool CreateDatabase { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 3600;

    public bool GrantRuntimeDatabaseAccess { get; set; }

    public string BootstrapPortalAdminPrincipal { get; set; } = string.Empty;

    public string BootstrapPortalAdminPrincipalType { get; set; } = "ADUser";

    public List<SqlScriptOptions> Scripts { get; set; } = [];

    public Dictionary<string, string> ArtifactVersionOverrides { get; set; } = new();

    public Dictionary<string, Dictionary<string, string>> ArtifactVersionVariableOverrides { get; set; } = new();
}

internal sealed class SqlScriptOptions
{
    public bool Enabled { get; set; } = true;

    public string Path { get; set; } = string.Empty;
}

internal sealed class ArtifactPayloadOptions
{
    public bool Enabled { get; set; } = true;

    public string Source { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public bool Overwrite { get; set; } = true;

    public bool RemoveRuntimeConfigurationFiles { get; set; } = true;

    public bool IsExample { get; set; }
}

internal sealed class HostAgentInstallOptions
{
    public bool Enabled { get; set; } = true;

    public string ServiceName { get; set; } = "OMP.HostAgent";

    public List<string> AdditionalServiceNamesToRemove { get; set; } = [];

    public string DisplayName { get; set; } = "OMP HostAgent";

    public string Description { get; set; } = "OpenModulePlatform artifact provisioning agent.";

    public string ServiceAccountName { get; set; } = string.Empty;

    public string ServiceAccountPassword { get; set; } = string.Empty;

    public string ServiceAccountCredentialKey { get; set; } = string.Empty;

    public string InstallPath { get; set; } = string.Empty;

    public string PackagePath { get; set; } = string.Empty;

    public bool BackupExistingInstall { get; set; } = true;

    public bool StartService { get; set; } = true;

    public string SettingsFileName { get; set; } = "appsettings.Production.json";

    public string LocalArtifactCacheRoot { get; set; } = string.Empty;

    public string HostKey { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public int RefreshSeconds { get; set; } = 30;

    public bool DeployWebApps { get; set; } = true;

    public string IisSiteName { get; set; } = string.Empty;

    public bool EnsureIisSite { get; set; }

    public string IisBindingProtocol { get; set; } = "http";

    public int IisBindingPort { get; set; } = 80;

    public string IisBindingHostHeader { get; set; } = string.Empty;

    public string IisBindingCertificateThumbprint { get; set; } = string.Empty;

    public string IisBindingCertificateSerialNumber { get; set; } = string.Empty;

    public string IisBindingCertificateStoreName { get; set; } = "My";

    public string WebAppsRoot { get; set; } = string.Empty;

    public string PortalPhysicalPath { get; set; } = string.Empty;

    public string IisAppPoolNamePrefix { get; set; } = "OMP_";

    public string IisAppPoolUserName { get; set; } = string.Empty;

    public string IisAppPoolPassword { get; set; } = string.Empty;

    public string IisAppPoolPasswordCredentialKey { get; set; } = string.Empty;

    public Dictionary<string, IisAppPoolIdentityOptions> IisAppPoolOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool DeployServiceApps { get; set; } = true;

    public string ServicesRoot { get; set; } = string.Empty;

    public string ServiceAppUserName { get; set; } = string.Empty;

    public string ServiceAppPassword { get; set; } = string.Empty;

    public string ServiceAppPasswordCredentialKey { get; set; } = string.Empty;

    public Dictionary<string, ServiceAppIdentityOptions> ServiceAppIdentityOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HostAgentCredentialStoreBootstrapOptions CredentialStore { get; set; } = new();

    public JsonNode? AppSettings { get; set; }
}

internal sealed record HostAgentBootstrapServiceIdentity(
    string ServiceNamePrefix,
    string ServiceName,
    string InstallPath,
    string DisplayName,
    string? Version);

internal sealed record WindowsServiceCandidate(
    string Name,
    string ExecutablePath);

internal sealed class IisAppPoolIdentityOptions
{
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string PasswordCredentialKey { get; set; } = string.Empty;
}

internal sealed class ServiceAppIdentityOptions
{
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string PasswordCredentialKey { get; set; } = string.Empty;
}

internal sealed class HostAgentCredentialStoreBootstrapOptions
{
    public string AutomationMode { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string ProtectionScope { get; set; } = HostAgentCredentialProtectionScopes.LocalMachine;

    public string EntropyPurpose { get; set; } = "OpenModulePlatform.HostAgent.CredentialStore.v1";
}

internal sealed record HostAgentCredentialBootstrapPlan(
    HostAgentCredentialStoreSettings StoreSettings,
    string ServiceAccountCredentialKey,
    string DefaultIisAppPoolCredentialKey,
    string DefaultServiceAppCredentialKey,
    IReadOnlyDictionary<string, HostAgentServiceAppIdentitySettings> ServiceAppIdentityOverrides,
    IReadOnlyDictionary<string, HostAgentIisAppPoolIdentitySettings> IisAppPoolOverrides,
    IReadOnlyList<HostAgentPlainTextCredential> Credentials);

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
