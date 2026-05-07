// File: OpenModulePlatform.Portal/Services/OmpUserAdminRepository.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Provides minimal Portal administration for OMP users and their authentication links.
/// </summary>
public sealed class OmpUserAdminRepository
{
    private const string AdProviderDisplayName = "AD";

    private readonly SqlConnectionFactory _db;
    private readonly LocalPasswordHasher _passwordHasher;

    public OmpUserAdminRepository(SqlConnectionFactory db, LocalPasswordHasher passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public async Task<IReadOnlyList<OmpUserListRow>> GetUsersAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT u.user_id,
       u.display_name,
       u.account_status,
       u.last_login_at,
       u.created_at,
       u.updated_at,
       ua.user_auth_id,
       ap.display_name AS provider_display_name,
       ua.provider_user_key
FROM omp.users u
LEFT JOIN omp.user_auth ua ON ua.user_id = u.user_id
LEFT JOIN omp.auth_providers ap ON ap.provider_id = ua.provider_id
ORDER BY u.display_name,
         u.user_id,
         ap.display_name,
         ua.provider_user_key;";

        var rows = new Dictionary<int, OmpUserListRow>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            var userId = rdr.GetInt32(0);
            if (!rows.TryGetValue(userId, out var row))
            {
                row = new OmpUserListRow
                {
                    UserId = userId,
                    DisplayName = rdr.GetString(1),
                    AccountStatus = rdr.GetInt32(2),
                    LastLoginAt = rdr.IsDBNull(3) ? null : rdr.GetDateTime(3),
                    CreatedAt = rdr.GetDateTime(4),
                    UpdatedAt = rdr.GetDateTime(5)
                };

                rows.Add(userId, row);
            }

            if (!rdr.IsDBNull(6))
            {
                row.AuthLinks.Add(new OmpUserAuthLinkSummary(
                    rdr.GetString(7),
                    rdr.GetString(8)));
            }
        }

