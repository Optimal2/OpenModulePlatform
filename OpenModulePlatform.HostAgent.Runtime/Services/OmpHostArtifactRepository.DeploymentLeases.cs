using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed partial class OmpHostArtifactRepository
{
    public async Task<IReadOnlyDictionary<string, string?>> ReadDeploymentLockSettingsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT def.ConfigSetting, value.ConfigValue
            FROM omp.config_setting_definitions def
            OUTER APPLY (
                SELECT TOP (1) cs.ConfigValue
                FROM omp.config_settings cs
                WHERE cs.ConfigSettingId = def.ConfigSettingId
                  AND cs.ConfigUsr IS NULL AND cs.ConfigPermission IS NULL AND cs.ConfigRole IS NULL
                ORDER BY cs.ConfigPriority DESC, cs.ConfigId DESC
            ) value
            WHERE def.ConfigCategory = N'HostAgent' AND def.IsEnabled = 1
              AND def.ConfigSetting IN (N'DeploymentLockScope', N'DeploymentLeaseSeconds');
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return result;
    }

    public async Task<AppDeploymentLeaseResult> AcquireAppDeploymentLeaseAsync(
        string scopeKey, Guid hostId, string reason, int leaseSeconds, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (scopeKey.Length > 200) throw new ArgumentOutOfRangeException(nameof(scopeKey));
        if (leaseSeconds is < 1 or > 86400) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        const string sql = """
            SET XACT_ABORT ON;
            DECLARE @now datetime2(3) = SYSUTCDATETIME();
            DECLARE @existingHost uniqueidentifier, @existingUntil datetime2(3);
            DECLARE @takenOver bit = 0, @acquired bit = 0;
            -- Coordinate overlapping scope snapshots while an operator changes modes.
            -- These transaction locks cover acquisition only, never runtime deployment.
            DECLARE @blockingKey nvarchar(200);
            SELECT TOP (1) @blockingKey = LeaseScopeKey
            FROM omp.AppDeploymentLeases WITH (UPDLOCK, HOLDLOCK)
            WHERE HostId <> @hostId AND LeaseUntilUtc > @now
              AND (LeaseScopeKey = @scopeKey OR LeaseScopeKey = N'host:*' OR @scopeKey = N'host:*')
            ORDER BY LeaseScopeKey;
            IF @blockingKey IS NOT NULL
            BEGIN
                SELECT CAST(0 AS bit), lease.HostId, host.HostKey, lease.LeaseToken, lease.LeaseUntilUtc, CAST(0 AS bit)
                FROM omp.AppDeploymentLeases lease
                INNER JOIN omp.Hosts host ON host.HostId = lease.HostId
                WHERE lease.LeaseScopeKey = @blockingKey;
                RETURN;
            END;
            SELECT @existingHost = HostId, @existingUntil = LeaseUntilUtc
            FROM omp.AppDeploymentLeases WITH (UPDLOCK, HOLDLOCK)
            WHERE LeaseScopeKey = @scopeKey;
            IF @existingHost IS NULL
            BEGIN
                INSERT omp.AppDeploymentLeases(LeaseScopeKey, HostId, LeaseToken, Reason, LeaseUntilUtc)
                VALUES (@scopeKey, @hostId, @token, @reason, DATEADD(second, @seconds, @now));
                SET @acquired = 1;
            END
            ELSE IF @existingUntil <= @now OR @existingHost = @hostId
            BEGIN
                SET @takenOver = CASE WHEN @existingUntil <= @now AND @existingHost <> @hostId THEN 1 ELSE 0 END;
                UPDATE omp.AppDeploymentLeases
                SET HostId = @hostId, LeaseToken = @token, Reason = @reason,
                    LeaseUntilUtc = DATEADD(second, @seconds, @now), UpdatedUtc = @now
                WHERE LeaseScopeKey = @scopeKey;
                SET @acquired = 1;
            END;
            SELECT @acquired, lease.HostId, host.HostKey, lease.LeaseToken, lease.LeaseUntilUtc, @takenOver
            FROM omp.AppDeploymentLeases lease
            INNER JOIN omp.Hosts host ON host.HostId = lease.HostId
            WHERE lease.LeaseScopeKey = @scopeKey;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        cmd.Parameters.AddWithValue("@token", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@reason", reason.Length > 400 ? reason[..400] : reason);
        cmd.Parameters.AddWithValue("@seconds", leaseSeconds);
        AppDeploymentLeaseResult result;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Deployment lease owner could not be resolved.");
            result = new(reader.GetBoolean(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3),
                DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc), reader.GetBoolean(5));
        }
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task ReleaseAppDeploymentLeaseAsync(string scopeKey, Guid leaseToken, string? reason, CancellationToken ct)
    {
        // Retain the last outcome for diagnosis. A stale owner cannot expire a successor's lease.
        const string sql = """
            UPDATE omp.AppDeploymentLeases
            SET LeaseUntilUtc = SYSUTCDATETIME(), UpdatedUtc = SYSUTCDATETIME(), Reason = @reason
            WHERE LeaseScopeKey = @scopeKey AND LeaseToken = @token;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@token", leaseToken);
        cmd.Parameters.AddWithValue("@reason", (object?)(reason?.Length > 400 ? reason[..400] : reason) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
