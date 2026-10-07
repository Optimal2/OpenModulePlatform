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

namespace OpenModulePlatform.Installation;

public static partial class InstallationEngine
{
    public static async Task CreateDatabaseIfConfiguredAsync(BootstrapConfig config)
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
    }

    public static async Task RunSqlAsync(BootstrapConfig config, string configPath, string payloadRoot)
    {
        var sql = config.Sql;
        if (string.IsNullOrWhiteSpace(sql.Database))
        {
            throw new InvalidOperationException("Sql:Database must be configured.");
        }

        foreach (var script in sql.Scripts.Where(static item => item.Enabled))
        {
            if (string.IsNullOrWhiteSpace(script.Path))
            {
                throw new InvalidOperationException("Sql:Scripts contains an enabled entry without Path.");
            }

            var scriptPath = ResolvePackageDataPath(payloadRoot, configPath, script.Path);
            InstallOutput.Info($"> SQL {scriptPath}");
            var sqlText = ReadSqlFile(scriptPath, sql, payloadRoot, config.IncludeExampleApps, []);
            await ExecuteSqlBatchesAsync(sql, sql.Database, sqlText, scriptPath);
        }
    }

    public static async Task EnsureRuntimeDatabaseAccessAsync(BootstrapConfig config)
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

        InstallOutput.Info("> Runtime database access");
        await using var connection = new SqlConnection(BuildConnectionString(config.Sql, config.Sql.Database));
        await connection.OpenAsync();

        foreach (var accountName in accountNames)
        {
            var granted = await EnsureDatabaseReaderWriterAsync(
                connection,
                accountName,
                config.Sql.CommandTimeoutSeconds);
            InstallOutput.Info(granted
                ? $"  {accountName}: db_datareader/db_datawriter."
                : $"  {accountName}: skipped; no matching SQL login exists.");
        }
    }

    internal static async Task<bool> EnsureDatabaseReaderWriterAsync(
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

    public static void EnsureRuntimeFilesystemAccess(BootstrapConfig config)
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

        InstallOutput.Info("> Runtime filesystem access");
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

        if (!string.IsNullOrWhiteSpace(hostAgent.PortalPhysicalPath))
        {
            EnsureDirectoryAccess(
                Path.GetFullPath(hostAgent.PortalPhysicalPath.Trim()),
                webAccounts,
                "M",
                required: false);
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

    internal static IEnumerable<string> ResolveRuntimeAccountNames(BootstrapConfig config)
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

    internal static IEnumerable<string> ResolveWebRuntimeAccountNames(HostAgentInstallOptions hostAgent)
    {
        yield return hostAgent.IisAppPoolUserName;
        foreach (var identity in hostAgent.IisAppPoolOverrides.Values)
        {
            yield return identity.UserName;
        }
    }

    internal static IEnumerable<string> ResolveWindowsAccountNameCandidates(string? configuredAccountName)
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

    internal static string TryNormalizeDomainAccountName(string accountName)
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

    internal static string NormalizeNetBiosDomainName(string domainName)
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

    internal static bool IsBuiltInServiceIdentity(string accountName)
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

    internal static IEnumerable<string> EnumerateDirectoryAndLocalParents(string path)
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

    internal static void EnsureDirectoryAccess(
        string path,
        IReadOnlyCollection<string> accountNames,
        string permission,
        bool required)
    {
        Directory.CreateDirectory(path);

        // R8-P2-16..23. CreateDirectory succeeds silently on an existing junction, so
        // this grant could land on whatever the junction points at -- and what it grants
        // is Modify to an IIS application-pool identity, which is the very privilege the
        // whole P2 threat model is about. This directory is one we create ourselves, so
        // finding a link here means someone put it there.
        OmpReparsePointGuard.EnsureNotReparsePoint(path, "Access-grant target directory");

        var granted = false;
        foreach (var accountName in accountNames)
        {
            // /L makes icacls act on a link rather than its target. Harmless on an
            // ordinary directory, and the difference between hardening this path and
            // handing out Modify somewhere else entirely if one appears later.
            var result = RunProcess(
                "icacls.exe",
                [path, "/grant", $"{accountName}:(OI)(CI)({permission})", "/L"],
                throwOnFailure: false);
            if (result.ExitCode == 0)
            {
                granted = true;
                InstallOutput.Info($"  {path}: granted {permission} to {accountName}.");
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
            InstallOutput.Info($"  {path}: skipped; could not grant {permission} to any configured web runtime account.");
        }
    }

    public static async Task ImportModuleDefinitionsAsync(
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
        InstallOutput.Info("> Module definitions");

        var packageDefinitions = new List<(string DefinitionPath, ModuleDefinitionDocument Definition)>();
        foreach (var definitionPath in definitionPaths)
        {
            var candidate = await ReadModuleDefinitionAsync(definitionPath);
            if (configuredModuleKeys.Count > 0 && !configuredModuleKeys.Contains(candidate.ModuleKey))
            {
                continue;
            }

            packageDefinitions.Add((definitionPath, candidate));
        }

        await using var connection = new SqlConnection(BuildConnectionString(config.Sql, config.Sql.Database));
        await connection.OpenAsync();

        foreach (var (definitionPath, definition) in packageDefinitions)
        {
            // A newer definition's idempotent SQL scripts supersede the older version's, so an
            // older package copy must never gate the import; re-running its failed scripts first
            // would permanently block a fixed newer definition from ever being applied.
            if (IsSupersededByNewerPackageDefinition(definition, packageDefinitions.Select(static item => item.Definition)))
            {
                InstallOutput.Info(
                    $"  {definition.ModuleKey} {definition.DefinitionVersion} skipped; the package contains a newer definition that supersedes this version's SQL scripts.");
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
                    InstallOutput.Info(
                        $"  {definition.ModuleKey} {definition.DefinitionVersion} skipped; installed definition {current.DefinitionVersion} is newer.");
                    continue;
                }

                if (current is not null
                    && CompareVersionText(definition.DefinitionVersion, current.DefinitionVersion) == 0
                    && trustSameVersion)
                {
                    InstallOutput.Info(
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
                    InstallOutput.Info(
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

            InstallOutput.Info(
                repairCount > 0
                    ? $"  {definition.ModuleKey} {definition.DefinitionVersion}; executed {repairCount} SQL repair script(s)."
                    : $"  {definition.ModuleKey} {definition.DefinitionVersion}");
        }
    }

    internal static bool IsSupersededByNewerPackageDefinition(
        ModuleDefinitionDocument definition,
        IEnumerable<ModuleDefinitionDocument> packageDefinitions)
    {
        return packageDefinitions.Any(candidate =>
            string.Equals(candidate.ModuleKey, definition.ModuleKey, StringComparison.OrdinalIgnoreCase)
            && CompareVersionText(candidate.DefinitionVersion, definition.DefinitionVersion) > 0);
    }

    internal static IReadOnlySet<string> ResolveConfiguredModuleKeys(BootstrapConfig config)
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

    internal static async Task<AppliedModuleDefinition?> QueryAppliedModuleDefinitionAsync(
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

    internal static async Task<ModuleDefinitionDocument> ReadModuleDefinitionAsync(string path)
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

    internal static IReadOnlyList<ModuleDefinitionCompatibilityEntry> ReadCompatibleArtifacts(JsonNode root)
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

    // Internal for PackageLibraryDefinitionGate, which needs the exact version
    // semantics the sync and status views use.
    //
    // Precedence follows SemVer 2.0 for the parts OMP versions use: build
    // metadata after '+' is ignored, and a release outranks its own
    // prereleases (1.2.3 > 1.2.3-rc1). The core may have any number of dotted
    // parts; missing trailing parts count as 0.
    internal static int CompareVersionText(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        var (leftCore, leftPrerelease) = SplitVersionText(left);
        var (rightCore, rightPrerelease) = SplitVersionText(right);
        var leftParts = leftCore.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = rightCore.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var index = 0; index < count; index++)
        {
            var comparison = CompareVersionIdentifier(
                index < leftParts.Length ? leftParts[index] : "0",
                index < rightParts.Length ? rightParts[index] : "0");
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (leftPrerelease is null || rightPrerelease is null)
        {
            // Equal cores: the side without a prerelease tag is the release.
            return (leftPrerelease is null ? 1 : 0) - (rightPrerelease is null ? 1 : 0);
        }

        // Prerelease identifiers compare one by one; when every shared one is
        // equal, the longer list is newer (1.2.3-alpha.1 > 1.2.3-alpha).
        var leftIdentifiers = leftPrerelease.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightIdentifiers = rightPrerelease.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var shared = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);
        for (var index = 0; index < shared; index++)
        {
            var comparison = CompareVersionIdentifier(leftIdentifiers[index], rightIdentifiers[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    internal static (string Core, string? Prerelease) SplitVersionText(string value)
    {
        var buildStart = value.IndexOf('+');
        var withoutBuild = buildStart >= 0 ? value[..buildStart] : value;
        var prereleaseStart = withoutBuild.IndexOf('-');
        return prereleaseStart >= 0
            ? (withoutBuild[..prereleaseStart], withoutBuild[(prereleaseStart + 1)..])
            : (withoutBuild, null);
    }

    internal static int CompareVersionIdentifier(string left, string right)
    {
        var leftIsNumber = long.TryParse(left, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var leftNumber);
        var rightIsNumber = long.TryParse(right, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rightNumber);
        if (leftIsNumber && rightIsNumber)
        {
            return leftNumber.CompareTo(rightNumber);
        }

        // SemVer: numeric identifiers have lower precedence than text ones.
        if (leftIsNumber != rightIsNumber)
        {
            return leftIsNumber ? -1 : 1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class VersionTextComparer : IComparer<string>
    {
        public static readonly VersionTextComparer Instance = new();

        public int Compare(string? x, string? y)
            => CompareVersionText(x ?? string.Empty, y ?? string.Empty);
    }

    internal static async Task<int> UpsertModuleDefinitionDocumentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleDefinitionDocument definition,
        string sourceName,
        int commandTimeoutSeconds)
    {
        var sql = OpenModulePlatform.ModuleDefinitions.ModuleRuntimeMaintenance.ModuleKeyCaseGuardSql("@moduleKey") + @"
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

    internal static async Task MarkOnlyModuleDefinitionAppliedAsync(
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

    internal static async Task ReplaceModuleDefinitionCompatibilityAsync(
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

    internal static async Task ApplyModuleDefinitionCatalogMetadataAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int documentId,
        int commandTimeoutSeconds)
    {
        var sql = @"
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
" + OpenModulePlatform.ModuleDefinitions.ModuleRuntimeMaintenance.ModuleKeyCaseGuardSql("@ModuleKey") + @"
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
                   WHEN app.AppType IN (N'Portal', N'WebApp', N'web') THEN N'web-app'
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
                   WHEN app.AppType IN (N'Portal', N'WebApp', N'web') THEN N'web-app'
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

    internal static async Task<int> ExecuteModuleDefinitionSqlRepairsAsync(
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
            var hasUnexecutedSql = await HasUnexecutedModuleDefinitionSqlAsync(
                connection,
                documentId,
                scripts.Where(static script => !IsValidationScript(script)),
                sql.CommandTimeoutSeconds);
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
                    InstallOutput.Info(
                        $"Validation script '{validationScript.Key}' failed and the module will be repaired: {ex.Message}");
                    needsRepair = true;
                }
            }

            if (!needsRepair && !hasUnexecutedSql)
            {
                return 0;
            }

            if (!needsRepair)
            {
                InstallOutput.Info(
                    $"Module '{definition.ModuleKey}' validation passed, but one or more idempotent SQL scripts have not succeeded for this definition; completing them now.");
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
                await OpenModulePlatform.ModuleDefinitions.PlatformConfigurationMigration.ApplyForDefinitionAsync(connection, definition.DefinitionJson, script.Key, CancellationToken.None, sql.CommandTimeoutSeconds);
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

    internal static async Task<bool> HasUnexecutedModuleDefinitionSqlAsync(
        SqlConnection connection,
        int documentId,
        IEnumerable<PortableModuleDefinitionSqlScript> scripts,
        int commandTimeoutSeconds)
    {
        const string sql = """
SELECT TOP (1) 1
FROM omp.ModuleDefinitionSqlExecutions
WHERE ModuleDefinitionDocumentId = @documentId
  AND ScriptKey = @scriptKey
  AND ScriptSha256 = @scriptSha256
  AND ExecutionStatus = N'Succeeded';
""";

        foreach (var script in scripts)
        {
            var originalSqlText = ResolvePortableSqlText(script);
            if (string.IsNullOrWhiteSpace(originalSqlText))
            {
                continue;
            }

            await using var command = new SqlCommand(sql, connection);
            command.CommandTimeout = commandTimeoutSeconds;
            command.Parameters.AddWithValue("@documentId", documentId);
            command.Parameters.AddWithValue("@scriptKey", script.Key);
            command.Parameters.AddWithValue("@scriptSha256", ComputeTextSha256(originalSqlText));
            if (await command.ExecuteScalarAsync() is null)
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<PortableModuleDefinitionSqlScript> ReadPortableSqlScripts(string definitionJson)
    {
        OpenModulePlatform.ModuleDefinitions.ModuleDefinitionSqlOwnership.ValidateDocument(definitionJson);
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

    internal static bool IsValidationScript(PortableModuleDefinitionSqlScript script)
        => string.Equals(script.Phase, "validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Phase, "validation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Execution, "validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(script.Execution, "validation", StringComparison.OrdinalIgnoreCase);

    internal static string? ResolvePortableSqlText(PortableModuleDefinitionSqlScript script)
        => OpenModulePlatform.ModuleDefinitions.ModuleDefinitionSqlOwnership.Decode(
            script.InlineSql, script.Content, script.ContentEncoding);

    internal static string ComputeTextSha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static async Task AcquireModuleDefinitionSqlExecutionLockAsync(
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

    internal static async Task<long> InsertModuleDefinitionSqlExecutionAsync(
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

    internal static async Task CompleteModuleDefinitionSqlExecutionAsync(
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

    internal static async Task ExecuteModuleDefinitionSqlBatchesAsync(
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

    internal static async Task<ModuleDefinitionValidationResult> ExecuteModuleDefinitionValidationSqlAsync(
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

    internal static ModuleDefinitionValidationResult ReadModuleDefinitionValidationResult(SqlDataReader reader)
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

    internal static int? TryGetOrdinal(SqlDataReader reader, string name)
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

    internal static bool? ConvertValidationBoolean(object? value)
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

    /// <summary>
    /// Replaces comments and string literals with equal-length blanks so the safety scans below
    /// match real statements only.
    /// </summary>
    /// <remarks>
    /// Without this the scans read prose. A comment reading "DELETE: channels with job or review
    /// history are hidden" was flagged as an unsafe DELETE, which blocked two module definitions
    /// at import. The failure stayed invisible until R7-G1 stopped routing packages with failed
    /// items to processed, so items had been dropping silently for some time. Blanking rather
    /// than removing keeps offsets stable for the ON DELETE lookbehind (R8-P3-14).
    /// </remarks>
    internal static string BlankSqlCommentsAndLiterals(string sqlText)
    {
        var buffer = new StringBuilder(sqlText.Length);
        var index = 0;
        while (index < sqlText.Length)
        {
            var current = sqlText[index];

            if (current == '-' && index + 1 < sqlText.Length && sqlText[index + 1] == '-')
            {
                while (index < sqlText.Length && sqlText[index] != '\n' && sqlText[index] != '\r')
                {
                    buffer.Append(' ');
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < sqlText.Length && sqlText[index + 1] == '*')
            {
                var depth = 0;
                while (index < sqlText.Length)
                {
                    if (sqlText[index] == '/' && index + 1 < sqlText.Length && sqlText[index + 1] == '*')
                    {
                        depth++;
                        buffer.Append("  ");
                        index += 2;
                        continue;
                    }

                    if (sqlText[index] == '*' && index + 1 < sqlText.Length && sqlText[index + 1] == '/')
                    {
                        depth--;
                        buffer.Append("  ");
                        index += 2;
                        if (depth <= 0)
                        {
                            break;
                        }

                        continue;
                    }

                    buffer.Append(sqlText[index] == '\n' || sqlText[index] == '\r' ? sqlText[index] : ' ');
                    index++;
                }

                continue;
            }

            if (current == '\'')
            {
                buffer.Append(' ');
                index++;
                while (index < sqlText.Length)
                {
                    if (sqlText[index] == '\'')
                    {
                        buffer.Append(' ');
                        index++;

                        // A doubled quote is an escaped quote inside the literal, not its end.
                        if (index < sqlText.Length && sqlText[index] == '\'')
                        {
                            buffer.Append(' ');
                            index++;
                            continue;
                        }

                        break;
                    }

                    buffer.Append(sqlText[index] == '\n' || sqlText[index] == '\r' ? sqlText[index] : ' ');
                    index++;
                }

                continue;
            }

            buffer.Append(current);
            index++;
        }

        return buffer.ToString();
    }

    // Configuration ownership is parsed by the source-linked guard first. The
    // guard includes table-targeted DBCC and global sp_updatestats/sp_createstats.
    // existing artifact/pointer rules below retain their stored-body exceptions.
    // See docs/MODULE_DEFINITIONS.md for the ownership model and analysis scope.
    internal static string? ValidateSafeModuleDefinitionSql(string sqlText)
    {
        // OMP-MODULE-SQL-CONFIG-OWNERSHIP: inspect parsed SQL before blanking literals.
        var configurationOwnership = OpenModulePlatform.ModuleDefinitions.ModuleDefinitionSqlOwnership.Validate(sqlText);
        if (configurationOwnership is not null) return configurationOwnership;

        sqlText = BlankSqlCommentsAndLiterals(sqlText);

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

        var artifactOwnershipSql = ExcludeStoredModuleBodiesFromArtifactOwnershipScan(sqlText);
        if (ContainsDirectModuleDefinitionTableWrite(artifactOwnershipSql, "Artifacts")
            || ContainsDirectModuleDefinitionTableDelete(artifactOwnershipSql, "Artifacts"))
        {
            return "Module definition SQL must not register or mutate omp.Artifacts; artifact registration is owned by the artifact import path.";
        }

        if (ContainsModuleDefinitionColumnWrite(artifactOwnershipSql, "InstanceTemplateAppInstances", "DesiredArtifactId")
            || ContainsModuleDefinitionColumnWrite(artifactOwnershipSql, "AppInstances", "ArtifactId"))
        {
            return "Module definition SQL must not write omp.InstanceTemplateAppInstances.DesiredArtifactId or omp.AppInstances.ArtifactId; artifact selection is owned by artifact auto-apply.";
        }

        var unsafeDeleteStatement = ExtractDeleteStatements(sqlText)
            .FirstOrDefault(static statement => !Regex.IsMatch(statement, @"(?is)\bWHERE\b"));
        if (unsafeDeleteStatement is not null)
        {
            return "The script contains DELETE without a WHERE clause.";
        }

        return null;
    }

    internal static string ExcludeStoredModuleBodiesFromArtifactOwnershipScan(string sqlText)
        => string.Join(
            ";" + Environment.NewLine,
            SplitSqlBatches(sqlText).Where(static batch => !Regex.IsMatch(
                batch,
                @"(?is)^\s*(?:CREATE(?:\s+OR\s+ALTER)?|ALTER)\s+(?:PROC(?:EDURE)?|TRIGGER|FUNCTION)\b")));

    internal static bool ContainsDirectModuleDefinitionTableWrite(string sqlText, string tableName)
    {
        var qualifiedTable = BuildOmpQualifiedIdentifierPattern(tableName);
        const string top = @"(?:TOP\s*(?:\([^)]*\)|\d+)(?:\s+PERCENT)?\s+)?";
        if (Regex.IsMatch(
            sqlText,
            $@"(?is)\b(?:INSERT\s+{top}(?:INTO\s+)?|UPDATE\s+{top}|MERGE\s+{top}(?:INTO\s+)?){qualifiedTable}"))
        {
            return true;
        }

        if (Regex.IsMatch(
            sqlText,
            $@"(?is)\bUPDATE\s+{top}(?<alias>\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s+SET\b(?:(?!;|^\s*GO\b).)*?\bFROM\s+{qualifiedTable}(?:\s+WITH\s*\([^)]*\))?\s+(?:AS\s+)?\k<alias>"))
        {
            return true;
        }

        // OUTPUT ... INTO writes rows into the table without an INSERT/UPDATE/MERGE
        // keyword in front of the table name.
        if (Regex.IsMatch(
            sqlText,
            $@"(?ims)\bOUTPUT\b(?:(?!;|^\s*GO\b).)*?\bINTO\s+{qualifiedTable}"))
        {
            return true;
        }

        // A CTE over the table can be the DML target; SQL Server writes the base table.
        return ContainsModuleDefinitionCteWrite(sqlText, qualifiedTable, columnPattern: null);
    }

    internal static bool ContainsDirectModuleDefinitionTableDelete(string sqlText, string tableName)
    {
        var qualifiedTable = BuildOmpQualifiedIdentifierPattern(tableName);
        const string top = @"(?:TOP\s*(?:\([^)]*\)|\d+)(?:\s+PERCENT)?\s+)?";
        if (Regex.IsMatch(sqlText, $@"(?is)\bDELETE\s+{top}(?:FROM\s+)?{qualifiedTable}"))
        {
            return true;
        }

        // DELETE <alias> FROM ... <table> [AS] <alias> — the alias form the UPDATE side already
        // guards. Without it `DELETE a FROM omp.Artifacts AS a WHERE a.ArtifactId = 1` passed:
        // the table never sits directly after DELETE or FROM, so the pattern above cannot see it.
        // Binding the deleted alias to the qualified table also covers the JOIN form
        // (`DELETE a FROM omp.Artifacts a JOIN ...`), which is why the table is matched anywhere
        // inside the FROM clause rather than only at its head.
        // Verified ALLOWED on all three mirrors before this change (independent review 2026-09-02).
        if (Regex.IsMatch(
            sqlText,
            $@"(?is)\bDELETE\s+{top}(?<alias>\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s+FROM\b"
            + $@"(?:(?!;|^\s*GO\b).)*?{qualifiedTable}(?:\s+WITH\s*\([^)]*\))?\s+(?:AS\s+)?"
            + @"\k<alias>(?![A-Za-z0-9_])"))
        {
            return true;
        }

        return ContainsModuleDefinitionCteWrite(sqlText, qualifiedTable, columnPattern: null);
    }

    // Matches a CTE whose body reads the given table and that is the target of a
    // following UPDATE or DELETE FROM. When columnPattern is supplied, only an UPDATE
    // whose top-level SET assignments write that column counts.
    internal static bool ContainsModuleDefinitionCteWrite(string sqlText, string qualifiedTable, string? columnPattern)
    {
        const string alias = @"(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)";
        foreach (Match cte in Regex.Matches(
            sqlText,
            $@"(?is)\b(?<name>{alias})\s+AS\s*\((?<body>[^()]*(?:\([^()]*\)[^()]*)*)\)\s*(?:UPDATE\s+\k<name>(?![A-Za-z0-9_])\s+SET\b|DELETE\s+(?:FROM\s+)?\k<name>(?![A-Za-z0-9_]))"))
        {
            if (!Regex.IsMatch(cte.Groups["body"].Value, qualifiedTable))
            {
                continue;
            }

            if (columnPattern is null)
            {
                return true;
            }

            var assignments = ExtractModuleDefinitionAssignments(sqlText, cte.Index + cte.Length, "WHERE");
            if (ContainsModuleDefinitionColumnAssignment(assignments, columnPattern, alias))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ContainsModuleDefinitionColumnWrite(
        string sqlText,
        string tableName,
        string columnName)
    {
        var qualifiedTable = BuildOmpQualifiedIdentifierPattern(tableName);
        var column = BuildSqlIdentifierPattern(columnName);
        const string top = @"(?:TOP\s*(?:\([^)]*\)|\d+)(?:\s+PERCENT)?\s+)?";
        const string alias = @"(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)";
        const string tableHint = @"(?:\s+WITH\s*\([^)]*\))?";

        if (Regex.Matches(
                sqlText,
                $@"(?is)\bINSERT\s+{top}(?:INTO\s+)?{qualifiedTable}{tableHint}\s*\((?<columns>[^)]*)\)")
            .Cast<Match>()
            .Any(insert => Regex.IsMatch(insert.Groups["columns"].Value, column, RegexOptions.IgnoreCase)))
        {
            return true;
        }

        // A positional INSERT (VALUES/SELECT/DEFAULT VALUES without a column list) has no
        // list to scan and can target the owned column by ordinal position.
        if (Regex.IsMatch(
            sqlText,
            $@"(?is)\bINSERT\s+{top}(?:INTO\s+)?{qualifiedTable}{tableHint}(?!\s*\()"))
        {
            return true;
        }

        if (Regex.Matches(
                sqlText,
                $@"(?is)\bUPDATE\s+{top}{qualifiedTable}{tableHint}(?:\s+(?:AS\s+)?{alias})?\s+SET\b")
            .Cast<Match>()
            .Select(update => ExtractModuleDefinitionAssignments(
                sqlText,
                update.Index + update.Length,
                "WHERE"))
            .Any(assignments => ContainsModuleDefinitionColumnAssignment(assignments, column, alias)))
        {
            return true;
        }

        if (Regex.Matches(
                sqlText,
                $@"(?is)\bUPDATE\s+{top}(?<targetAlias>{alias})\s+SET\b(?<assignments>.*?)\bFROM\s+{qualifiedTable}{tableHint}\s+(?:AS\s+)?\k<targetAlias>")
            .Cast<Match>()
            .Any(update => ContainsModuleDefinitionColumnAssignment(update.Groups["assignments"].Value, column, alias)))
        {
            return true;
        }

        foreach (var body in Regex.Matches(
                sqlText,
                $@"(?is)\bMERGE\s+{top}(?:INTO\s+)?{qualifiedTable}{tableHint}(?<body>.*?)(?=;|^\s*GO\b|\z)")
            .Cast<Match>()
            .Select(merge => merge.Groups["body"].Value))
        {
            if (Regex.Matches(body, @"(?is)\bINSERT\s*\((?<columns>[^)]*)\)")
                .Cast<Match>()
                .Any(match => Regex.IsMatch(match.Groups["columns"].Value, column, RegexOptions.IgnoreCase)))
            {
                return true;
            }

            // WHEN NOT MATCHED THEN INSERT VALUES(...) has no column list to scan and can
            // target the owned column by ordinal position.
            if (Regex.IsMatch(body, @"(?is)\bINSERT\b(?!\s*\()"))
            {
                return true;
            }

            if (Regex.Matches(body, @"(?is)\bUPDATE\s+SET\b")
                .Cast<Match>()
                .Select(set => ExtractModuleDefinitionAssignments(
                    body,
                    set.Index + set.Length,
                    "WHEN"))
                .Any(assignments => ContainsModuleDefinitionColumnAssignment(assignments, column, alias)))
            {
                return true;
            }
        }

        // OUTPUT ... INTO with a column list naming the owned column, or without a column
        // list at all (positional), writes the column without INSERT/UPDATE/MERGE in
        // front of the table name.
        if (Regex.Matches(
                sqlText,
                $@"(?ims)\bOUTPUT\b(?:(?!;|^\s*GO\b).)*?\bINTO\s+{qualifiedTable}{tableHint}\s*\((?<columns>[^)]*)\)")
            .Cast<Match>()
            .Any(output => Regex.IsMatch(output.Groups["columns"].Value, column, RegexOptions.IgnoreCase)))
        {
            return true;
        }

        if (Regex.IsMatch(
            sqlText,
            $@"(?ims)\bOUTPUT\b(?:(?!;|^\s*GO\b).)*?\bINTO\s+{qualifiedTable}{tableHint}(?!\s*\()"))
        {
            return true;
        }

        // A CTE over the table can be the UPDATE target; SQL Server writes the base table.
        return ContainsModuleDefinitionCteWrite(sqlText, qualifiedTable, column);
    }

    internal static bool ContainsModuleDefinitionColumnAssignment(
        string assignments,
        string columnPattern,
        string aliasPattern)
        => Regex.IsMatch(
            assignments,
            $@"(?is)(?:\A|,)\s*(?:{aliasPattern}\s*\.\s*)?{columnPattern}\s*[-+*/%&^|]?=");

    // Returns the SET assignments starting at startIndex, stopping at the first
    // terminator (';', a GO line, or one of the given keywords) found at parenthesis
    // depth zero and outside any CASE...END block. A regex lookahead cannot express
    // that, and stopping early was a confirmed bypass: a WHERE inside a subquery, a
    // WHEN inside a CASE expression, or a parameter named @Where all truncated the
    // scan before the real assignment.
    internal static string ExtractModuleDefinitionAssignments(
        string sqlText,
        int startIndex,
        params string[] terminatorKeywords)
    {
        var depth = 0;
        var caseDepth = 0;
        for (var index = startIndex; index < sqlText.Length; index++)
        {
            var current = sqlText[index];
            if (current == '(')
            {
                depth++;
                continue;
            }

            if (current == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            if (depth != 0)
            {
                continue;
            }

            if (caseDepth > 0)
            {
                if (IsSqlKeywordAt(sqlText, index, "CASE"))
                {
                    caseDepth++;
                }
                else if (IsSqlKeywordAt(sqlText, index, "END"))
                {
                    caseDepth--;
                }

                continue;
            }

            if (current == ';'
                || ((current == '\r' || current == '\n') && IsGoBatchSeparatorAt(sqlText, index))
                || terminatorKeywords.Any(keyword => IsSqlKeywordAt(sqlText, index, keyword)))
            {
                return sqlText[startIndex..index];
            }

            if (IsSqlKeywordAt(sqlText, index, "CASE"))
            {
                caseDepth++;
            }
        }

        return sqlText[startIndex..];
    }

    internal static bool IsGoBatchSeparatorAt(string sqlText, int lineBreakIndex)
    {
        var index = lineBreakIndex + 1;
        if (sqlText[lineBreakIndex] == '\r' && index < sqlText.Length && sqlText[index] == '\n')
        {
            index++;
        }

        while (index < sqlText.Length && (sqlText[index] == ' ' || sqlText[index] == '\t'))
        {
            index++;
        }

        return IsSqlKeywordAt(sqlText, index, "GO");
    }

    internal static bool IsSqlKeywordAt(string sqlText, int index, string keyword)
    {
        // A preceding '@' keeps parameters such as @Where from counting as keywords.
        if (index > 0 && (char.IsLetterOrDigit(sqlText[index - 1]) || sqlText[index - 1] is '_' or '@' or '#' or '$'))
        {
            return false;
        }

        if (index + keyword.Length > sqlText.Length)
        {
            return false;
        }

        if (!sqlText.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var after = index + keyword.Length;
        return after >= sqlText.Length
            || !(char.IsLetterOrDigit(sqlText[after]) || sqlText[after] is '_' or '#' or '$');
    }

    internal static IEnumerable<string> ExtractDeleteStatements(string sqlText)
    {
        foreach (var batch in SplitSqlBatches(sqlText))
        {
            foreach (Match match in Regex.Matches(
                batch,
                @"(?ims)\bDELETE\b(?<statement>.*?)(?=;|^\s*(?:GO|INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC(?:UTE)?|GRANT|REVOKE|DENY|SELECT)\b|\z)"))
            {
                if (IsForeignKeyOnDeleteClause(batch, match.Index))
                {
                    continue;
                }

                var statement = ("DELETE" + match.Groups["statement"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(statement))
                {
                    yield return statement;
                }
            }
        }
    }

    internal static bool IsForeignKeyOnDeleteClause(string sqlText, int deleteIndex)
    {
        var beforeDelete = sqlText[..deleteIndex].TrimEnd();
        // THEN as well as ON: "WHEN NOT MATCHED BY SOURCE THEN DELETE" is a MERGE action whose
        // scope is the merge predicate, so requiring a WHERE on it is meaningless -- but the
        // scan read it as an unguarded delete and blocked a module definition at import. Same
        // false-positive class as the comment scanning (R8-P3-14).
        return Regex.IsMatch(beforeDelete, @"(?is)\b(?:ON|THEN)$");
    }

    internal static string BuildOmpQualifiedIdentifierPattern(string tableName)
        => $@"(?:\[omp\]|""omp""|omp)\s*\.\s*{BuildSqlIdentifierPattern(tableName)}";

    internal static string BuildSqlIdentifierPattern(string identifier)
    {
        var escaped = Regex.Escape(identifier);
        return $@"(?:\[{escaped}\]|""{escaped}""|{escaped})(?![A-Za-z0-9_])";
    }

    internal static string? ValidateReadOnlyModuleDefinitionSql(string sqlText)
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

    public static async Task EnsureDatabaseAsync(SqlBootstrapOptions sql)
    {
        InstallOutput.Info($"> SQL ensure database {sql.Database}");
        var createSql = """
DECLARE @DatabaseName sysname = @requestedDatabaseName;
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
        command.Parameters.AddWithValue("@requestedDatabaseName", sql.Database.Trim());
        await command.ExecuteNonQueryAsync();
    }

    internal static string ReadSqlFile(
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

    internal static string PreprocessSql(
        string sqlText,
        SqlBootstrapOptions options,
        string scriptPath,
        string payloadRoot)
    {
        // Third instance of the R5S-G6 shape: a database name containing $ would be read
        // as a Regex substitution token by the string overload (R7-S7).
        var useDatabaseStatement = "USE " + ConvertToSqlBracketName(options.Database);
        var result = UseDatabaseRegex().Replace(sqlText, _ => useDatabaseStatement);

        result = PatchBootstrapPrincipal(result, options);

        var artifactVersion = ResolveArtifactVersionOverride(options, scriptPath, payloadRoot);
        if (!string.IsNullOrWhiteSpace(artifactVersion))
        {
            var versionLiteral = ConvertToSqlUnicodeLiteral(artifactVersion);
            // R5S-G6: the SQL literal is inserted via a MatchEvaluator so the
            // replacement is treated literally; a value like "1.0$'" would
            // otherwise have "$'" interpreted as a Regex substitution token.
            result = ArtifactVersionDeclarationRegex().Replace(
                result,
                _ => $"DECLARE @ArtifactVersion nvarchar(50) = {versionLiteral};",
                1);
        }

        foreach (var item in ResolveArtifactVersionVariableOverrides(options, scriptPath, payloadRoot))
        {
            result = PatchSqlNVarCharDeclaration(result, item.Key, item.Value);
        }

        return result;
    }

    internal static bool IsExampleSqlPath(string path, string payloadRoot)
    {
        var relative = NormalizePathForMatch(Path.GetRelativePath(payloadRoot, path));
        return relative.StartsWith("sql/examples/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("examples/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/examples/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string PatchBootstrapPrincipal(string sqlText, SqlBootstrapOptions options)
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

        // R5S-G6: insert SQL literals via a MatchEvaluator so a principal value
        // containing Regex substitution tokens (e.g. "$'") is inserted literally.
        var result = BootstrapPrincipalDeclarationRegex().Replace(
            sqlText,
            _ => $"DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = {principalLiteral};");

        return BootstrapPrincipalTypeDeclarationRegex().Replace(
            result,
            _ => $"DECLARE @BootstrapPortalAdminPrincipalType nvarchar(50) = {principalTypeLiteral};");
    }

    internal static string? ResolveArtifactVersionOverride(
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

    internal static IReadOnlyDictionary<string, string> ResolveArtifactVersionVariableOverrides(
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

    internal static string NormalizeScriptPathForOverrideMatch(string scriptPath, string payloadRoot)
    {
        if (Path.IsPathFullyQualified(scriptPath))
        {
            return NormalizePathForMatch(Path.GetRelativePath(payloadRoot, scriptPath));
        }

        return NormalizePathForMatch(scriptPath);
    }

    internal static string PatchSqlNVarCharDeclaration(
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

        // R5S-G6: MatchEvaluator inserts the literal verbatim; a value such as
        // "1.0$'" must not be reinterpreted as a Regex substitution token.
        return Regex.Replace(sqlText, pattern, _ => replacement, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    internal static async Task ExecuteSqlBatchesAsync(
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

    internal static async Task ExecuteSqlBatchWithRetryAsync(
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
                InstallOutput.Info(
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

    internal static bool IsDeadlock(SqlException ex)
        => ex.Errors.Cast<SqlError>().Any(static error => error.Number == SqlDeadlockErrorNumber);

    internal static IEnumerable<string> SplitSqlBatches(string sqlText)
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

    internal static string BuildConnectionString(SqlBootstrapOptions sql, string database)
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
}