        return rows.Values.ToArray();
    }

    public async Task<OmpUserDetail?> GetUserAsync(int userId, CancellationToken ct)
    {
        const string userSql = @"
SELECT user_id,
       display_name,
       account_status,
       last_login_at,
       created_at,
       updated_at
FROM omp.users
WHERE user_id = @user_id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        OmpUserDetail? user = null;
        await using (var cmd = new SqlCommand(userSql, conn))
        {
            Add(cmd, "@user_id", userId);

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                user = new OmpUserDetail
                {
                    UserId = rdr.GetInt32(0),
                    DisplayName = rdr.GetString(1),
                    AccountStatus = rdr.GetInt32(2),
                    LastLoginAt = rdr.IsDBNull(3) ? null : rdr.GetDateTime(3),
                    CreatedAt = rdr.GetDateTime(4),
                    UpdatedAt = rdr.GetDateTime(5)
                };
            }
        }

        if (user is null)
        {
            return null;
        }

        user.AuthLinks.AddRange(await GetAuthLinksAsync(conn, userId, ct));
        return user;
    }

    public async Task<int> CreateUserAsync(OmpUserEditData input, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.users(display_name, account_status, created_at, updated_at)
VALUES(@display_name, @account_status, SYSUTCDATETIME(), SYSUTCDATETIME());

SELECT CAST(SCOPE_IDENTITY() AS int);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@display_name", input.DisplayName);
        Add(cmd, "@account_status", input.AccountStatus);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<bool> UpdateUserAsync(OmpUserEditData input, CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.users
SET display_name = @display_name,
    account_status = @account_status,
    updated_at = SYSUTCDATETIME()
WHERE user_id = @user_id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@user_id", input.UserId);
        Add(cmd, "@display_name", input.DisplayName);
        Add(cmd, "@account_status", input.AccountStatus);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<AddAuthLinkResult> AddAdAuthLinkAsync(
        int userId,
        string providerUserKey,
        CancellationToken ct)
    {
        providerUserKey = providerUserKey.Trim();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var providerId = await GetAuthProviderIdAsync(conn, AdProviderDisplayName, ct);
        if (providerId is null)
        {
            return new AddAuthLinkResult(AddAuthLinkStatus.ProviderMissing);
        }

        var existing = await GetExistingAuthLinkAsync(conn, providerId.Value, providerUserKey, ct);
        if (existing is not null)
        {
            return existing.Value.UserId == userId
                ? new AddAuthLinkResult(AddAuthLinkStatus.AlreadyLinkedToThisUser, existing.Value.UserId)
                : new AddAuthLinkResult(AddAuthLinkStatus.AlreadyLinkedToAnotherUser, existing.Value.UserId);
        }

        const string sql = @"
INSERT INTO omp.user_auth(user_id, provider_id, provider_user_key, created_at)
VALUES(@user_id, @provider_id, @provider_user_key, SYSUTCDATETIME());";

        try
        {
            await using var cmd = new SqlCommand(sql, conn);
            Add(cmd, "@user_id", userId);
            Add(cmd, "@provider_id", providerId.Value);
            Add(cmd, "@provider_user_key", providerUserKey);
            await cmd.ExecuteNonQueryAsync(ct);
            return new AddAuthLinkResult(AddAuthLinkStatus.Added);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            existing = await GetExistingAuthLinkAsync(conn, providerId.Value, providerUserKey, ct);
            return existing is null
                ? new AddAuthLinkResult(AddAuthLinkStatus.AlreadyLinkedToAnotherUser)
                : new AddAuthLinkResult(AddAuthLinkStatus.AlreadyLinkedToAnotherUser, existing.Value.UserId);
        }
    }

    public async Task<AddLocalPasswordLoginResult> AddLocalPasswordLoginAsync(
        int userId,
        string userName,
        string password,
        CancellationToken ct)
    {
        var normalizedUserName = LocalPasswordIdentity.NormalizeUserName(userName);
        var passwordHash = _passwordHasher.Hash(password);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            if (!await UserExistsAsync(conn, tx, userId, ct))
            {
                await tx.RollbackAsync(ct);
                return new AddLocalPasswordLoginResult(AddLocalPasswordLoginStatus.UserMissing);
            }

            var providerId = await GetEnabledAuthProviderIdAsync(
                conn,
                tx,
                LocalPasswordIdentity.ProviderDisplayName,
                ct);

            if (providerId is null)
            {
                await tx.RollbackAsync(ct);
                return new AddLocalPasswordLoginResult(AddLocalPasswordLoginStatus.ProviderMissing);
            }

            if (await UserHasProviderLinkAsync(conn, tx, userId, providerId.Value, ct))
            {
                await tx.RollbackAsync(ct);
                return new AddLocalPasswordLoginResult(AddLocalPasswordLoginStatus.UserAlreadyHasLocalLogin);
            }

            if (await LocalPasswordUserExistsAsync(conn, tx, normalizedUserName, ct) ||
                await GetExistingAuthLinkAsync(conn, tx, providerId.Value, normalizedUserName, ct) is not null)
            {
                await tx.RollbackAsync(ct);
                return new AddLocalPasswordLoginResult(AddLocalPasswordLoginStatus.UserNameAlreadyInUse);
            }

            await InsertLocalPasswordUserAsync(conn, tx, normalizedUserName, passwordHash, ct);
            await InsertAuthLinkAsync(conn, tx, userId, providerId.Value, normalizedUserName, ct);
            await tx.CommitAsync(ct);

            return new AddLocalPasswordLoginResult(AddLocalPasswordLoginStatus.Added, normalizedUserName);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(ct);
            return new AddLocalPasswordLoginResult(AddLocalPasswordLoginStatus.UserNameAlreadyInUse, normalizedUserName);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task<IReadOnlyList<OmpUserAuthLinkRow>> GetAuthLinksAsync(
        SqlConnection conn,
        int userId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT ua.user_auth_id,
       ap.display_name,
       ua.provider_user_key,
       ua.created_at,
       ua.last_used_at
FROM omp.user_auth ua
INNER JOIN omp.auth_providers ap ON ap.provider_id = ua.provider_id
WHERE ua.user_id = @user_id
ORDER BY ap.display_name,
         ua.provider_user_key;";

        var rows = new List<OmpUserAuthLinkRow>();

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@user_id", userId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new OmpUserAuthLinkRow
            {
                UserAuthId = rdr.GetInt32(0),
                ProviderDisplayName = rdr.GetString(1),
                ProviderUserKey = rdr.GetString(2),
                CreatedAt = rdr.GetDateTime(3),
                LastUsedAt = rdr.IsDBNull(4) ? null : rdr.GetDateTime(4)
            });
        }

        return rows;
    }

    private static async Task<int?> GetAuthProviderIdAsync(
        SqlConnection conn,
        string displayName,
        CancellationToken ct)
    {
        const string sql = @"
SELECT provider_id
FROM omp.auth_providers
WHERE display_name = @display_name;";

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@display_name", displayName);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task<int?> GetEnabledAuthProviderIdAsync(
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
        Add(cmd, "@display_name", displayName);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task<ExistingAuthLink?> GetExistingAuthLinkAsync(
        SqlConnection conn,
        int providerId,
        string providerUserKey,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       user_auth_id,
       user_id
FROM omp.user_auth
WHERE provider_id = @provider_id
  AND provider_user_key = @provider_user_key
ORDER BY user_auth_id;";

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@provider_id", providerId);
        Add(cmd, "@provider_user_key", providerUserKey);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ExistingAuthLink(rdr.GetInt32(0), rdr.GetInt32(1));
    }

    private static async Task<ExistingAuthLink?> GetExistingAuthLinkAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int providerId,
        string providerUserKey,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1)
       user_auth_id,
       user_id
FROM omp.user_auth
WHERE provider_id = @provider_id
  AND provider_user_key = @provider_user_key
ORDER BY user_auth_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@provider_id", providerId);
        Add(cmd, "@provider_user_key", providerUserKey);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new ExistingAuthLink(rdr.GetInt32(0), rdr.GetInt32(1));
    }

    private static async Task<bool> UserExistsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int userId,
        CancellationToken ct)
    {
        const string sql = "SELECT COUNT(1) FROM omp.users WHERE user_id = @user_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@user_id", userId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<bool> UserHasProviderLinkAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int userId,
        int providerId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1)
