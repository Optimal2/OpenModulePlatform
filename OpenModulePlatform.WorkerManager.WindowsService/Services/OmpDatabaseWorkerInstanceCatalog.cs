// File: OpenModulePlatform.WorkerManager.WindowsService/Services/OmpDatabaseWorkerInstanceCatalog.cs
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.WorkerManager.WindowsService.Contracts;
using OpenModulePlatform.WorkerManager.WindowsService.Models;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

public sealed class OmpDatabaseWorkerInstanceCatalog : IWorkerInstanceCatalog
{
    private readonly SqlConnectionFactory _db;
    private readonly IOptionsMonitor<WorkerManagerSettings> _settings;
    private readonly ILogger<OmpDatabaseWorkerInstanceCatalog> _logger;

    public OmpDatabaseWorkerInstanceCatalog(
        SqlConnectionFactory db,
        IOptionsMonitor<WorkerManagerSettings> settings,
        ILogger<OmpDatabaseWorkerInstanceCatalog> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DesiredWorkerInstance>> GetDesiredWorkersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = _settings.CurrentValue;

        var hostKey = settings.ResolveHostKey();
        var runtimeKind = settings.OmpDatabase.RuntimeKind.Trim();
        var runningDesiredState = settings.OmpDatabase.RunningDesiredState;
        var useHostArtifactCache = settings.OmpDatabase.UseHostArtifactCache;

        const string sql = @"
DECLARE @hostId uniqueidentifier;

SELECT @hostId = HostId
FROM omp.Hosts
WHERE HostKey = @hostKey
  AND IsEnabled = 1;

IF @hostId IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS uniqueidentifier) AS AppInstanceId,
        CAST(NULL AS uniqueidentifier) AS WorkerInstanceId,
        CAST(NULL AS nvarchar(150)) AS WorkerInstanceKey,
        CAST(NULL AS nvarchar(200)) AS WorkerTypeKey,
        CAST(NULL AS int) AS ArtifactId,
        CAST(NULL AS nvarchar(64)) AS PackageType,
        CAST(NULL AS nvarchar(500)) AS InstallPath,
        CAST(NULL AS bit) AS IsProvisionedFromHostArtifactCache,
        CAST(NULL AS nvarchar(400)) AS PluginRelativePath,
        CAST(NULL AS nvarchar(max)) AS ConfigurationJson,
        CAST(NULL AS nvarchar(50)) AS ArtifactVersion;
    RETURN;
END;

WITH HostRoles AS
(
    SELECT HostTemplateId
    FROM omp.HostDeploymentAssignments
    WHERE HostId = @hostId
      AND IsActive = 1
),
WorkerRows AS
(
    SELECT
        ai.AppInstanceId,
        wi.WorkerInstanceId,
        wi.WorkerInstanceKey,
        awd.WorkerTypeKey,
        COALESCE(wi.ArtifactId, ai.ArtifactId) AS EffectiveArtifactId,
        ar.PackageType,
        CASE WHEN @useHostArtifactCache = 1 THEN COALESCE(has.LocalPath, ai.InstallPath) ELSE ai.InstallPath END AS InstallPath,
        CASE WHEN @useHostArtifactCache = 1 AND has.LocalPath IS NOT NULL THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS IsProvisionedFromHostArtifactCache,
        awd.PluginRelativePath,
        wi.ConfigurationJson,
        -- R12-F2. Carried on the definition so the manager can report which artifact
        -- version a worker was actually started from; see DesiredWorkerInstance.ArtifactVersion.
        ar.Version AS ArtifactVersion,
        wi.SortOrder
    FROM omp.WorkerInstances wi
    INNER JOIN omp.AppInstances ai ON ai.AppInstanceId = wi.AppInstanceId
    INNER JOIN omp.Apps a ON a.AppId = ai.AppId
    INNER JOIN omp.AppWorkerDefinitions awd ON awd.AppId = ai.AppId
    INNER JOIN omp.Artifacts ar ON ar.ArtifactId = COALESCE(wi.ArtifactId, ai.ArtifactId)
    LEFT JOIN omp.HostArtifactStates has
        ON has.HostId = CASE
            WHEN wi.HostId IS NOT NULL THEN wi.HostId
            WHEN ai.HostId IS NOT NULL THEN ai.HostId
            ELSE @hostId
        END
       AND has.ArtifactId = ar.ArtifactId
       AND has.ProvisioningState = 2
    WHERE
      (
          wi.HostId = @hostId
          OR (wi.HostId IS NULL AND ai.HostId = @hostId)
          OR
          (
              wi.HostId IS NULL
              AND ai.HostId IS NULL
              AND ai.TargetHostTemplateId IS NOT NULL
              AND EXISTS (SELECT 1 FROM HostRoles hr WHERE hr.HostTemplateId = ai.TargetHostTemplateId)
          )
      )
      AND a.IsEnabled = 1
      AND ai.IsEnabled = 1
      AND ai.IsAllowed = 1
      AND wi.IsEnabled = 1
      AND wi.IsAllowed = 1
      AND wi.DesiredState = @runningDesiredState
      AND COALESCE(wi.ArtifactId, ai.ArtifactId) IS NOT NULL
      AND awd.IsEnabled = 1
      AND awd.RuntimeKind = @runtimeKind
      AND ar.IsEnabled = 1

    UNION ALL

    SELECT
        ai.AppInstanceId,
        ai.AppInstanceId AS WorkerInstanceId,
        ai.AppInstanceKey AS WorkerInstanceKey,
        awd.WorkerTypeKey,
        ai.ArtifactId AS EffectiveArtifactId,
        ar.PackageType,
        CASE WHEN @useHostArtifactCache = 1 THEN COALESCE(has.LocalPath, ai.InstallPath) ELSE ai.InstallPath END AS InstallPath,
        CASE WHEN @useHostArtifactCache = 1 AND has.LocalPath IS NOT NULL THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS IsProvisionedFromHostArtifactCache,
        awd.PluginRelativePath,
        CAST(NULL AS nvarchar(max)) AS ConfigurationJson,
        ar.Version AS ArtifactVersion,
        ai.SortOrder
    FROM omp.AppInstances ai
    INNER JOIN omp.Apps a ON a.AppId = ai.AppId
    INNER JOIN omp.AppWorkerDefinitions awd ON awd.AppId = ai.AppId
    INNER JOIN omp.Artifacts ar ON ar.ArtifactId = ai.ArtifactId
    LEFT JOIN omp.HostArtifactStates has
        ON has.HostId = CASE WHEN ai.HostId IS NOT NULL THEN ai.HostId ELSE @hostId END
       AND has.ArtifactId = ar.ArtifactId
       AND has.ProvisioningState = 2
    WHERE
      (
          ai.HostId = @hostId
          OR
          (
              ai.HostId IS NULL
              AND ai.TargetHostTemplateId IS NOT NULL
              AND EXISTS (SELECT 1 FROM HostRoles hr WHERE hr.HostTemplateId = ai.TargetHostTemplateId)
          )
      )
      AND a.IsEnabled = 1
      AND ai.IsEnabled = 1
      AND ai.IsAllowed = 1
      AND ai.DesiredState = @runningDesiredState
      AND ai.ArtifactId IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM omp.WorkerInstances wi WHERE wi.AppInstanceId = ai.AppInstanceId)
      AND awd.IsEnabled = 1
      AND awd.RuntimeKind = @runtimeKind
      AND ar.IsEnabled = 1
)
SELECT
    AppInstanceId,
    WorkerInstanceId,
    WorkerInstanceKey,
    WorkerTypeKey,
    EffectiveArtifactId,
    PackageType,
    InstallPath,
    IsProvisionedFromHostArtifactCache,
    PluginRelativePath,
    ConfigurationJson,
    ArtifactVersion
