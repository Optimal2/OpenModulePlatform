// File: OpenModulePlatform.WorkerManager.WindowsService/Services/OmpDatabaseWorkerInstanceCatalog.cs
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
        settings.Validate();
        settings.OmpDatabase.Validate();

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
        CAST(NULL AS nvarchar(500)) AS InstallPath,
        CAST(NULL AS bit) AS IsProvisionedFromHostArtifactCache,
        CAST(NULL AS nvarchar(400)) AS PluginRelativePath,
        CAST(NULL AS nvarchar(max)) AS ConfigurationJson;
    RETURN;
END;

WITH WorkerRows AS
(
    SELECT
        ai.AppInstanceId,
        wi.WorkerInstanceId,
        wi.WorkerInstanceKey,
        awd.WorkerTypeKey,
        COALESCE(wi.ArtifactId, ai.ArtifactId) AS EffectiveArtifactId,
        CASE WHEN @useHostArtifactCache = 1 THEN COALESCE(has.LocalPath, ai.InstallPath) ELSE ai.InstallPath END AS InstallPath,
        CASE WHEN @useHostArtifactCache = 1 AND has.LocalPath IS NOT NULL THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS IsProvisionedFromHostArtifactCache,
        awd.PluginRelativePath,
        wi.ConfigurationJson,
        wi.SortOrder
    FROM omp.WorkerInstances wi
    INNER JOIN omp.AppInstances ai ON ai.AppInstanceId = wi.AppInstanceId
    INNER JOIN omp.Apps a ON a.AppId = ai.AppId
    INNER JOIN omp.AppWorkerDefinitions awd ON awd.AppId = ai.AppId
    INNER JOIN omp.Artifacts ar ON ar.ArtifactId = COALESCE(wi.ArtifactId, ai.ArtifactId)
    LEFT JOIN omp.HostArtifactStates has
        ON has.HostId = COALESCE(wi.HostId, ai.HostId)
       AND has.ArtifactId = ar.ArtifactId
       AND has.ProvisioningState = 2
    WHERE COALESCE(wi.HostId, ai.HostId) = @hostId
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
        CASE WHEN @useHostArtifactCache = 1 THEN COALESCE(has.LocalPath, ai.InstallPath) ELSE ai.InstallPath END AS InstallPath,
        CASE WHEN @useHostArtifactCache = 1 AND has.LocalPath IS NOT NULL THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS IsProvisionedFromHostArtifactCache,
        awd.PluginRelativePath,
        CAST(NULL AS nvarchar(max)) AS ConfigurationJson,
        ai.SortOrder
    FROM omp.AppInstances ai
    INNER JOIN omp.Apps a ON a.AppId = ai.AppId
    INNER JOIN omp.AppWorkerDefinitions awd ON awd.AppId = ai.AppId
    INNER JOIN omp.Artifacts ar ON ar.ArtifactId = ai.ArtifactId
    LEFT JOIN omp.HostArtifactStates has
        ON has.HostId = ai.HostId
       AND has.ArtifactId = ar.ArtifactId
       AND has.ProvisioningState = 2
    WHERE ai.HostId = @hostId
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
    InstallPath,
    IsProvisionedFromHostArtifactCache,
    PluginRelativePath,
    ConfigurationJson
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
            var appInstanceId = rdr.GetGuid(0);
            var workerInstanceId = rdr.GetGuid(1);
            if (!seen.Add(workerInstanceId))
            {
                throw new InvalidOperationException(
                    $"OMP database worker catalog returned duplicate WorkerInstanceId '{workerInstanceId}'.");
            }

            var workerInstanceKey = rdr.GetString(2);
            var workerTypeKey = rdr.GetString(3);
            var artifactId = rdr.IsDBNull(4) ? (int?)null : rdr.GetInt32(4);
            var installPath = rdr.IsDBNull(5) ? null : rdr.GetString(5);
            var isProvisionedFromHostArtifactCache = rdr.GetBoolean(6);
            var pluginRelativePath = rdr.GetString(7);
            var configurationJson = rdr.IsDBNull(8) ? null : rdr.GetString(8);
            var pluginAssemblyPath = string.IsNullOrWhiteSpace(installPath)
                ? string.Empty
                : ResolvePluginAssemblyPath(installPath, pluginRelativePath, appInstanceId, workerInstanceId);

            desired.Add(new DesiredWorkerInstance
            {
                AppInstanceId = appInstanceId,
                WorkerInstanceId = workerInstanceId,
                WorkerInstanceKey = workerInstanceKey.Trim(),
                WorkerTypeKey = workerTypeKey.Trim(),
                ArtifactId = artifactId,
                InstallRootPath = installPath,
                IsProvisionedFromHostArtifactCache = isProvisionedFromHostArtifactCache,
                PluginRelativePath = pluginRelativePath.Trim(),
                PluginAssemblyPath = pluginAssemblyPath,
                ConfigurationJson = configurationJson,
                ShutdownEventName = BuildShutdownEventName(workerInstanceId)
            });
        }

        _logger.LogDebug(
            "Resolved desired workers from OMP database. HostKey={HostKey}, RuntimeKind={RuntimeKind}, Count={Count}, UseHostArtifactCache={UseHostArtifactCache}",
            hostKey,
            runtimeKind,
            desired.Count,
            useHostArtifactCache);

        return desired;
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
}