FROM omp.user_auth
WHERE user_id = @user_id
  AND provider_id = @provider_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@user_id", userId);
        Add(cmd, "@provider_id", providerId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<bool> LocalPasswordUserExistsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string userName,
        CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1)
FROM omp.auth_provider_lpwd
WHERE user_name = @user_name;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@user_name", userName);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task InsertLocalPasswordUserAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string userName,
        string passwordHash,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.auth_provider_lpwd(user_name, password_hash)
VALUES(@user_name, @password_hash);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@user_name", userName);
        Add(cmd, "@password_hash", passwordHash);
        await cmd.ExecuteNonQueryAsync(ct);
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
INSERT INTO omp.user_auth(user_id, provider_id, provider_user_key, created_at)
VALUES(@user_id, @provider_id, @provider_user_key, SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@user_id", userId);
        Add(cmd, "@provider_id", providerId);
        Add(cmd, "@provider_user_key", providerUserKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Add(SqlCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private readonly record struct ExistingAuthLink(int UserAuthId, int UserId);
}

public sealed class OmpUserListRow
{
    public int UserId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public int AccountStatus { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<OmpUserAuthLinkSummary> AuthLinks { get; } = [];
}

public sealed class OmpUserDetail
{
    public int UserId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public int AccountStatus { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<OmpUserAuthLinkRow> AuthLinks { get; } = [];
}

public sealed record OmpUserAuthLinkSummary(string ProviderDisplayName, string ProviderUserKey);

public sealed class OmpUserAuthLinkRow
{
    public int UserAuthId { get; set; }

    public string ProviderDisplayName { get; set; } = string.Empty;

    public string ProviderUserKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }
}

public sealed class OmpUserEditData
{
    public int UserId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public int AccountStatus { get; set; }
}

public sealed record AddAuthLinkResult(AddAuthLinkStatus Status, int? ExistingUserId = null);

public enum AddAuthLinkStatus
{
    Added,
    AlreadyLinkedToThisUser,
    AlreadyLinkedToAnotherUser,
    ProviderMissing
}

public sealed record AddLocalPasswordLoginResult(AddLocalPasswordLoginStatus Status, string? NormalizedUserName = null);

public enum AddLocalPasswordLoginStatus
{
    Added,
    UserMissing,
    ProviderMissing,
    UserAlreadyHasLocalLogin,
    UserNameAlreadyInUse
}
