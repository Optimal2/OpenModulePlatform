// File: OpenModulePlatform.Auth/Services/OmpAuthRepository.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using System.Globalization;
using System.Security.Claims;

namespace OpenModulePlatform.Auth.Services;

public sealed class OmpAuthRepository
{
    private const string AdProvider = "AD";
    private const int ActiveAccountStatus = 1;
    private const int DisplayNameMaxLength = 200;
    private const int ProviderUserKeyMaxLength = 1000;
    private const int AdGroupPrincipalQueryChunkSize = 500;

    private readonly SqlConnectionFactory _db;
    private readonly LocalPasswordHasher _passwordHasher;
    private readonly WindowsPrincipalReader _windows;
    private readonly OmpConfigurationService _configuration;
    private readonly ILogger<OmpAuthRepository> _log;

    public OmpAuthRepository(
        SqlConnectionFactory db,
        LocalPasswordHasher passwordHasher,
        WindowsPrincipalReader windows,
        OmpConfigurationService configuration,
        ILogger<OmpAuthRepository> log)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _windows = windows;
        _configuration = configuration;
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
        if (linkedUser is not null && linkedUser.Value.IsActive)
        {
            await MarkUserAuthUsedAsync(conn, linkedUser.Value.UserAuthId, linkedUser.Value.UserId, ct);
        }
        else if (linkedUser is not null)
        {
            _log.LogWarning(
                "Windows identity '{UserName}' matched disabled OMP user {UserId}. AD-principal fallback is blocked.",
                userName,
                linkedUser.Value.UserId);
            return null;
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

        if (linkedUser is null &&
            await IsAutomaticExternalUserProvisioningEnabledAsync(ct) &&
            await HasNonSystemRoleAssignmentAsync(conn, principals, ct))
        {
            linkedUser = await TryAutoProvisionLinkedUserAsync(
                conn,
                provider.Value.ProviderId,
                userName,
                userKeys,
                ct);

            if (linkedUser is not null && !linkedUser.Value.IsActive)
            {
                _log.LogWarning(
                    "Windows identity '{UserName}' matched disabled OMP user {UserId} during auto-provisioning retry. AD-principal fallback is blocked.",
                    userName,
                    linkedUser.Value.UserId);
                return null;
            }
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

        var provider = await EnsureProviderAsync(conn, LocalPasswordIdentity.ProviderDisplayName, ct);
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

        if (!linkedUser.Value.IsActive)
        {
            _log.LogWarning(
                "Local password user '{UserName}' matched disabled OMP user {UserId}.",
                normalizedUserName,
                linkedUser.Value.UserId);
            return (null, "The linked OMP user is disabled.");
        }

        await MarkUserAuthUsedAsync(conn, linkedUser.Value.UserAuthId, linkedUser.Value.UserId, ct);

        return (new OmpAuthenticatedUser
        {
            UserId = linkedUser.Value.UserId,
            DisplayName = linkedUser.Value.DisplayName,
            Provider = LocalPasswordIdentity.ProviderDisplayName,
            ProviderUserKey = normalizedUserName,
            RolePrincipals =
            [
                ("OmpUser", linkedUser.Value.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("LocalUser", normalizedUserName)
            ]
        }, null);
    }

    private static string NormalizeLocalUserName(string userName)
        => LocalPasswordIdentity.NormalizeUserName(userName);

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
        => await TryResolveLinkedUserAsync(conn, tx: null, providerId, providerUserKeys, ct);

    private static async Task<LinkedUserRow?> TryResolveLinkedUserAsync(
        SqlConnection conn,
        SqlTransaction? tx,
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
       u.display_name,
       u.account_status
FROM omp.user_auth ua
INNER JOIN omp.users u ON u.user_id = ua.user_id
WHERE ua.provider_id = @provider_id
  AND ua.provider_user_key IN ({inList})
ORDER BY CASE WHEN u.account_status = 1 THEN 0 ELSE 1 END,
         ua.user_auth_id;";

        await using var cmd = tx is null
            ? new SqlCommand(sql, conn)
            : new SqlCommand(sql, conn, tx);
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

        return new LinkedUserRow(rdr.GetInt32(0), rdr.GetInt32(1), rdr.GetString(2), rdr.GetInt32(3));
    }

    private async Task<bool> IsAutomaticExternalUserProvisioningEnabledAsync(CancellationToken ct)
    {
        var mode = await _configuration.GetGlobalStringAsync(
            OmpAuthDefaults.ConfigurationCategory,
            OmpAuthDefaults.ExternalUserProvisioningModeSetting,
            ct);

        return string.Equals(
            mode?.Trim(),
            OmpAuthDefaults.ExternalUserProvisioningModeAutomaticForAuthorizedUsers,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasNonSystemRoleAssignmentAsync(
        SqlConnection conn,
        IReadOnlyList<(string PrincipalType, string Principal)> principals,
        CancellationToken ct)
    {
        var rolePrincipals = principals
            .Where(principal => IsProvisioningTriggerPrincipalType(principal.PrincipalType))
            .Where(principal => !string.IsNullOrWhiteSpace(principal.Principal))
            .Select(principal => (
                PrincipalType: principal.PrincipalType.Trim(),
                Principal: principal.Principal.Trim()))
            .Where(principal => principal.Principal.Length <= 256)
            .Distinct()
            .ToArray();

        if (rolePrincipals.Length == 0)
        {
            return false;
        }

        var values = string.Join(
            ",",
            Enumerable.Range(0, rolePrincipals.Length).Select(i => $"(@pt{i}, @p{i})"));

        var sql = $@"
WITH RequestedPrincipals(PrincipalType, Principal) AS
(
    SELECT v.PrincipalType, v.Principal
    FROM (VALUES {values}) AS v(PrincipalType, Principal)
)
SELECT TOP (1) 1
FROM omp.RolePrincipals rp
INNER JOIN omp.Roles r ON r.RoleId = rp.RoleId
INNER JOIN RequestedPrincipals requested
    ON requested.PrincipalType = rp.PrincipalType
   AND requested.Principal = rp.Principal
WHERE r.Name NOT IN (@everyoneRoleName, @authenticatedUsersRoleName)
  AND NOT EXISTS
  (
      SELECT 1
      FROM omp.RolePrincipals ambient
      WHERE ambient.RoleId = r.RoleId
        AND ambient.PrincipalType = @systemPrincipalType
        AND ambient.Principal IN (@everyonePrincipal, @authenticatedUsersPrincipal)
  );";

        await using var cmd = new SqlCommand(sql, conn);
        for (var i = 0; i < rolePrincipals.Length; i++)
        {
            cmd.Parameters.AddWithValue("@pt" + i, rolePrincipals[i].PrincipalType);
            cmd.Parameters.AddWithValue("@p" + i, rolePrincipals[i].Principal);
        }

        cmd.Parameters.AddWithValue("@everyoneRoleName", OmpRbacDefaults.EveryoneRoleName);
        cmd.Parameters.AddWithValue("@authenticatedUsersRoleName", OmpRbacDefaults.AuthenticatedUsersRoleName);
        cmd.Parameters.AddWithValue("@systemPrincipalType", OmpRbacDefaults.SystemPrincipalType);
        cmd.Parameters.AddWithValue("@everyonePrincipal", OmpRbacDefaults.EveryonePrincipal);
        cmd.Parameters.AddWithValue("@authenticatedUsersPrincipal", OmpRbacDefaults.AuthenticatedUsersPrincipal);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null and not DBNull;
    }

    private static bool IsProvisioningTriggerPrincipalType(string principalType)
        => string.Equals(principalType, "ADUser", StringComparison.OrdinalIgnoreCase)
           || string.Equals(principalType, "ADGroup", StringComparison.OrdinalIgnoreCase)
           || string.Equals(principalType, "User", StringComparison.OrdinalIgnoreCase);

    private async Task<LinkedUserRow?> TryAutoProvisionLinkedUserAsync(
        SqlConnection conn,
        int providerId,
        string userName,
        IReadOnlyList<string> providerUserKeys,
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
            return null;
        }

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var existing = await TryResolveLinkedUserAsync(conn, tx, providerId, keys, ct);
            if (existing is not null)
            {
                await tx.RollbackAsync(ct);
                if (existing.Value.IsActive)
                {
                    await MarkUserAuthUsedAsync(conn, existing.Value.UserAuthId, existing.Value.UserId, ct);
                }

                return existing;
            }

            var displayName = CreateAutoProvisionedDisplayName(userName);
            var userId = await InsertAutoProvisionedUserAsync(conn, tx, displayName, ct);
            foreach (var key in keys)
            {
                await InsertAutoProvisionedAuthLinkAsync(conn, tx, userId, providerId, key, ct);
            }

            await tx.CommitAsync(ct);
            _log.LogInformation(
                "Auto-provisioned OMP user {UserId} for Windows identity '{UserName}' with {AuthLinkCount} AD auth link(s).",
                userId,
                userName,
                keys.Length);

            return new LinkedUserRow(0, userId, displayName, ActiveAccountStatus);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(ct);

            var existing = await TryResolveLinkedUserAsync(conn, providerId, keys, ct);
            if (existing is not null && existing.Value.IsActive)
            {
                await MarkUserAuthUsedAsync(conn, existing.Value.UserAuthId, existing.Value.UserId, ct);
            }

            return existing;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static string CreateAutoProvisionedDisplayName(string userName)
    {
        var displayName = string.IsNullOrWhiteSpace(userName)
            ? "External user"
            : userName.Trim();

        return displayName.Length <= DisplayNameMaxLength
            ? displayName
            : displayName[..DisplayNameMaxLength];
    }

    private static async Task<int> InsertAutoProvisionedUserAsync(
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
        cmd.Parameters.AddWithValue("@display_name", displayName);
        cmd.Parameters.AddWithValue("@account_status", ActiveAccountStatus);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task InsertAutoProvisionedAuthLinkAsync(
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
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@provider_id", providerId);
        cmd.Parameters.AddWithValue("@provider_user_key", providerUserKey);

        await cmd.ExecuteNonQueryAsync(ct);
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
    private readonly record struct LinkedUserRow(int UserAuthId, int UserId, string DisplayName, int AccountStatus)
    {
        public bool IsActive => AccountStatus == 1;
    }
}
