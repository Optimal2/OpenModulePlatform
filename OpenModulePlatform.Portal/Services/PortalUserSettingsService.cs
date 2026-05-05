using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;

namespace OpenModulePlatform.Portal.Services;

public sealed class PortalUserSettingsService
{
    public const int DisplayNameMaxLength = 200;

    private readonly SqlConnectionFactory _db;

    public PortalUserSettingsService(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<PortalAccountSettings?> GetAccountSettingsAsync(int userId, CancellationToken ct)
    {
        const string sql = @"
SELECT u.display_name,
       CAST(ISNULL(s.admin_metrics_collapsed, 0) AS bit)
FROM omp.users u
LEFT JOIN omp_portal.user_settings s ON s.user_id = u.user_id
WHERE u.user_id = @user_id
  AND u.account_status = 1;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        AddUserId(cmd, userId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new PortalAccountSettings(
            rdr.GetString(0),
            rdr.GetBoolean(1));
    }

    public async Task<PortalUserSettings> GetForUserAsync(int userId, CancellationToken ct)
    {
        const string sql = @"
SELECT admin_metrics_collapsed
FROM omp_portal.user_settings
WHERE user_id = @user_id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        AddUserId(cmd, userId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return new PortalUserSettings(result is bool collapsed && collapsed);
    }

    public async Task<bool> UpdateDisplayNameAsync(int userId, string displayName, CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.users
SET display_name = @display_name,
    updated_at = SYSUTCDATETIME()
WHERE user_id = @user_id
  AND account_status = 1;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        AddUserId(cmd, userId);
        cmd.Parameters.Add("@display_name", SqlDbType.NVarChar, DisplayNameMaxLength).Value = displayName;

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task UpsertAdminMetricsCollapsedAsync(int userId, bool value, CancellationToken ct)
    {
        const string sql = @"
UPDATE omp_portal.user_settings
SET admin_metrics_collapsed = @admin_metrics_collapsed,
    updated_at = SYSUTCDATETIME()
WHERE user_id = @user_id;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO omp_portal.user_settings(user_id, admin_metrics_collapsed)
    VALUES(@user_id, @admin_metrics_collapsed);
END;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        AddUserId(cmd, userId);
        cmd.Parameters.Add("@admin_metrics_collapsed", SqlDbType.Bit).Value = value;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddUserId(SqlCommand cmd, int userId)
        => cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
}

public sealed record PortalAccountSettings(string DisplayName, bool AdminMetricsCollapsed);

public sealed record PortalUserSettings(bool AdminMetricsCollapsed);
