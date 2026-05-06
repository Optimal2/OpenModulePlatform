using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;

namespace OpenModulePlatform.Portal.Services;

public sealed class PortalUserSettingsService
{
    public const int DisplayNameMaxLength = 200;
    private const int ActiveAccountStatus = 1;
    private const int ProviderUserKeyMaxLength = 1000;
    private const string AdProviderDisplayName = "AD";

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

    public async Task<CreateSelfServiceAdAccountResult> CreateSelfServiceAdAccountAsync(
        string displayName,
        IEnumerable<string> providerUserKeys,
        CancellationToken ct)
    {
        var keys = providerUserKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Where(key => key.Length <= ProviderUserKeyMaxLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (keys.Length == 0)
        {
            return new CreateSelfServiceAdAccountResult(CreateSelfServiceAdAccountStatus.MissingProviderKeys);
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            var providerId = await GetAuthProviderIdAsync(conn, tx, AdProviderDisplayName, ct);
            if (providerId is null)
            {
                await tx.RollbackAsync(ct);
                return new CreateSelfServiceAdAccountResult(CreateSelfServiceAdAccountStatus.ProviderUnavailable);
            }

            var existing = await GetExistingAuthLinkAsync(conn, tx, providerId.Value, keys, ct);
            if (existing is not null)
            {
                await tx.RollbackAsync(ct);
                return new CreateSelfServiceAdAccountResult(
                    CreateSelfServiceAdAccountStatus.AlreadyLinkedToAnotherUser,
                    ExistingUserId: existing.Value.UserId);
            }

            var userId = await InsertUserAsync(conn, tx, displayName, ct);
            foreach (var key in keys)
            {
                await InsertAuthLinkAsync(conn, tx, userId, providerId.Value, key, ct);
            }

            await tx.CommitAsync(ct);
            return new CreateSelfServiceAdAccountResult(
                CreateSelfServiceAdAccountStatus.Created,
                UserId: userId);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(ct);
            return new CreateSelfServiceAdAccountResult(CreateSelfServiceAdAccountStatus.AlreadyLinkedToAnotherUser);
        }
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

    private static async Task<int?> GetAuthProviderIdAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string displayName,
        CancellationToken ct)
    {
        const string sql = @"
SELECT provider_id
FROM omp.auth_providers
WHERE display_name = @display_name
  AND is_enabled = 1;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@display_name", SqlDbType.NVarChar, 200).Value = displayName;

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task<ExistingAuthLink?> GetExistingAuthLinkAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int providerId,
        IReadOnlyList<string> providerUserKeys,
        CancellationToken ct)
    {
        var inList = string.Join(",", Enumerable.Range(0, providerUserKeys.Count).Select(i => "@key" + i));
        var sql = $@"
SELECT TOP (1)
       user_auth_id,
       user_id
FROM omp.user_auth
WHERE provider_id = @provider_id
  AND provider_user_key IN ({inList})
ORDER BY user_auth_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@provider_id", SqlDbType.Int).Value = providerId;
        for (var i = 0; i < providerUserKeys.Count; i++)
        {
            cmd.Parameters.Add("@key" + i, SqlDbType.NVarChar, ProviderUserKeyMaxLength).Value = providerUserKeys[i];
        }

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ExistingAuthLink(rdr.GetInt32(0), rdr.GetInt32(1));
    }

    private static async Task<int> InsertUserAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string displayName,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.users(display_name, account_status, last_login_at, created_at, updated_at)
VALUES(@display_name, @account_status, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());

SELECT CAST(SCOPE_IDENTITY() AS int);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@display_name", SqlDbType.NVarChar, DisplayNameMaxLength).Value = displayName;
        cmd.Parameters.Add("@account_status", SqlDbType.Int).Value = ActiveAccountStatus;

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task InsertAuthLinkAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int userId,
        int providerId,
        string providerUserKey,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.user_auth(user_id, provider_id, provider_user_key, last_used_at, created_at)
VALUES(@user_id, @provider_id, @provider_user_key, SYSUTCDATETIME(), SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@provider_id", SqlDbType.Int).Value = providerId;
        cmd.Parameters.Add("@provider_user_key", SqlDbType.NVarChar, ProviderUserKeyMaxLength).Value = providerUserKey;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private readonly record struct ExistingAuthLink(int UserAuthId, int UserId);
}

public sealed record PortalAccountSettings(string DisplayName, bool AdminMetricsCollapsed);

public sealed record PortalUserSettings(bool AdminMetricsCollapsed);

public sealed record CreateSelfServiceAdAccountResult(
    CreateSelfServiceAdAccountStatus Status,
    int? UserId = null,
    int? ExistingUserId = null);

public enum CreateSelfServiceAdAccountStatus
{
    Created,
    MissingProviderKeys,
    ProviderUnavailable,
    AlreadyLinkedToAnotherUser
}
