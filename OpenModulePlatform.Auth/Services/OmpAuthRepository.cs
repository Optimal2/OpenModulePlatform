// File: OpenModulePlatform.Auth/Services/OmpAuthRepository.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Web.Shared.Services;
using System.Security.Claims;

namespace OpenModulePlatform.Auth.Services;

public sealed class OmpAuthRepository
{
    private const string AdProvider = "AD";
    private const string LocalPasswordProvider = "lpwd";
    private const int AdGroupPrincipalQueryChunkSize = 500;

    private readonly SqlConnectionFactory _db;
    private readonly LocalPasswordHasher _passwordHasher;
    private readonly WindowsPrincipalReader _windows;
    private readonly ILogger<OmpAuthRepository> _log;

    public OmpAuthRepository(
        SqlConnectionFactory db,
        LocalPasswordHasher passwordHasher,
        WindowsPrincipalReader windows,
        ILogger<OmpAuthRepository> log)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _windows = windows;
        _log = log;
    }

    public async Task<OmpAuthenticatedUser?> ResolveWindowsAsync(
        ClaimsPrincipal windowsPrincipal,
        CancellationToken ct)
    {
        var userName = _windows.GetUserName(windowsPrincipal);
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var provider = await EnsureProviderAsync(conn, AdProvider, ct);
        if (provider is null)
        {
            return null;
        }

        var userSid = _windows.GetUserSid(windowsPrincipal);
        var userKeys = new List<string>();
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            userKeys.Add("sid:" + userSid);
        }

        userKeys.Add("name:" + userName);
        userKeys.Add(userName);

        var linkedUser = await TryResolveLinkedUserAsync(conn, provider.Value.ProviderId, userKeys, ct);
        if (linkedUser is not null)
        {
            await MarkUserAuthUsedAsync(conn, linkedUser.Value.UserAuthId, linkedUser.Value.UserId, ct);
        }

        var principals = new List<(string PrincipalType, string Principal)>
        {
            ("User", userName),
            ("ADUser", userName)
        };

        if (!string.IsNullOrWhiteSpace(userSid))
        {
            principals.Add(("ADUser", userSid));
        }

        var windowsGroupPrincipals = _windows.GetGroupPrincipals(windowsPrincipal);
        var mappedAdGroupPrincipals = await GetMappedAdGroupPrincipalsAsync(
            conn,
            windowsGroupPrincipals,
            ct);

        _log.LogDebug(
            "Resolved {MappedCount} matching AD group role principals from {TotalCount} Windows group principals.",
            mappedAdGroupPrincipals.Count,
            windowsGroupPrincipals.Count);

        foreach (var group in mappedAdGroupPrincipals)
        {
            principals.Add(("ADGroup", group));
        }

        return new OmpAuthenticatedUser
        {
            UserId = linkedUser?.UserId,
            DisplayName = linkedUser?.DisplayName ?? userName,
            Provider = AdProvider,
            ProviderUserKey = userKeys[0],
            RolePrincipals = principals
        };
    }

    public async Task<(OmpAuthenticatedUser? User, string? Error)> ResolveLocalPasswordAsync(
        string userName,
        string password,
        CancellationToken ct)
    {
        var normalizedUserName = NormalizeLocalUserName(userName);
        if (string.IsNullOrWhiteSpace(normalizedUserName))
        {
            return (null, "Enter a user name.");
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var provider = await EnsureProviderAsync(conn, LocalPasswordProvider, ct);
        if (provider is null)
        {
            return (null, "Local password sign-in is disabled.");
        }

        var storedHash = await GetLocalPasswordHashAsync(conn, normalizedUserName, ct);
        if (string.IsNullOrWhiteSpace(storedHash) ||
            !_passwordHasher.Verify(password, storedHash))
        {
            return (null, "The user name or password is incorrect.");
        }

        var linkedUser = await TryResolveLinkedUserAsync(
            conn,
            provider.Value.ProviderId,
            [normalizedUserName, "name:" + normalizedUserName],
            ct);

        if (linkedUser is null)
        {
            _log.LogWarning(
                "Local password user '{UserName}' authenticated but has no omp.user_auth link.",
                normalizedUserName);
            return (null, "The local password account is not linked to an OMP user.");
        }

        await MarkUserAuthUsedAsync(conn, linkedUser.Value.UserAuthId, linkedUser.Value.UserId, ct);

        return (new OmpAuthenticatedUser
        {
            UserId = linkedUser.Value.UserId,
            DisplayName = linkedUser.Value.DisplayName,
            Provider = LocalPasswordProvider,
            ProviderUserKey = normalizedUserName,
            RolePrincipals =
            [
                ("OmpUser", linkedUser.Value.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("LocalUser", normalizedUserName)
            ]
        }, null);
    }

    private static string NormalizeLocalUserName(string userName)
        => userName.Trim().ToLowerInvariant();

    private static async Task<ProviderRow?> EnsureProviderAsync(
        SqlConnection conn,
        string displayName,
        CancellationToken ct)
    {
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM omp.auth_providers WHERE display_name = @display_name)
BEGIN
    INSERT INTO omp.auth_providers(display_name, is_enabled)
    VALUES(@display_name, 1);
END

SELECT provider_id,
       is_enabled
FROM omp.auth_providers
WHERE display_name = @display_name;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@display_name", displayName);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        var row = new ProviderRow(rdr.GetInt32(0), rdr.GetBoolean(1));
        return row.IsEnabled ? row : null;
    }

    private static async Task<LinkedUserRow?> TryResolveLinkedUserAsync(
        SqlConnection conn,
        int providerId,
        IReadOnlyList<string> providerUserKeys,
        CancellationToken ct)
    {
        var keys = providerUserKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (keys.Length == 0)
        {
            return null;
        }

        var inList = string.Join(",", Enumerable.Range(0, keys.Length).Select(i => "@key" + i));
        var sql = $@"
SELECT TOP (1)
       ua.user_auth_id,
       u.user_id,
       u.display_name
FROM omp.user_auth ua
INNER JOIN omp.users u ON u.user_id = ua.user_id
WHERE ua.provider_id = @provider_id
  AND ua.provider_user_key IN ({inList})
  AND u.account_status = 1
ORDER BY ua.user_auth_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@provider_id", providerId);
        for (var i = 0; i < keys.Length; i++)
        {
            cmd.Parameters.AddWithValue("@key" + i, keys[i]);
        }

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new LinkedUserRow(rdr.GetInt32(0), rdr.GetInt32(1), rdr.GetString(2));
    }

    private static async Task<string?> GetLocalPasswordHashAsync(
        SqlConnection conn,
        string userName,
        CancellationToken ct)
    {
        const string sql = @"
SELECT password_hash
FROM omp.auth_provider_lpwd
WHERE user_name = @user_name;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_name", userName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    private static async Task<IReadOnlyList<string>> GetMappedAdGroupPrincipalsAsync(
        SqlConnection conn,
        IReadOnlyCollection<string> groupPrincipals,
        CancellationToken ct)
    {
        var groups = groupPrincipals
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (groups.Length == 0)
        {
            return [];
        }

        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in groups.Chunk(AdGroupPrincipalQueryChunkSize))
        {
            var values = string.Join(
                ",",
                Enumerable.Range(0, chunk.Length).Select(i => $"(@group{i})"));

            var sql = $@"
WITH CandidateGroups(Principal) AS
(
    SELECT v.Principal
    FROM (VALUES {values}) AS v(Principal)
)
SELECT DISTINCT rp.Principal
FROM omp.RolePrincipals rp
INNER JOIN CandidateGroups candidate
    ON candidate.Principal = rp.Principal
WHERE rp.PrincipalType = N'ADGroup';";

            await using var cmd = new SqlCommand(sql, conn);
            for (var i = 0; i < chunk.Length; i++)
            {
                cmd.Parameters.AddWithValue("@group" + i, chunk[i]);
            }

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var principal = rdr.GetString(0);
                if (!string.IsNullOrWhiteSpace(principal))
                {
                    mapped.Add(principal);
                }
            }
        }

        return mapped.ToList();
    }

    private static async Task MarkUserAuthUsedAsync(
        SqlConnection conn,
        int userAuthId,
        int userId,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.user_auth
SET last_used_at = SYSUTCDATETIME()
WHERE user_auth_id = @user_auth_id;

UPDATE omp.users
SET last_login_at = SYSUTCDATETIME(),
    updated_at = SYSUTCDATETIME()
WHERE user_id = @user_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_auth_id", userAuthId);
        cmd.Parameters.AddWithValue("@user_id", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private readonly record struct ProviderRow(int ProviderId, bool IsEnabled);
    private readonly record struct LinkedUserRow(int UserAuthId, int UserId, string DisplayName);
}
