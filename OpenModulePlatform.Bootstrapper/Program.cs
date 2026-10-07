using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
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
using OpenModulePlatform.Installation;
using static OpenModulePlatform.Installation.InstallationEngine;

[assembly: InternalsVisibleTo("OpenModulePlatform.Bootstrapper.Tests")]

namespace OpenModulePlatform.Bootstrapper;

internal static partial class Program
{
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

            if (cli.RefreshAndStagePackage)
            {
                EnsureConsole();
                return await RunRefreshAndStagePackageAsync(cli);
            }

            if (cli.CheckDeveloperSourceStatus)
            {
                EnsureConsole();
                return await RunCheckDeveloperSourceStatusAsync(cli);
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
                await CreateDatabaseIfConfiguredAsync(config);
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
        if (answer is null)
        {
            // R5-G3: no interactive input (EOF/redirected/detached console).
            // Returning false silently aborted the run with exit code 2 and no
            // explanation; state the reason so the operator can react.
            Console.WriteLine();
            Console.WriteLine("No interactive console is available to confirm. Re-run with --yes to proceed non-interactively, or launch the graphical installer (install-hostagent-first.cmd).");
            return false;
        }

        return string.Equals(answer.Trim(), "Y", StringComparison.OrdinalIgnoreCase);
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
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --refresh-installer-package --config <bootstrap.json> [--payload-root <path>] [--parent-process-id <pid>] [--restart-gui] [--log-file <path>]");
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --sync-package-objects --config <bootstrap.json> [--payload-root <path>] [--full-content-check]");
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --refresh-and-stage-package --config <bootstrap.json> [--payload-root <path>] [--full-content-check] [--skip-refresh] [--wait-for-import | --wait-for-import-seconds <n>]");
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --check-developer-source-status --config <bootstrap.json> [--payload-root <path>] [--json]");
        Console.WriteLine();
        Console.WriteLine("Updating an EXISTING installation:");
        Console.WriteLine("  Use --refresh-and-stage-package. It refreshes the installer data folder from every configured");
        Console.WriteLine("  source repository, builds one global universal package, and stages it in this host's HostAgent");
        Console.WriteLine("  import folder (resolved from the config). Add --wait-for-import to follow the HostAgent import.");
        Console.WriteLine("  Do NOT use --upgrade-or-complete against an existing installation; that is for new installs.");
        Console.WriteLine("  Note: --refresh-installer-package rebuilds the installer BINARIES and is a different operation.");
        Console.WriteLine();
        Console.WriteLine("Checking an EXISTING installation (read-only):");
        Console.WriteLine("  Use --check-developer-source-status. It compares the developer source manifests against this");
        Console.WriteLine("  installer package and the INSTALLED database (applied module definitions and artifact versions)");
        Console.WriteLine("  and never writes anything. Exit codes: 0 = up to date, 2 = updates pending, 1 = the check");
        Console.WriteLine("  could not be completed (for example the database could not be read). Add --json for structured");
        Console.WriteLine("  output. For per-host RUNTIME drift, use scripts/diagnostics/Get-OmpDeploymentDrift.ps1 instead.");
        Console.WriteLine();
        Console.WriteLine("The bootstrapper runs initial SQL, prepares ArtifactStore, and installs the HostAgent service.");
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

    private static readonly Environment.SpecialFolder[] ProtectedRuntimeDeleteFolders =
    [
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.UserProfile
    ];

    private static void EnsureSafeRuntimeDeletePath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root)
            || Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to remove unsafe runtime directory path: '{path}'.");
        }

        // A runtime root may live UNDER these folders (C:\ProgramData\OMP is common); it
        // must never be one of them or a parent of one.
        foreach (var folder in ProtectedRuntimeDeleteFolders)
        {
            var protectedPath = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(protectedPath) && IsSameOrChildPath(path, protectedPath))
            {
                throw new InvalidOperationException($"Refusing to remove runtime directory path '{path}' because it contains '{protectedPath}'.");
            }
        }
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;

    private static StreamWriter? _attachedConsoleOut;

    private static StreamWriter? _attachedConsoleError;

    private static StreamReader? _attachedConsoleIn;

    // Bound at call time through NativeLibrary and a delegate rather than declared as an
    // extern P/Invoke: the console attach is a one-shot Windows-only call at startup, and
    // binding by hand keeps the interop surface a single plain delegate.
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(
        System.Runtime.InteropServices.CallingConvention.Winapi, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private delegate bool AttachConsoleFn(uint dwProcessId);

    private static bool AttachConsole(uint processId)
    {
        var kernel32 = System.Runtime.InteropServices.NativeLibrary.Load("kernel32.dll");
        var attach = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<AttachConsoleFn>(
            System.Runtime.InteropServices.NativeLibrary.GetExport(kernel32, "AttachConsole"));
        return attach(processId);
    }

    private static void EnsureConsole()
    {
        // R5-G3: the Bootstrapper is a GUI-subsystem (WinExe) process, so when it
        // is launched from a console (install-hostagent-first-console.cmd) its
        // stdout/stderr/stdin are not wired to that console and every
        // Console.Write is silently discarded - the operator sees nothing and
        // only an exit code. Attach to the launching process's console (if any)
        // and rebind the standard streams so the console entrypoint produces
        // output and can read confirmations. A double-click launch has no parent
        // console; AttachConsole fails there and the streams are left as-is.
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
            // Held in fields rather than left as locals. These readers and writers are
            // handed to Console and must live as long as the process does -- disposing
            // them would close the very streams they were created to rebind. CodeQL read
            // the locals as leaked disposables (cs/local-not-disposed), which is the right
            // question to ask; the answer is that their lifetime is the process, and
            // saying so in a field is clearer than an annotation claiming the analyzer is
            // wrong.
            _attachedConsoleOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            _attachedConsoleError = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            _attachedConsoleIn = new StreamReader(Console.OpenStandardInput());

            Console.SetOut(_attachedConsoleOut);
            Console.SetError(_attachedConsoleError);
            Console.SetIn(_attachedConsoleIn);
        }
        catch (IOException)
        {
            // Rebinding the streams is best-effort; a failure here must not abort the run.
        }
    }

    // Forwarders for the installation engine members the test suite exercises through
    // Program (the engine itself lives in OpenModulePlatform.Installation).
    internal static string NormalizeWindowsAccount(string value)
        => InstallationEngine.NormalizeWindowsAccount(value);

    internal static string? ValidateSafeModuleDefinitionSql(string sqlText)
        => InstallationEngine.ValidateSafeModuleDefinitionSql(sqlText);

    internal static bool IsSupersededByNewerPackageDefinition(
        ModuleDefinitionDocument definition,
        IEnumerable<ModuleDefinitionDocument> packageDefinitions)
        => InstallationEngine.IsSupersededByNewerPackageDefinition(definition, packageDefinitions);

    internal static int CompareVersionText(string left, string right)
        => InstallationEngine.CompareVersionText(left, right);

    internal static IReadOnlyList<ArtifactPackageConfigurationFile> ReadArtifactPackageConfigurationFilesOnly(string source)
        => InstallationEngine.ReadArtifactPackageConfigurationFilesOnly(source);

    internal static IReadOnlyList<PortableModuleDefinitionSqlScript> ReadPortableSqlScripts(string definitionJson)
        => InstallationEngine.ReadPortableSqlScripts(definitionJson);

    internal static string? ResolvePortableSqlText(PortableModuleDefinitionSqlScript script)
        => InstallationEngine.ResolvePortableSqlText(script);

}

