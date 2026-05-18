using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Bootstrapper;

internal static partial class Program
{
    private const string BootstrapPrincipalPlaceholder = "__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__";
    private const int SqlDeadlockErrorNumber = 1205;
    private const int SqlDeadlockRetryCount = 3;
    private const int ServiceStopTimeoutSeconds = 60;
    private const int AttachParentProcess = -1;
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
            }

            PrepareArtifacts(config, payloadRoot);

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

    private static void PrepareArtifacts(BootstrapConfig config, string payloadRoot)
    {
        if (string.IsNullOrWhiteSpace(config.ArtifactStoreRoot))
        {
            throw new InvalidOperationException("ArtifactStoreRoot must be configured.");
        }

        var artifactStoreRoot = Path.GetFullPath(config.ArtifactStoreRoot.Trim());
        Directory.CreateDirectory(artifactStoreRoot);

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

            if (artifact.Overwrite && (File.Exists(target) || Directory.Exists(target)))
            {
                TryDeleteFileOrDirectory(target);
            }

            if (Directory.Exists(source))
            {
                CopyDirectory(source, target);
            }
            else if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(source, target, overwriteFiles: true);
            }
            else if (File.Exists(source))
            {
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

    private static ProcessResult RunProcess(string fileName, IReadOnlyList<string> arguments, bool throwOnFailure = true)
    {
        var info = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

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