FROM WorkerRows
WHERE PluginRelativePath IS NOT NULL
  AND LTRIM(RTRIM(PluginRelativePath)) <> N''
ORDER BY SortOrder, WorkerInstanceKey, WorkerInstanceId;";

        var desired = new List<DesiredWorkerInstance>();
        var seen = new HashSet<Guid>();

        await using var conn = _db.Create();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@runningDesiredState", runningDesiredState);
        cmd.Parameters.AddWithValue("@runtimeKind", runtimeKind);
        cmd.Parameters.AddWithValue("@useHostArtifactCache", useHostArtifactCache);

        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            // R7-F6. One broken catalog row costs THAT row, never the host's whole
            // reconciliation: before this, a single row that ResolvePluginAssemblyPath
            // rejected (or a duplicate id, or a NULL the query did not promise) escaped
            // GetDesiredWorkersAsync and failed EVERY worker on the host, retried and
            // refailed every cycle for as long as the row stayed broken.
            WorkerCatalogRow row;
            try
            {
                row = ReadRow(rdr);
            }
            catch (Exception ex) when (ex is InvalidCastException or InvalidOperationException)
            {
                _logger.LogError(
                    ex,
                    "Skipping an unreadable worker catalog row; reconciliation continues for the remaining rows. HostKey={HostKey}",
                    hostKey);
                continue;
            }

            if (TryCreateDesiredWorker(row, runtimeKind, seen, out var instance, out var problem))
            {
                desired.Add(instance);
            }
            else
            {
                _logger.LogError(
                    "Skipping a broken worker catalog row; reconciliation continues for the remaining rows. HostKey={HostKey}, RuntimeKind={RuntimeKind}, AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, ArtifactId={ArtifactId}, PackageType={PackageType}, Problem={Problem}",
                    hostKey,
                    runtimeKind,
                    row.AppInstanceId,
                    row.WorkerInstanceId,
                    row.ArtifactId,
                    row.PackageType,
                    problem);
            }
        }

        _logger.LogDebug(
            "Resolved desired workers from OMP database. HostKey={HostKey}, RuntimeKind={RuntimeKind}, Count={Count}, UseHostArtifactCache={UseHostArtifactCache}",
            hostKey,
            runtimeKind,
            desired.Count,
            useHostArtifactCache);

        return desired;
    }

    private static WorkerCatalogRow ReadRow(SqlDataReader rdr)
    {
        return new WorkerCatalogRow
        {
            AppInstanceId = rdr.GetGuid(0),
            WorkerInstanceId = rdr.GetGuid(1),
            WorkerInstanceKey = rdr.GetString(2),
            WorkerTypeKey = rdr.GetString(3),
            ArtifactId = rdr.IsDBNull(4) ? null : rdr.GetInt32(4),
            PackageType = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            InstallPath = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            IsProvisionedFromHostArtifactCache = rdr.GetBoolean(7),
            PluginRelativePath = rdr.GetString(8),
            ConfigurationJson = rdr.IsDBNull(9) ? null : rdr.GetString(9),
            ArtifactVersion = rdr.IsDBNull(10) ? null : rdr.GetString(10).Trim()
        };
    }

    /// <summary>
    /// Validates one catalog row and maps it to a desired worker definition (R7-F6).
    /// A broken row -- duplicate id, an artifact package type incompatible with the
    /// runtime kind, an unresolvable plugin path -- is reported through
    /// <paramref name="problem"/> and skipped; the method never throws for row-level
    /// data problems, because that exception is what used to fail reconciliation for
    /// every other worker on the host.
    /// </summary>
    public static bool TryCreateDesiredWorker(
        WorkerCatalogRow row,
        string runtimeKind,
        ISet<Guid> seenWorkerInstanceIds,
        [NotNullWhen(true)] out DesiredWorkerInstance? worker,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(seenWorkerInstanceIds);

        worker = null;
        problem = null;

        if (!seenWorkerInstanceIds.Add(row.WorkerInstanceId))
        {
            problem = $"OMP database worker catalog returned duplicate WorkerInstanceId '{row.WorkerInstanceId}'.";
            return false;
        }

        if (!IsArtifactPackageTypeCompatibleWithRuntimeKind(runtimeKind, row.PackageType))
        {
            problem = $"Artifact package type '{row.PackageType}' is incompatible with runtime kind '{runtimeKind}'.";
            return false;
        }

        string pluginAssemblyPath;
        try
        {
            pluginAssemblyPath = string.IsNullOrWhiteSpace(row.InstallPath)
                ? string.Empty
                : ResolvePluginAssemblyPath(row.InstallPath, row.PluginRelativePath, row.AppInstanceId, row.WorkerInstanceId);
        }
        catch (InvalidOperationException ex)
        {
            problem = ex.Message;
            return false;
        }

        worker = new DesiredWorkerInstance
        {
            AppInstanceId = row.AppInstanceId,
            WorkerInstanceId = row.WorkerInstanceId,
            WorkerInstanceKey = row.WorkerInstanceKey.Trim(),
            WorkerTypeKey = row.WorkerTypeKey.Trim(),
            ArtifactId = row.ArtifactId,
            ArtifactVersion = row.ArtifactVersion,
            InstallRootPath = row.InstallPath,
            IsProvisionedFromHostArtifactCache = row.IsProvisionedFromHostArtifactCache,
            PluginRelativePath = row.PluginRelativePath.Trim(),
            PluginAssemblyPath = pluginAssemblyPath,
            ConfigurationJson = row.ConfigurationJson,
            ShutdownEventName = BuildShutdownEventName(row.WorkerInstanceId)
        };
        return true;
    }

    private static string ResolvePluginAssemblyPath(
        string installPath,
        string pluginRelativePath,
        Guid appInstanceId,
        Guid workerInstanceId)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            throw new InvalidOperationException(
                $"WorkerInstance '{workerInstanceId}' for AppInstance '{appInstanceId}' is missing InstallPath for OMP database worker discovery.");
        }

        if (string.IsNullOrWhiteSpace(pluginRelativePath))
        {
            throw new InvalidOperationException(
                $"WorkerInstance '{workerInstanceId}' for AppInstance '{appInstanceId}' resolved an empty PluginRelativePath from omp.AppWorkerDefinitions.");
        }

        if (Path.IsPathRooted(pluginRelativePath))
        {
            throw new InvalidOperationException(
                $"WorkerInstance '{workerInstanceId}' resolved a rooted PluginRelativePath '{pluginRelativePath}'. The value must be relative to the artifact install path.");
        }

        var installRoot = Path.GetFullPath(installPath.Trim());
        var normalizedInstallRoot = installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sanitizedRelativePath = pluginRelativePath.Trim()
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidatePath = string.IsNullOrWhiteSpace(sanitizedRelativePath)
            ? Path.GetFullPath(normalizedInstallRoot)
            : Path.GetFullPath($"{normalizedInstallRoot}{Path.DirectorySeparatorChar}{sanitizedRelativePath}");

        var normalizedRoot = normalizedInstallRoot + Path.DirectorySeparatorChar;

        if (!candidatePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidatePath, installRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"WorkerInstance '{workerInstanceId}' resolved PluginRelativePath '{pluginRelativePath}' outside InstallPath '{installPath}'.");
        }

        return candidatePath;
    }

    private static string BuildShutdownEventName(Guid workerInstanceId)
    {
        return $"OpenModulePlatform.WorkerShutdown.{workerInstanceId:N}";
    }

    private static bool IsArtifactPackageTypeCompatibleWithRuntimeKind(string runtimeKind, string? packageType)
    {
        if (string.Equals(runtimeKind, "windows-worker-plugin", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(packageType, "worker", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }
}