internal sealed class CliOptions
{
    // Matches the AI Orchestrator's import watch window.
    public const int DefaultImportWaitSeconds = 180;

    public string ConfigPath { get; private init; } = string.Empty;

    public string ConfigDirectory { get; private init; } = string.Empty;

    public string PayloadRoot { get; private init; } = string.Empty;

    public string PayloadZipPath { get; private init; } = string.Empty;

    public bool Yes { get; private init; }

    public bool Gui { get; private init; }

    public bool ShowHelp { get; private init; }

    public bool RefreshInstallerPackage { get; private init; }

    public bool SyncPackageObjects { get; private init; }

    // Complete update flow for an existing installation: refresh the installer data
    // folder from all source repos -> build one global universal package -> stage it
    // in this host's HostAgent import folder.
    public bool RefreshAndStagePackage { get; private init; }

    // Build+stage from the existing data folder without refreshing it first.
    public bool SkipRefresh { get; private init; }

    // Read-only check: compares developer source manifests against this installer
    // package and the INSTALLED database (applied module definitions and artifact
    // versions). Never writes to the database or the installation.
    public bool CheckDeveloperSourceStatus { get; private init; }

    // Structured JSON output for --check-developer-source-status.
    public bool Json { get; private init; }

    // 0 = stage and return; >0 = also wait that many seconds for the HostAgent to
    // consume the staged package and report processed/failed.
    public int WaitForImportSeconds { get; private init; }

