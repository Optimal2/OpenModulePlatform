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

        const string sql = @"
SELECT ai.AppInstanceId,
       awd.WorkerTypeKey,
       ai.InstallPath,
       awd.PluginRelativePath
FROM omp.AppInstances ai
INNER JOIN omp.Hosts h ON h.HostId = ai.HostId
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
INNER JOIN omp.AppWorkerDefinitions awd ON awd.AppId = ai.AppId
INNER JOIN omp.Artifacts ar ON ar.ArtifactId = ai.ArtifactId
WHERE h.HostKey = @hostKey
  AND h.IsEnabled = 1
  AND a.IsEnabled = 1
  AND ai.IsEnabled = 1
  AND ai.IsAllowed = 1
  AND ai.DesiredState = @runningDesiredState
  AND ai.ArtifactId IS NOT NULL
  AND ai.InstallPath IS NOT NULL
  AND LTRIM(RTRIM(ai.InstallPath)) <> N''
  AND awd.IsEnabled = 1
  AND awd.RuntimeKind = @runtimeKind
  AND ar.IsEnabled = 1
ORDER BY ai.AppInstanceId;";

        var desired = new List<DesiredWorkerInstance>();
        var seen = new HashSet<Guid>();

        await using var conn = _db.Create();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@runningDesiredState", runningDesiredState);
        cmd.Parameters.AddWithValue("@runtimeKind", runtimeKind);

        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var appInstanceId = rdr.GetGuid(0);
            if (!seen.Add(appInstanceId))
            {
                throw new InvalidOperationException(
                    $"OMP database worker catalog returned duplicate AppInstanceId '{appInstanceId}'.");
            }

            var workerTypeKey = rdr.GetString(1);
            var installPath = rdr.GetString(2);
            var pluginRelativePath = rdr.GetString(3);
            var pluginAssemblyPath = ResolvePluginAssemblyPath(installPath, pluginRelativePath, appInstanceId);

            desired.Add(new DesiredWorkerInstance
            {
                AppInstanceId = appInstanceId,
                WorkerTypeKey = workerTypeKey.Trim(),
                PluginAssemblyPath = pluginAssemblyPath,
                ShutdownEventName = BuildShutdownEventName(appInstanceId)
            });
        }

        _logger.LogDebug(
            "Resolved desired workers from OMP database. HostKey={HostKey}, RuntimeKind={RuntimeKind}, Count={Count}",
            hostKey,
            runtimeKind,
            desired.Count);

        return desired;
    }

    private static string ResolvePluginAssemblyPath(string installPath, string pluginRelativePath, Guid appInstanceId)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            throw new InvalidOperationException(
                $"AppInstance '{appInstanceId}' is missing InstallPath for OMP database worker discovery.");
        }

        if (string.IsNullOrWhiteSpace(pluginRelativePath))
        {
            throw new InvalidOperationException(
                $"AppInstance '{appInstanceId}' resolved an empty PluginRelativePath from omp.AppWorkerDefinitions.");
        }

        if (Path.IsPathRooted(pluginRelativePath))
        {
            throw new InvalidOperationException(
                $"AppInstance '{appInstanceId}' resolved a rooted PluginRelativePath '{pluginRelativePath}'. The value must be relative to AppInstances.InstallPath.");
        }

        var installRoot = Path.GetFullPath(installPath.Trim());
        var sanitizedRelativePath = pluginRelativePath.Trim()
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidatePath = Path.GetFullPath(Path.Combine(installRoot, sanitizedRelativePath));

        var normalizedRoot = installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!candidatePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidatePath, installRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"AppInstance '{appInstanceId}' resolved PluginRelativePath '{pluginRelativePath}' outside InstallPath '{installPath}'.");
        }

        return candidatePath;
    }

    private static string BuildShutdownEventName(Guid appInstanceId)
    {
        return $"OpenModulePlatform.WorkerShutdown.{appInstanceId:N}";
    }
}
