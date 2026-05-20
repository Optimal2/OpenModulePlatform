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

namespace OpenModulePlatform.Bootstrapper;

internal static partial class Program
{
    private const string BootstrapPrincipalPlaceholder = "__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__";
    private const int SqlDeadlockErrorNumber = 1205;
    private const int SqlDeadlockRetryCount = 3;
    private const int ServiceStopTimeoutSeconds = 60;
    private const int AttachParentProcess = -1;
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

            var preparedArtifactConfigurationFiles = PrepareArtifacts(config, payloadRoot);
            await RegisterPreparedArtifactConfigurationFilesAsync(config, preparedArtifactConfigurationFiles);

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

        return Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory;
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
        Console.WriteLine("  OpenModulePlatform.Bootstrapper.exe --gui --config <bootstrap.json> [--payload-root <path>] [--payload-zip <zip>]");
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

    private static async Task ImportModuleDefinitionsAsync(BootstrapConfig config, string payloadRoot)
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
            using var transaction = connection.BeginTransaction();

            try
            {
                var documentId = await UpsertModuleDefinitionDocumentAsync(
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

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            Console.WriteLine($"  {definition.ModuleKey} {definition.DefinitionVersion}");
        }
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

        var relative = NormalizePathForMatch(Path.GetRelativePath(payloadRoot, scriptPath));
        foreach (var item in options.ArtifactVersionOverrides)
        {
            var key = NormalizePathForMatch(item.Key);
            if (relative.Equals(key, StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(relative).Equals(key, StringComparison.OrdinalIgnoreCase))
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

        var relative = NormalizePathForMatch(Path.GetRelativePath(payloadRoot, scriptPath));
        foreach (var item in options.ArtifactVersionVariableOverrides)
        {
            var key = NormalizePathForMatch(item.Key);
            if (relative.Equals(key, StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(relative).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return item.Value;
            }
        }

        return EmptyStringDictionary;
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
        string payloadRoot)
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
        var stagingRoot = Path.Combine(Path.GetTempPath(), "OpenModulePlatform.HostAgent", Guid.NewGuid().ToString("N"));
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

            if (serviceExists)
            {
                ConfigureService(hostAgent, executablePath);
            }
            else
            {
                CreateService(hostAgent, executablePath);
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
        SynchronizeHostAgentSettings(settings, config);

        var fileName = string.IsNullOrWhiteSpace(hostAgent.SettingsFileName)
            ? "appsettings.Production.json"
            : hostAgent.SettingsFileName.Trim();
        var path = CombineUnderRoot(installPath, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

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

    private static void SynchronizeHostAgentSettings(JsonNode settings, BootstrapConfig config)
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
        hostAgentSettings["IisAppPoolPassword"] = hostAgent.IisAppPoolPassword;
        hostAgentSettings["DeployServiceApps"] = hostAgent.DeployServiceApps;
        hostAgentSettings["ServicesRoot"] = hostAgent.ServicesRoot;

        var selfUpgrade = GetOrCreateJsonObject(hostAgentSettings, "SelfUpgrade");
        selfUpgrade["InstallRoot"] = hostAgent.ServicesRoot;
        selfUpgrade["ServiceNamePrefix"] = hostAgent.ServiceName;
        selfUpgrade["ServiceAccountName"] = hostAgent.ServiceAccountName;
        selfUpgrade["ServiceAccountPassword"] = hostAgent.ServiceAccountPassword;
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
                        IisAppPoolPassword = hostAgent.IisAppPoolPassword,
                        DeployServiceApps = hostAgent.DeployServiceApps,
                        ServicesRoot = hostAgent.ServicesRoot,
                        SelfUpgrade = new
                        {
                            IsEnabled = true,
                            InstallRoot = hostAgent.ServicesRoot,
                            ServiceNamePrefix = hostAgent.ServiceName,
                            ServiceAccountName = hostAgent.ServiceAccountName,
                            ServiceAccountPassword = hostAgent.ServiceAccountPassword,
                            TakeoverStopTimeoutSeconds = 45,
                            DeletePreviousServiceAfterTakeover = true,
                            StartPreparedService = true
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

    private static void CreateService(HostAgentInstallOptions hostAgent, string executablePath)
    {
        Console.WriteLine($"> Create service {hostAgent.ServiceName}");
        var arguments = CreateServiceArguments("create", hostAgent, executablePath);
        RunProcess(GetScPath(), arguments);
        SetServiceDescription(hostAgent);
    }

    private static void ConfigureService(HostAgentInstallOptions hostAgent, string executablePath)
    {
        Console.WriteLine($"> Configure service {hostAgent.ServiceName}");
        var arguments = CreateServiceArguments("config", hostAgent, executablePath);
        RunProcess(GetScPath(), arguments);
        SetServiceDescription(hostAgent);
    }

    private static string[] CreateServiceArguments(
        string verb,
        HostAgentInstallOptions hostAgent,
        string executablePath)
    {
        var arguments = new List<string>
        {
            verb,
            hostAgent.ServiceName,
            "binPath=",
            executablePath,
            "start=",
            "auto",
            "DisplayName=",
            hostAgent.DisplayName
        };

        if (!string.IsNullOrWhiteSpace(hostAgent.ServiceAccountName))
        {
            arguments.Add("obj=");
            arguments.Add(hostAgent.ServiceAccountName.Trim());

            if (!string.IsNullOrWhiteSpace(hostAgent.ServiceAccountPassword))
            {
                arguments.Add("password=");
                arguments.Add(hostAgent.ServiceAccountPassword);
            }
        }

        return [.. arguments];
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

internal sealed class CliOptions
{
    public string ConfigPath { get; private init; } = string.Empty;

    public string PayloadRoot { get; private init; } = string.Empty;

    public string PayloadZipPath { get; private init; } = string.Empty;

    public bool Yes { get; private init; }

    public bool Gui { get; private init; }

    public bool ShowHelp { get; private init; }

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

        public string PayloadRoot { get; set; } = string.Empty;

        public string PayloadZipPath { get; set; } = string.Empty;

        public bool Yes { get; set; }

        public bool Gui { get; set; }

        public bool ShowHelp { get; set; }

        public CliOptions ToOptions()
            => new()
            {
                ConfigPath = ConfigPath,
                PayloadRoot = PayloadRoot,
                PayloadZipPath = PayloadZipPath,
                Yes = Yes,
                Gui = Gui,
                ShowHelp = ShowHelp
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

internal sealed class BootstrapConfig
{
    public SqlBootstrapOptions Sql { get; set; } = new();

    public string ArtifactStoreRoot { get; set; } = string.Empty;

    public bool IncludeExampleApps { get; set; } = true;

    public List<ArtifactPayloadOptions> Artifacts { get; set; } = [];

    public HostAgentInstallOptions HostAgent { get; set; } = new();
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

    public string ServiceName { get; set; } = "OpenModulePlatform.HostAgent";

    public List<string> AdditionalServiceNamesToRemove { get; set; } = [];

    public string DisplayName { get; set; } = "OpenModulePlatform HostAgent";

    public string Description { get; set; } = "OpenModulePlatform artifact provisioning agent.";

    public string ServiceAccountName { get; set; } = string.Empty;

    public string ServiceAccountPassword { get; set; } = string.Empty;

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

    public bool DeployServiceApps { get; set; } = true;

    public string ServicesRoot { get; set; } = string.Empty;

    public JsonNode? AppSettings { get; set; }
}

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
