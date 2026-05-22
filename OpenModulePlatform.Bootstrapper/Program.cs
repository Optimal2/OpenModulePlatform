using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
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
    private const int AttachParentProcess = -1;
    private const int ArtifactHashBufferSize = 64 * 1024;
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

            return await RunBootstrapAsync(cli);
        }
        catch (Exception ex)
        {
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
                temporaryPayloadRoot = Path.Combine(
                    Path.GetTempPath(),
                    "OpenModulePlatform.Bootstrapper",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryPayloadRoot);
                ZipFile.ExtractToDirectory(Path.GetFullPath(payloadZipPath), temporaryPayloadRoot, overwriteFiles: true);
                payloadRoot = temporaryPayloadRoot;
            }

            WritePlan(config, configPath, payloadRoot);
            if (!yes && !Confirm("Continue with OpenModulePlatform bootstrap?"))
            {
                Console.WriteLine("Bootstrap cancelled.");
                return 2;
            }

            if (config.Sql.Enabled)
            {
                await RunSqlAsync(config, payloadRoot);
                await ImportModuleDefinitionsAsync(config, payloadRoot);
            }

            var preparedArtifactConfigurationFiles = PrepareArtifacts(
                config,
                payloadRoot,
                ArtifactPreparationMode.InstallOrUpdate);
            await RegisterPackageArtifactsAsync(config);
            await RegisterPreparedArtifactConfigurationFilesAsync(config, preparedArtifactConfigurationFiles);
            PublishAvailableDeploymentObjects(config, payloadRoot);

            if (config.HostAgent.Enabled)
            {
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
        string payloadZipPath)
    {
        var temporaryPayloadRoot = string.Empty;

        try
        {
            if (!string.IsNullOrWhiteSpace(payloadZipPath))
            {
                temporaryPayloadRoot = Path.Combine(
                    Path.GetTempPath(),
                    "OpenModulePlatform.Bootstrapper",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryPayloadRoot);
                ZipFile.ExtractToDirectory(Path.GetFullPath(payloadZipPath), temporaryPayloadRoot, overwriteFiles: true);
                payloadRoot = temporaryPayloadRoot;
            }

            WritePlan(config, configPath, payloadRoot);

            if (config.Sql.Enabled)
            {
                await ImportModuleDefinitionsAsync(config, payloadRoot, onlyNewerOrChanged: true);
            }

            var preparedArtifactConfigurationFiles = PrepareArtifacts(
                config,
                payloadRoot,
                ArtifactPreparationMode.AddMissingOnly);
            await RegisterPackageArtifactsAsync(config);
            await RegisterPreparedArtifactConfigurationFilesAsync(config, preparedArtifactConfigurationFiles);
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
            else if (!ServiceExists(config.HostAgent.ServiceName))
            {
                Console.WriteLine($"> HostAgent service '{config.HostAgent.ServiceName}' is missing; installing it.");
                await InstallHostAgentAsync(config, payloadRoot);
            }
            else
            {
                Console.WriteLine("> HostAgent service already exists; leaving runtime installation unchanged.");
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

        var configDirectory = Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory;
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(configDirectory))
            .Equals("configs", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(configDirectory)) ?? configDirectory
            : configDirectory;
    }

    private static void WritePlan(BootstrapConfig config, string configPath, string payloadRoot)
    {
        Console.WriteLine("OpenModulePlatform HostAgent-first bootstrap");
        Console.WriteLine($"Config:         {configPath}");
        Console.WriteLine($"Payload root:   {payloadRoot}");
        Console.WriteLine($"SQL target:     {config.Sql.Server}/{config.Sql.Database}");
        Console.WriteLine($"Artifact store: {config.ArtifactStoreRoot}");
        Console.WriteLine($"HostAgent:      {config.HostAgent.ServiceName} -> {config.HostAgent.InstallPath}");
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
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --config <bootstrap.json> [--payload-root <path>] [--payload-zip <zip>] [--yes]");
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --gui [--config <bootstrap.json> | --config-dir <configs>] [--payload-root <path>] [--payload-zip <zip>]");
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --refresh-installer-package --config <bootstrap.json> --payload-root <path> [--parent-process-id <pid>] [--restart-gui]");
        Console.WriteLine();
        Console.WriteLine("The bootstrapper runs initial SQL, prepares ArtifactStore, and installs the HostAgent service.");
    }

    private static async Task RunSqlAsync(BootstrapConfig config, string payloadRoot)
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

            var scriptPath = ResolvePath(payloadRoot, script.Path);
            Console.WriteLine($"> SQL {scriptPath}");
            var sqlText = ReadSqlFile(scriptPath, sql, payloadRoot, config.IncludeExampleApps, []);
            await ExecuteSqlBatchesAsync(sql, sql.Database, sqlText, scriptPath);
        }
    }

    private static async Task ImportModuleDefinitionsAsync(
        BootstrapConfig config,
        string payloadRoot,
        bool onlyNewerOrChanged = false)
    {
        var definitionsRoot = Path.Combine(payloadRoot, "module-definitions");
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

        Console.WriteLine("> Module definitions");

        await using var connection = new SqlConnection(BuildConnectionString(config.Sql, config.Sql.Database));
        await connection.OpenAsync();

        foreach (var definitionPath in definitionPaths)
        {
            var definition = await ReadModuleDefinitionAsync(definitionPath);
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
            using var transaction = connection.BeginTransaction();

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

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
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
        if (IsInstallerManagedModuleDefinitionSql(definition.DefinitionJson))
        {
            return 0;
        }

        var scripts = ReadPortableSqlScripts(definition.DefinitionJson)
            .OrderBy(static script => script.Order)
            .ThenBy(static script => script.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scripts.Count == 0)
        {
            return 0;
        }

        await AcquireModuleDefinitionSqlExecutionLockAsync(connection, sql.CommandTimeoutSeconds);

        var executed = 0;
        foreach (var script in scripts)
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

    private static string? ValidateSafeModuleDefinitionSql(string sqlText)
    {
        if (Regex.IsMatch(sqlText, @"(?im)^\s*USE\s+(?:\[[^\]]+\]|[A-Za-z0-9_]+)\s*;?\s*$"))
        {
            return "Module definition SQL must not contain USE database directives.";
        }

        if (Regex.IsMatch(sqlText, @"(?is)\bDROP\s+(?:DATABASE|SCHEMA|TABLE)\b"))
        {
            return "The script contains DROP DATABASE, DROP SCHEMA, or DROP TABLE.";
        }

        if (Regex.IsMatch(sqlText, @"(?is)\bTRUNCATE\s+TABLE\b"))
        {
            return "The script contains TRUNCATE TABLE.";
        }

        foreach (Match match in Regex.Matches(sqlText, @"(?is)\bDELETE\s+FROM\b(?<statement>.*?)(?:;|\r?\n\s*GO\b|$)"))
        {
            var statement = match.Groups["statement"].Value;
            if (!Regex.IsMatch(statement, @"(?is)\bWHERE\b"))
            {
                return "The script contains DELETE FROM without a WHERE clause.";
            }
        }

        return null;
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
                    var resolvedInclude = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, includePath));
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

            var source = ResolvePath(payloadRoot, artifact.Source);
            var target = CombineUnderRoot(artifactStoreRoot, artifact.Target);
            if (mode == ArtifactPreparationMode.AddMissingOnly
                && (File.Exists(target) || Directory.Exists(target)))
            {
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
                var stagingPath = Path.Combine(
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
                File.Copy(source, Path.Combine(target, Path.GetFileName(source)), overwrite: true);
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

    private static async Task RegisterPackageArtifactsAsync(BootstrapConfig config)
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
            var sha256 = ComputeDirectorySha256(targetPath);
            await UpsertArtifactMetadataAsync(
                connection,
                appId.Value,
                identity.Version,
                identity.PackageType,
                identity.TargetName,
                relativePath,
                sha256);

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
        var availableRoot = Path.Combine(artifactStoreRoot, "_available");
        var definitionsCopied = CopyAvailableDeploymentObjects(
            Path.Combine(payloadRoot, "available-module-definitions"),
            Path.Combine(availableRoot, "module-definitions"),
            "*.json",
            overwrite);
        var artifactsCopied = CopyAvailableDeploymentObjects(
            Path.Combine(payloadRoot, "available-artifacts"),
            Path.Combine(availableRoot, "artifacts"),
            "*.zip",
            overwrite);

        if (definitionsCopied > 0 || artifactsCopied > 0)
        {
            Console.WriteLine(
                $"> Available package library: {definitionsCopied} module definition(s), {artifactsCopied} artifact package(s)");
        }
    }

    private static int CopyAvailableDeploymentObjects(
        string sourceRoot,
        string targetRoot,
        string searchPattern,
        bool overwrite)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return 0;
        }

        Directory.CreateDirectory(targetRoot);
        var copied = 0;
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, searchPattern, SearchOption.TopDirectoryOnly))
        {
            var targetPath = Path.Combine(targetRoot, Path.GetFileName(sourcePath));
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

        var packagePath = ResolvePath(payloadRoot, hostAgent.PackagePath);
        var installPath = Path.GetFullPath(hostAgent.InstallPath.Trim());
        var stagingRoot = Path.Combine(Path.GetTempPath(), "OMP.HostAgent", Guid.NewGuid().ToString("N"));
        var sourceDirectory = packagePath;

        try
        {
            if (File.Exists(packagePath) && packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(stagingRoot);
                ZipFile.ExtractToDirectory(packagePath, stagingRoot, overwriteFiles: true);
                sourceDirectory = stagingRoot;
            }

            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException($"HostAgent package folder was not found: {sourceDirectory}");
            }

            var serviceExists = ServiceExists(hostAgent.ServiceName);
            if (serviceExists)
            {
                StopService(hostAgent.ServiceName);
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
            await WriteHostAgentSettingsAsync(config, installPath);

            var executablePath = Path.Combine(installPath, "OpenModulePlatform.HostAgent.WindowsService.exe");
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException("HostAgent executable was not found after installation.", executablePath);
            }

            RunHostAgentOnce(executablePath, installPath);
            var serviceAccountPassword = ResolveInstallerSecret(
                hostAgent.ServiceAccountPassword,
                config,
                "HostAgent:ServiceAccountPassword");

            if (serviceExists)
            {
                ConfigureService(hostAgent, executablePath, serviceAccountPassword);
            }
            else
            {
                CreateService(hostAgent, executablePath, serviceAccountPassword);
            }

            if (hostAgent.StartService)
            {
                StartService(hostAgent.ServiceName);
            }
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static async Task WriteHostAgentSettingsAsync(BootstrapConfig config, string installPath)
    {
        var hostAgent = config.HostAgent;
        var settings = hostAgent.AppSettings?.DeepClone()
            ?? CreateDefaultHostAgentSettings(config);
        var credentialPlan = CreateHostAgentCredentialPlan(config, installPath);

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SqlConnectionString"] = BuildConnectionString(config.Sql, config.Sql.Database),
            ["ArtifactStoreRoot"] = Path.GetFullPath(config.ArtifactStoreRoot.Trim()),
            ["HostAgent.InstallPath"] = Path.GetFullPath(hostAgent.InstallPath.Trim()),
            ["HostAgent.LocalArtifactCacheRoot"] = hostAgent.LocalArtifactCacheRoot,
            ["HostAgent.HostKey"] = hostAgent.HostKey,
            ["HostAgent.HostName"] = hostAgent.HostName,
            ["HostAgent.ServiceName"] = hostAgent.ServiceName
        };

        ReplaceTokens(settings, tokens);
        SynchronizeHostAgentSettings(settings, config, credentialPlan);

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

    private static void RunHostAgentOnce(string executablePath, string installPath)
    {
        Console.WriteLine("> Run HostAgent once");
        RunProcess(executablePath, ["--run-once"], workingDirectory: installPath);
    }

    private static void SynchronizeHostAgentSettings(
        JsonNode settings,
        BootstrapConfig config,
        HostAgentCredentialBootstrapPlan credentialPlan)
    {
        if (settings is not JsonObject root)
        {
            throw new InvalidOperationException("HostAgent appsettings root must be a JSON object.");
        }

        var hostAgent = config.HostAgent;
        var connectionStrings = GetOrCreateJsonObject(root, "ConnectionStrings");
        connectionStrings["OmpDb"] = BuildConnectionString(config.Sql, config.Sql.Database);

        var hostAgentSettings = GetOrCreateJsonObject(root, "HostAgent");
        hostAgentSettings["ServiceName"] = hostAgent.ServiceName;
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
        selfUpgrade["ServiceNamePrefix"] = hostAgent.ServiceName;
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
            ? Path.Combine(Path.GetFullPath(installPath), "hostagent.credentials.json")
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

    private static JsonNode CreateDefaultHostAgentSettings(BootstrapConfig config)
    {
        var hostAgent = config.HostAgent;
        var localArtifactCacheRoot = string.IsNullOrWhiteSpace(hostAgent.LocalArtifactCacheRoot)
            ? Path.Combine(Path.GetFullPath(hostAgent.InstallPath.Trim()), "ArtifactCache")
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

            var inMemberList = false;
            foreach (var rawLine in result.StdOut.Split([Environment.NewLine], StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("---", StringComparison.Ordinal))
                {
                    inMemberList = true;
                    continue;
                }

                if (!inMemberList
                    || line.Contains("command completed", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("kommandot slutf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                members.Add(NormalizeWindowsAccount(line));
            }

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

    private static void RemoveWindowsServices(BootstrapConfig config)
    {
        var serviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddWindowsServiceName(serviceNames, config.HostAgent.ServiceName);
        foreach (var serviceName in config.HostAgent.AdditionalServiceNamesToRemove ?? [])
        {
            AddWindowsServiceName(serviceNames, serviceName);
        }

        var runtimeRoots = GetServiceRuntimeRoots(config.HostAgent);
        if (runtimeRoots.Count > 0)
        {
            foreach (var serviceName in EnumerateWindowsServiceNames())
            {
                var executablePath = GetWindowsServiceExecutablePath(serviceName);
                if (!string.IsNullOrWhiteSpace(executablePath)
                    && runtimeRoots.Any(root => IsSameOrChildPath(root, executablePath)))
                {
                    serviceNames.Add(serviceName);
                }
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

        foreach (var rawLine in result.StdOut.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var serviceName = line["SERVICE_NAME:".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                yield return serviceName;
            }
        }
    }

    private static string GetWindowsServiceExecutablePath(string serviceName)
    {
        var result = RunProcess(GetScPath(), ["qc", serviceName], throwOnFailure: false);
        if (result.ExitCode != 0)
        {
            return string.Empty;
        }

        foreach (var rawLine in result.StdOut.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
        var appCmdPath = Path.Combine(windows, "System32", "inetsrv", "appcmd.exe");
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
        foreach (var path in new[]
        {
            hostAgent.PortalPhysicalPath,
            hostAgent.WebAppsRoot,
            hostAgent.LocalArtifactCacheRoot,
            config.ArtifactStoreRoot,
            hostAgent.InstallPath,
            hostAgent.ServicesRoot
        })
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path.Trim();
            }
        }

        var hostAgentSettings = GetJsonObjectProperty(hostAgent.AppSettings, "HostAgent");
        var dataProtectionPath = GetJsonStringProperty(hostAgentSettings, "WebAppDataProtectionKeyPath");
        if (!string.IsNullOrWhiteSpace(dataProtectionPath))
        {
            yield return dataProtectionPath;
        }

        var artifactZipImport = GetJsonObjectProperty(hostAgentSettings, "ArtifactZipImport");
        foreach (var property in new[] { "ImportPath", "ProcessedPath", "FailedPath" })
        {
            var path = GetJsonStringProperty(artifactZipImport, property);
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }

    private static JsonNode? GetJsonObjectProperty(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var property in obj)
        {
            if (property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
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
        string executablePath,
        string serviceAccountPassword)
    {
        Console.WriteLine($"> Create service {hostAgent.ServiceName}");
        var arguments = CreateServiceArguments("create", hostAgent, executablePath, serviceAccountPassword);
        RunProcess(GetScPath(), arguments);
        SetServiceDescription(hostAgent);
    }

    private static void ConfigureService(
        HostAgentInstallOptions hostAgent,
        string executablePath,
        string serviceAccountPassword)
    {
        Console.WriteLine($"> Configure service {hostAgent.ServiceName}");
        var arguments = CreateServiceArguments("config", hostAgent, executablePath, serviceAccountPassword);
        RunProcess(GetScPath(), arguments);
        SetServiceDescription(hostAgent);
    }

    private static string[] CreateServiceArguments(
        string verb,
        HostAgentInstallOptions hostAgent,
        string executablePath,
        string serviceAccountPassword)
    {
        var arguments = new List<string>
        {
            verb,
            hostAgent.ServiceName,
            "binPath=",
            CreateHostAgentServiceBinaryPath(executablePath, hostAgent.ServiceName),
            "start=",
            "auto",
            "DisplayName=",
            hostAgent.DisplayName
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

    private static void SetServiceDescription(HostAgentInstallOptions hostAgent)
    {
        if (!string.IsNullOrWhiteSpace(hostAgent.Description))
        {
            RunProcess(GetScPath(), ["description", hostAgent.ServiceName, hostAgent.Description]);
        }
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
        string? workingDirectory = null)
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
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} failed with exit code {process.ExitCode}: {stdout}{stderr}");
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string GetScPath()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Combine(windows, "System32", "sc.exe");
    }

    private static string CreateBackupPath(string installPath)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installPath))
            ?? throw new InvalidOperationException($"Cannot resolve parent folder for {installPath}.");
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(installPath));
        return Path.Combine(parent + "Backups", $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}");
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var target = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void RemoveRuntimeConfigurationFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)
                || (fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                || fileName.Equals("odv.site.config.js", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }
    }

    private static string ResolvePath(string root, string path)
        => Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));

    private static string CombineUnderRoot(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root.Trim());
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Trim().Replace('/', Path.DirectorySeparatorChar)));
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
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            return;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
}

internal sealed record PreparedArtifactConfigurationFiles(
    string ArtifactRelativePath,
    IReadOnlyList<ArtifactPackageConfigurationFile> ConfigurationFiles);

internal sealed record ConfiguredArtifactIdentity(
    string ModuleKey,
    string AppKey,
    string PackageType,
    string TargetName,
    string Version);

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

        public int ParentProcessId { get; set; }

        public bool RestartGui { get; set; }

        public CliOptions ToOptions()
            => new()
            {
                ConfigPath = ConfigPath,
                ConfigDirectory = ConfigDirectory,
                PayloadRoot = PayloadRoot,
                PayloadZipPath = PayloadZipPath,
                Yes = Yes,
                Gui = Gui,
                ShowHelp = ShowHelp,
                RefreshInstallerPackage = RefreshInstallerPackage,
                ParentProcessId = ParentProcessId,
                RestartGui = RestartGui
            };
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

    public string DisplayName { get; set; } = "OpenModulePlatform HostAgent";

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