    public bool SyncPackageObjectsBeforeAction { get; private init; }

    public bool FullContentCheck { get; private init; }

    public bool UpgradeOrComplete { get; private init; }

    public bool Uninstall { get; private init; }

    public bool RemoveRuntimeFiles { get; private init; }

    public bool RemoveDatabaseObjects { get; private init; }

    public int ParentProcessId { get; private init; }

    public bool RestartGui { get; private init; }

    public string LogFilePath { get; private init; } = string.Empty;

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
                case "--refresh-and-stage-package":
                    options.RefreshAndStagePackage = true;
                    break;
                case "--check-developer-source-status":
                    options.CheckDeveloperSourceStatus = true;
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--skip-refresh":
                    options.SkipRefresh = true;
                    break;
                case "--wait-for-import":
                    options.WaitForImportSeconds = DefaultImportWaitSeconds;
                    break;
                case "--wait-for-import-seconds":
                    options.WaitForImportSeconds = int.Parse(ReadValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
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
                case "--log-file":
                    options.LogFilePath = ReadValue(args, ref i, arg);
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

        public bool RefreshAndStagePackage { get; set; }

        public bool CheckDeveloperSourceStatus { get; set; }

        public bool Json { get; set; }

        public bool SkipRefresh { get; set; }

        public int WaitForImportSeconds { get; set; }

        public bool SyncPackageObjectsBeforeAction { get; set; }

        public bool FullContentCheck { get; set; }

        public bool UpgradeOrComplete { get; set; }

        public bool Uninstall { get; set; }

        public bool RemoveRuntimeFiles { get; set; }

        public bool RemoveDatabaseObjects { get; set; }

        public int ParentProcessId { get; set; }

        public bool RestartGui { get; set; }

        public string LogFilePath { get; set; } = string.Empty;

        public CliOptions ToOptions()
        {
            var selectedModes = new[] { UpgradeOrComplete, Uninstall, RefreshInstallerPackage, SyncPackageObjects, RefreshAndStagePackage, CheckDeveloperSourceStatus }
                .Count(static item => item);
            if (selectedModes > 1)
            {
                throw new InvalidOperationException("Choose only one operation mode: bootstrap, upgrade/complete, uninstall, refresh-installer-package, sync-package-objects, refresh-and-stage-package, or check-developer-source-status.");
            }

            if (Json && !CheckDeveloperSourceStatus)
            {
                throw new InvalidOperationException("--json can only be used with --check-developer-source-status.");
            }

            if ((SkipRefresh || WaitForImportSeconds > 0) && !RefreshAndStagePackage)
            {
                throw new InvalidOperationException("--skip-refresh and --wait-for-import can only be used with --refresh-and-stage-package.");
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
                RefreshAndStagePackage = RefreshAndStagePackage,
                CheckDeveloperSourceStatus = CheckDeveloperSourceStatus,
                Json = Json,
                SkipRefresh = SkipRefresh,
                WaitForImportSeconds = WaitForImportSeconds,
                SyncPackageObjectsBeforeAction = SyncPackageObjectsBeforeAction,
                FullContentCheck = FullContentCheck,
                UpgradeOrComplete = UpgradeOrComplete,
                Uninstall = Uninstall,
                RemoveRuntimeFiles = RemoveRuntimeFiles,
                RemoveDatabaseObjects = RemoveDatabaseObjects,
                ParentProcessId = ParentProcessId,
                RestartGui = RestartGui,
                LogFilePath = LogFilePath
            };
        }
    }
}
