// File: OpenModulePlatform.Web.ExampleServiceAppModule/Services/ExampleServiceAppModuleAdminRepository.cs
using OpenModulePlatform.Web.ExampleServiceAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace OpenModulePlatform.Web.ExampleServiceAppModule.Services;

public sealed class ExampleServiceAppModuleAdminRepository
{
    private readonly SqlConnectionFactory _db;
    private readonly ILogger<ExampleServiceAppModuleAdminRepository> _log;

    private const string ModuleKey = "example_serviceapp_module";
    private const string ModuleSchema = "omp_example_serviceapp_module";
    private const string ServiceAppKey = "example_serviceapp_module_service";

    public ExampleServiceAppModuleAdminRepository(SqlConnectionFactory db, ILogger<ExampleServiceAppModuleAdminRepository> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<OverviewRow> GetOverviewAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT @moduleKey,
       @moduleSchema,
       (SELECT COUNT(1) FROM omp_example_serviceapp_module.Configurations WHERE VersionNo = 0),
       (SELECT COUNT(1)
        FROM omp.AppInstances ai
        INNER JOIN omp.Apps a ON a.AppId = ai.AppId
        WHERE a.AppKey = @serviceAppKey),
       (SELECT COUNT(1) FROM omp_example_serviceapp_module.Jobs WHERE Status IN (0,1));";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@moduleKey", ModuleKey);
        cmd.Parameters.AddWithValue("@moduleSchema", ModuleSchema);
        cmd.Parameters.AddWithValue("@serviceAppKey", ServiceAppKey);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        await rdr.ReadAsync(ct);

        return new OverviewRow
        {
            ModuleKey = rdr.GetString(0),
            SchemaName = rdr.GetString(1),
            ActiveConfigurationCount = rdr.GetInt32(2),
            ServiceAppInstanceCount = rdr.GetInt32(3),
            OpenJobCount = rdr.GetInt32(4)
        };
    }

    public async Task<IReadOnlyList<AppInstanceRow>> GetAppInstancesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT ai.AppInstanceId,
       ai.HostId,
       h.HostKey,
       ai.AppInstanceKey,
       ai.DisplayName,
       ai.InstallationName,
       ai.RoutePath,
       ai.LastSeenUtc,
       ai.LastLogin,
       ai.LastClientHostName,
       ai.LastClientIp,
       ai.ExpectedLogin,
       ai.ExpectedClientHostName,
       ai.ExpectedClientIp,
       ai.VerificationStatus,
       ai.LastVerifiedUtc,
       ai.IsAllowed,
       ai.DesiredState,
       ai.ConfigId,
       ai.ArtifactId,
       ar.Version,
       ar.TargetName,
       ai.UpdatedUtc
FROM omp.AppInstances ai
LEFT JOIN omp.Hosts h ON h.HostId = ai.HostId
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
LEFT JOIN omp.Artifacts ar ON ar.ArtifactId = ai.ArtifactId
WHERE a.AppKey = @serviceAppKey
ORDER BY h.HostKey, ai.AppInstanceKey;";

        var rows = new List<AppInstanceRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@serviceAppKey", ServiceAppKey);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new AppInstanceRow
            {
                AppInstanceId = rdr.GetGuid(0),
                HostId = rdr.IsDBNull(1) ? null : rdr.GetGuid(1),
                HostKey = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                AppInstanceKey = rdr.GetString(3),
                DisplayName = rdr.GetString(4),
                InstallationName = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                RoutePath = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                LastSeenUtc = rdr.IsDBNull(7) ? null : rdr.GetDateTime(7),
                LastLogin = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                LastClientHostName = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                LastClientIp = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                ExpectedLogin = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                ExpectedClientHostName = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                ExpectedClientIp = rdr.IsDBNull(13) ? null : rdr.GetString(13),
                VerificationStatus = rdr.GetByte(14),
                LastVerifiedUtc = rdr.IsDBNull(15) ? null : rdr.GetDateTime(15),
                IsAllowed = rdr.GetBoolean(16),
                DesiredState = rdr.GetByte(17),
                ConfigId = rdr.IsDBNull(18) ? null : rdr.GetInt32(18),
                ArtifactId = rdr.IsDBNull(19) ? null : rdr.GetInt32(19),
                ArtifactVersion = rdr.IsDBNull(20) ? null : rdr.GetString(20),
                ArtifactTargetName = rdr.IsDBNull(21) ? null : rdr.GetString(21),
                UpdatedUtc = rdr.GetDateTime(22)
            });
        }
        return rows;
    }

    public async Task<AppInstanceRow?> GetAppInstanceAsync(Guid appInstanceId, CancellationToken ct)
    {
        var rows = await GetAppInstancesAsync(ct);
        return rows.FirstOrDefault(x => x.AppInstanceId == appInstanceId);
    }

    public async Task UpdateAppInstanceAsync(Guid appInstanceId, bool isAllowed, byte desiredState, int? configId, int? artifactId, string actor, CancellationToken ct)
    {
        const string sql = @"
UPDATE ai
SET IsAllowed = @isAllowed,
    DesiredState = @desiredState,
    ConfigId = @configId,
    ArtifactId = @artifactId,
    UpdatedUtc = SYSUTCDATETIME()
FROM omp.AppInstances ai
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
WHERE ai.AppInstanceId = @appInstanceId
  AND a.AppKey = @serviceAppKey;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@serviceAppKey", ServiceAppKey);
        cmd.Parameters.AddWithValue("@isAllowed", isAllowed);
        cmd.Parameters.AddWithValue("@desiredState", desiredState);
        cmd.Parameters.AddWithValue("@configId", (object?)configId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@artifactId", (object?)artifactId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        const string auditSql = @"
INSERT INTO omp.AuditLog(Actor, Action, TargetType, TargetId, BeforeJson, AfterJson)
VALUES(@actor, @action, @targetType, @targetId, NULL, @afterJson);";

        try
        {
            await using var audit = new SqlCommand(auditSql, conn);
            audit.Parameters.AddWithValue("@actor", actor);
            audit.Parameters.AddWithValue("@action", ServiceAppKey + ".appinstance.update");
            audit.Parameters.AddWithValue("@targetType", "AppInstance");
            audit.Parameters.AddWithValue("@targetId", appInstanceId.ToString());
            audit.Parameters.AddWithValue("@afterJson", JsonSerializer.Serialize(new { appInstanceId, isAllowed, desiredState, configId, artifactId }));
            await audit.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex)
        {
            _log.LogWarning(ex, "Audit logging failed for app instance {AppInstanceId}.", appInstanceId);
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "Audit logging failed for app instance {AppInstanceId}.", appInstanceId);
        }
    }

    public async Task<IReadOnlyList<ConfigurationRow>> GetConfigurationsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT c.ConfigId,
       c.VersionNo,
       c.ConfigJson,
       c.Comment,
       c.CreatedUtc,
       c.CreatedBy,
       CAST(ISNULL(a.AssignedInstances, 0) AS int)
