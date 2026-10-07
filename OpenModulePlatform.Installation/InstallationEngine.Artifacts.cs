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
    public static IReadOnlyList<PreparedArtifactConfigurationFiles> PrepareArtifacts(
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
                    var configurationFiles = ReadArtifactPackageConfigurationFilesOnly(source);
                    if (configurationFiles.Count > 0)
                    {
                        preparedConfigurationFiles.Add(new PreparedArtifactConfigurationFiles(
                            artifact.Target,
                            configurationFiles));
                    }
                }

                InstallOutput.Info($"> Artifact {artifact.Target} already exists; skipped.");
                continue;
            }

            InstallOutput.Info($"> Artifact {artifact.Target}");

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

    public static IReadOnlyList<string> SelectLatestAvailableArtifactPackages(
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

    public static void WriteArtifactSelectionMessages(IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        InstallOutput.Info("> Latest available artifact selection");
        foreach (var message in messages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            InstallOutput.Info(message);
        }

        InstallOutput.Info();
    }

    internal static IReadOnlyList<AvailableArtifactPackage> DiscoverAvailableArtifactPackages(string payloadRoot)
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

    internal static AvailableArtifactPackage? FindLatestAvailableArtifactPackage(
        IReadOnlyList<AvailableArtifactPackage> packages,
        ConfiguredArtifactIdentity identity)
        => packages
            .Where(package => IsSameArtifactSlot(package.Identity, identity))
            .OrderByDescending(package => package.Identity.Version, VersionTextComparer.Instance)
            .FirstOrDefault();

    internal static bool IsSameArtifactSlot(ConfiguredArtifactIdentity left, ConfiguredArtifactIdentity right)
        => left.ModuleKey.Equals(right.ModuleKey, StringComparison.OrdinalIgnoreCase)
            && left.AppKey.Equals(right.AppKey, StringComparison.OrdinalIgnoreCase)
            && left.PackageType.Equals(right.PackageType, StringComparison.OrdinalIgnoreCase)
            && left.TargetName.Equals(right.TargetName, StringComparison.OrdinalIgnoreCase);

    internal static bool IsHostAgentArtifact(ConfiguredArtifactIdentity identity)
        => identity.PackageType.Equals("host-agent", StringComparison.OrdinalIgnoreCase);

    internal static bool TryReplaceArtifactTargetVersion(
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

    internal const long MaxArtifactConfigReadManifestBytes = 1024 * 1024;

    internal const long MaxArtifactConfigReadFileBytes = 1024 * 1024 * 5;

    // R5-G5: for an AddMissingOnly artifact that already exists we only need
    // its configuration files, not its (potentially multi-GB) payload. The
    // previous path called ArtifactPackageExtractor.Extract, which unpacked
    // the ENTIRE payload to a staging folder just to hand back the small
    // configuration entries, then deleted it. Read the manifest and the
    // referenced configuration entries straight out of the zip instead.
    internal static IReadOnlyList<ArtifactPackageConfigurationFile> ReadArtifactPackageConfigurationFilesOnly(string source)
    {
        using var archive = ZipFile.OpenRead(source);
        var manifestEntry = archive.Entries.FirstOrDefault(entry =>
            string.Equals(
                NormalizeArtifactZipEntryName(entry.FullName),
                ArtifactPackageExtractor.ManifestEntryName,
                StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null)
        {
            // Legacy artifact zips (no manifest envelope) carry no configuration files.
            return [];
        }

        if (manifestEntry.Length > MaxArtifactConfigReadManifestBytes)
        {
            throw new InvalidOperationException(
                $"Artifact package manifest exceeds the limit of {MaxArtifactConfigReadManifestBytes} bytes.");
        }

        JsonObject manifest;
        using (var manifestStream = manifestEntry.Open())
        using (var manifestReader = new StreamReader(
            manifestStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true))
        {
            manifest = JsonNode.Parse(manifestReader.ReadToEnd()) as JsonObject
                ?? throw new InvalidOperationException("Artifact package manifest must be a JSON object.");
        }

        var nodes = manifest["configurationFiles"]?.AsArray();
        if (nodes is null || nodes.Count == 0)
        {
            return [];
        }

        var files = new List<ArtifactPackageConfigurationFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var item = node?.AsObject()
                ?? throw new InvalidOperationException("Each artifact package configurationFiles item must be an object.");
            var relativePath = NormalizeArtifactConfigRelativePath(
                item["relativePath"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Artifact package configurationFiles[].relativePath is required."));
            var sourcePath = NormalizeArtifactZipEntryName(
                item["source"]?.GetValue<string>()
                    ?? item["path"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Artifact package configurationFiles[].source is required."));

            if (!seenPaths.Add(relativePath))
            {
                throw new InvalidOperationException(
                    $"Artifact package contains duplicate configuration relative path '{relativePath}'.");
            }

            var sourceEntry = archive.Entries.FirstOrDefault(candidate =>
                !string.IsNullOrEmpty(candidate.Name)
                && string.Equals(NormalizeArtifactZipEntryName(candidate.FullName), sourcePath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Artifact package references missing file '{sourcePath}'.");

            if (sourceEntry.Length > MaxArtifactConfigReadFileBytes)
            {
                throw new InvalidOperationException(
                    $"Artifact package configuration file '{sourceEntry.FullName}' exceeds the limit of {MaxArtifactConfigReadFileBytes} bytes.");
            }

            using var contentStream = sourceEntry.Open();
            using var contentReader = new StreamReader(
                contentStream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            files.Add(new ArtifactPackageConfigurationFile(relativePath, contentReader.ReadToEnd()));
        }

        return files;
    }

    internal static string NormalizeArtifactZipEntryName(string fullName)
    {
        var normalized = fullName.Replace('\\', '/').Trim();
        if (normalized.Length == 0 || normalized.StartsWith('/'))
        {
            throw new InvalidOperationException("The artifact package contains an invalid entry path.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("The artifact package contains a path that escapes the package root.");
        }

        return string.Join('/', segments);
    }

    internal static string NormalizeArtifactConfigRelativePath(string relativePath)
    {
        var normalized = relativePath.Trim().Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || normalized.Contains(':', StringComparison.Ordinal)
            || segments.Any(static segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("Configuration file relative paths must stay inside the deployed artifact root.");
        }

        return string.Join('/', segments);
    }

    public static async Task RegisterPackageArtifactsAsync(
        BootstrapConfig config,
        bool trustExistingArtifactVersions = false)
    {
        if (!config.Sql.Enabled)
        {
            InstallOutput.Info("> SQL disabled; skipping artifact metadata registration.");
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
                InstallOutput.Info($"> Artifact metadata {artifact.Target}: skipped because Source filename does not use the standard artifact package name.");
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
                InstallOutput.Info($"> Artifact metadata {artifact.Target}: fast mode reused existing metadata for version {identity.Version}.");
            }

            var updates = await ApplyConfiguredArtifactToMatchingApplicationsAsync(
                connection,
                artifactId.Value,
                identity.PackageType);
            if (updates.TemplateAppRowsUpdated + updates.AppInstanceRowsUpdated + updates.WorkerInstanceRowsUpdated > 0)
            {
                InstallOutput.Info(
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
                    InstallOutput.Info(
                        $"> HostAgent desired state {artifact.Target}: updated {hostAgentDesiredRows} host row(s).");
                }
            }

            registered++;
        }

        if (registered > 0)
        {
            InstallOutput.Info($"> Artifact metadata registered or updated: {registered}");
        }
    }

    internal static ConfiguredArtifactIdentity? ParseConfiguredArtifactIdentity(string source)
    {
        var fileName = Path.GetFileNameWithoutExtension(source);
        var parts = fileName.Split(["__"], StringSplitOptions.None);
        return parts.Length == 5
            && parts.All(static part => !string.IsNullOrWhiteSpace(part))
            ? new ConfiguredArtifactIdentity(parts[0], parts[1], parts[2], parts[3], parts[4])
            : null;
    }

    internal static async Task<int?> ResolveArtifactAppIdAsync(
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

    internal static async Task<int?> TryReuseExistingArtifactMetadataAsync(
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

    internal static async Task<int> UpsertArtifactMetadataAsync(
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

    internal static async Task<(int TemplateAppRowsUpdated, int AppInstanceRowsUpdated, int WorkerInstanceRowsUpdated)> ApplyConfiguredArtifactToMatchingApplicationsAsync(
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
       (@packageType = N'web-app' AND @appType IN (N'Portal', N'WebApp', N'web'))
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

    internal static async Task<int> ApplyConfiguredHostAgentArtifactToCurrentHostAsync(
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

    internal static string ResolveHostAgentHostKey(HostAgentInstallOptions hostAgent)
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

    internal static HostAgentBootstrapServiceIdentity ResolveBootstrapHostAgentServiceIdentity(BootstrapConfig config)
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

    public static void WriteHostAgentInstallOrUpdateIntent(BootstrapConfig config)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(config.HostAgent.ServiceName))
        {
            return;
        }

        var identity = ResolveBootstrapHostAgentServiceIdentity(config);
        if (ServiceExists(identity.ServiceName))
        {
            InstallOutput.Info(
                $"> HostAgent service '{identity.ServiceName}' already exists; full install/update will stop and reconfigure it from this profile.");
            return;
        }

        if (HostAgentServiceWithPrefixExists(identity.ServiceNamePrefix))
        {
            InstallOutput.Info(
                $"> HostAgent service '{identity.ServiceName}' is missing, but another HostAgent service is present; full install/update will configure '{identity.ServiceName}' from this profile and may remove target-path duplicates.");
            return;
        }

        InstallOutput.Info($"> HostAgent service '{identity.ServiceName}' is missing; installing it.");
    }

    internal static string ResolveBootstrapHostAgentInstallPath(
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

    internal static string ResolveHostAgentServiceNamePrefix(string serviceName)
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

    internal static string? ResolveConfiguredHostAgentArtifactVersion(BootstrapConfig config)
        => config.Artifacts
            .Where(static artifact => artifact.Enabled)
            .Select(static artifact =>
                TryResolveHostAgentVersionFromTarget(artifact.Target)
                ?? TryResolveHostAgentVersionFromPackageName(artifact.Source))
            .FirstOrDefault(static version => !string.IsNullOrWhiteSpace(version))
            ?? TryResolveHostAgentVersionFromPackageName(config.HostAgent.PackagePath);

    internal static string? TryResolveHostAgentVersionFromTarget(string target)
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

    internal static string? TryResolveHostAgentVersionFromPackageName(string source)
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

    internal static string NormalizePathFragment(string value)
        => value.Trim().Replace('\\', '/').TrimStart('/');

    internal static string SanitizeWindowsServiceNamePart(string value)
    {
        var chars = value.Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'
                ? ch
                : '_')
            .ToArray();
        var sanitized = new string(chars).Trim('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    internal static string SanitizeWindowsPathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value.Trim()
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(chars).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    internal static string ComputeDirectorySha256(string path)
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

    public static void PublishAvailableDeploymentObjects(
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
            InstallOutput.Info(
                $"> Available package library: {definitionsCopied} module definition(s), {artifactsCopied} artifact package(s), {hostConfigsCopied} host config(s), {configOverlaysCopied} config overlay(s), {widgetsCopied} widget(s), {widgetDataCopied} widget data package(s)");
        }
    }

    internal static int CopyAvailableDeploymentObjects(
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

    public static async Task RegisterPreparedArtifactConfigurationFilesAsync(
        BootstrapConfig config,
        IReadOnlyList<PreparedArtifactConfigurationFiles> preparedConfigurationFiles)
    {
        if (preparedConfigurationFiles.Count == 0)
        {
            return;
        }

        if (!config.Sql.Enabled)
        {
            InstallOutput.Info("> SQL disabled; skipping artifact configuration file registration.");
            return;
        }

        await using var connection = new SqlConnection(BuildConnectionString(config.Sql, config.Sql.Database));
        await connection.OpenAsync();

        foreach (var prepared in preparedConfigurationFiles)
        {
            var artifactId = await ResolveArtifactIdByRelativePathAsync(
                connection,
                prepared.ArtifactRelativePath);

            // Carry-forward must run only the FIRST time this artifact version's
            // config is registered, matching the HostAgent/Portal import paths.
            // The Bootstrapper re-registers config on every run (AddMissingOnly),
            // and an operator's deliberate revert to package default is
            // indistinguishable from a pristine row, so re-running carry-forward
            // would re-apply the sibling version's stale edit over that revert
            // every refresh (R4-D2). Decide before the replace mutates the rows.
            var alreadyRegistered = await ArtifactHasConfigurationRowsAsync(connection, artifactId);

            await ReplaceArtifactConfigurationFilesAsync(
                connection,
                artifactId,
                prepared.ConfigurationFiles);

            if (!alreadyRegistered)
            {
                // Preserve operator-edited configuration content from the previous
                // artifact version when the prepared file itself is unchanged, and
                // log files whose operator edits could not be carried forward.
                await CarryForwardArtifactConfigurationFilesAsync(
                    connection,
                    artifactId,
                    prepared.ArtifactRelativePath);
            }

            InstallOutput.Info(
                $"> Artifact config files {prepared.ArtifactRelativePath}: {prepared.ConfigurationFiles.Count}");
        }
    }

    public static async Task CopyMissingArtifactConfigurationFilesFromPreviousVersionsAsync(
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
                InstallOutput.Info(
                    $"> Artifact config files {artifactRelativePath}: copied {copied} from the previous artifact version the slot pointed to.");
            }
        }
    }

    internal static async Task<int> CopyMissingArtifactConfigurationFilesFromPreviousVersionAsync(
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

        // Shared with the HostAgent and Portal import paths
        // (ArtifactConfigurationFileImportSql.CopyConfigurationFilesFromContinuitySource):
        // the source is the artifact the slot's pointers referenced, with the
        // per-path operator-delta fallback when no pointer names a source --
        // never simply the most recently created sibling.
        await using var command = new SqlCommand(
            ArtifactConfigurationFileImportSql.CopyConfigurationFilesFromContinuitySource,
            connection);
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddWithValue("@ArtifactId", target.ArtifactId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return 0;
        }

        return reader.GetInt32(2);
    }

    internal static async Task<ArtifactConfigurationCopyTarget?> QueryArtifactConfigurationCopyTargetAsync(
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

    internal static async Task<bool> ArtifactHasConfigurationRowsAsync(
        SqlConnection connection,
        int artifactId)
    {
        const string sql = """
SELECT CASE WHEN EXISTS
(
    SELECT 1 FROM omp.ArtifactConfigurationFiles WHERE ArtifactId = @artifactId
) THEN 1 ELSE 0 END;
""";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@artifactId", artifactId);
        var result = await command.ExecuteScalarAsync();
        return result is int flag && flag == 1;
    }

    internal static async Task<int> ResolveArtifactIdByRelativePathAsync(
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

    internal static async Task ReplaceArtifactConfigurationFilesAsync(
        SqlConnection connection,
        int artifactId,
        IReadOnlyList<ArtifactPackageConfigurationFile> configurationFiles)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            var existingPaths = new List<string>();
            await using (var select = new SqlCommand(
                ArtifactConfigurationFileImportSql.SelectConfigurationFilePaths,
                connection,
                transaction))
            {
                select.Parameters.AddWithValue("@ArtifactId", artifactId);
                await using var reader = await select.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    existingPaths.Add(reader.GetString(0));
                }
            }

            var incomingPaths = configurationFiles
                .Select(static file => file.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stalePath in existingPaths.Where(path => !incomingPaths.Contains(path)))
            {
                await using var delete = new SqlCommand(
                    ArtifactConfigurationFileImportSql.DeleteConfigurationFileByPath,
                    connection,
                    transaction);
                delete.Parameters.AddWithValue("@ArtifactId", artifactId);
                delete.Parameters.AddWithValue("@RelativePath", stalePath);
                await delete.ExecuteNonQueryAsync();
            }

            foreach (var configurationFile in configurationFiles)
            {
                await using var upsert = new SqlCommand(
                    ArtifactConfigurationFileImportSql.UpsertPackageConfigurationFile,
                    connection,
                    transaction);
                upsert.Parameters.AddWithValue("@ArtifactId", artifactId);
                upsert.Parameters.AddWithValue("@RelativePath", configurationFile.RelativePath);
                upsert.Parameters.AddWithValue("@FileContent", configurationFile.FileContent);
                await upsert.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    internal static async Task CarryForwardArtifactConfigurationFilesAsync(
        SqlConnection connection,
        int artifactId,
        string artifactRelativePath)
    {
        await using var command = new SqlCommand(
            ArtifactConfigurationFileImportSql.CarryForwardOperatorEdits,
            connection);
        command.Parameters.AddWithValue("@ArtifactId", artifactId);

        string? sourceVersion = null;
        var items = new List<ArtifactConfigurationCarryForwardItem>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sourceVersion ??= reader.IsDBNull(0) ? null : reader.GetString(0);
            items.Add(new ArtifactConfigurationCarryForwardItem(
                reader.GetString(1),
                Enum.Parse<ArtifactConfigurationCarryForwardOutcome>(reader.GetString(2))));
        }

        var message = new ArtifactConfigurationCarryForwardResult(sourceVersion, items).BuildImportMessage();
        if (!string.IsNullOrWhiteSpace(message))
        {
            InstallOutput.Info($"> Artifact config files {artifactRelativePath}: {message}");
        }
    }

    public static async Task RefreshExistingHostAgentRuntimeSettingsAsync(
        BootstrapConfig config,
        HostAgentBootstrapServiceIdentity serviceIdentity)
    {
        if (!Directory.Exists(serviceIdentity.InstallPath))
        {
            InstallOutput.Info(
                $"> HostAgent service exists, but install path was not found; runtime settings were not refreshed. Path={serviceIdentity.InstallPath}");
            return;
        }

        InstallOutput.Info("> Refresh HostAgent runtime settings and credential store");
        await WriteHostAgentSettingsAsync(config, serviceIdentity.InstallPath, serviceIdentity);

        if (config.HostAgent.StartService)
        {
            StopService(serviceIdentity.ServiceName);
            StartService(serviceIdentity.ServiceName);
        }
    }
}