FROM omp_example_serviceapp_module.Configurations c
OUTER APPLY (
    SELECT COUNT(*) AS AssignedInstances
    FROM omp.AppInstances ai
    INNER JOIN omp.Apps a ON a.AppId = ai.AppId
    WHERE a.AppKey = @serviceAppKey AND ai.ConfigId = c.ConfigId
) a
WHERE c.VersionNo = 0
ORDER BY c.ConfigId DESC;";

        var rows = new List<ConfigurationRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@serviceAppKey", ServiceAppKey);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ConfigurationRow
            {
                ConfigId = rdr.GetInt32(0),
                VersionNo = rdr.GetInt32(1),
                ConfigJson = rdr.GetString(2),
                Comment = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                CreatedUtc = rdr.GetDateTime(4),
                CreatedBy = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                AssignedInstallations = rdr.GetInt32(6)
            });
        }
        return rows;
    }

    public async Task<ConfigurationRow?> GetConfigurationAsync(int configId, CancellationToken ct)
    {
        const string sql = @"
SELECT ConfigId, VersionNo, ConfigJson, Comment, CreatedUtc, CreatedBy
FROM omp_example_serviceapp_module.Configurations
WHERE ConfigId = @configId AND VersionNo = 0;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@configId", configId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
            return null;

        return new ConfigurationRow
        {
            ConfigId = rdr.GetInt32(0),
            VersionNo = rdr.GetInt32(1),
            ConfigJson = rdr.GetString(2),
            Comment = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            CreatedUtc = rdr.GetDateTime(4),
            CreatedBy = rdr.IsDBNull(5) ? null : rdr.GetString(5)
        };
    }

    public async Task UpdateConfigurationAsync(int configId, string configJson, string? comment, string actor, CancellationToken ct)
    {
        const string sql = @"
UPDATE omp_example_serviceapp_module.Configurations
SET ConfigJson = @configJson,
    Comment = @comment,
    CreatedBy = @actor,
    CreatedUtc = SYSUTCDATETIME()
WHERE ConfigId = @configId AND VersionNo = 0;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@configId", configId);
        cmd.Parameters.AddWithValue("@configJson", configJson);
        cmd.Parameters.AddWithValue("@comment", (object?)comment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@actor", actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<JobRow>> GetJobsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT JobId, RequestType, PayloadJson, Status, Attempts, RequestedUtc, RequestedBy, LastError, ResultJson
FROM omp_example_serviceapp_module.Jobs
ORDER BY JobId DESC;";

        var rows = new List<JobRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new JobRow
            {
                JobId = rdr.GetInt64(0),
                RequestType = rdr.GetString(1),
                PayloadJson = rdr.GetString(2),
                Status = rdr.GetByte(3),
                Attempts = rdr.GetInt32(4),
                RequestedUtc = rdr.GetDateTime(5),
                RequestedBy = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                LastError = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                ResultJson = rdr.IsDBNull(8) ? null : rdr.GetString(8)
            });
        }
        return rows;
    }

    public async Task EnqueueJobAsync(string requestType, string payloadJson, string actor, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp_example_serviceapp_module.Jobs(RequestType, PayloadJson, Status, RequestedUtc, RequestedBy, UpdatedUtc)
VALUES(@requestType, @payloadJson, 0, SYSUTCDATETIME(), @actor, SYSUTCDATETIME());";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@requestType", requestType);
        cmd.Parameters.AddWithValue("@payloadJson", payloadJson);
        cmd.Parameters.AddWithValue("@actor", actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
