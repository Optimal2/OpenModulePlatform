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
    private const int ActiveAccountStatus = 1;
    private const int DisplayNameMaxLength = 200;
    private const int ProviderUserKeyMaxLength = 1000;
    // SQL Server allows 2100 parameters per command; 500 keeps AD group lookups comfortably below that limit.
    private const int AdGroupPrincipalQueryChunkSize = 500;
    // R7-F15: structurally valid PBKDF2-SHA256 hash (zero salt and zero
    // expected hash) verified in place of a missing account, so an unknown
    // user name costs the same hashing work as a wrong password. The
    // iteration count must track the LocalPasswordHasher.Hash default;
    // UnknownUserDummyHashTests fails the build if the two diverge.
    private const string UnknownUserDummyPasswordHash =
        "PBKDF2-SHA256$210000$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private enum ExternalUserProvisioningMode
    {
        Manual,
        AutoIfRole,
        AutoIfAuthenticated
    }

    private readonly SqlConnectionFactory _db;
    private readonly IOmpLocalPasswordHasher _passwordHasher;
    private readonly WindowsPrincipalReader _windows;
    private readonly OmpConfigurationService _configuration;
    private readonly ILogger<OmpAuthRepository> _log;

    public OmpAuthRepository(
        SqlConnectionFactory db,
        IOmpLocalPasswordHasher passwordHasher,
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

        var provider = await EnsureProviderAsync(conn, OmpAuthDefaults.AdProviderDisplayName, ct);
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
        else if (await TryResolveDisabledLinkedUserAsync(conn, provider.Value.ProviderId, userKeys, ct) is { } disabledUser)
        {
            _log.LogWarning(
                "Windows identity '{UserName}' matched disabled OMP user {UserId}. AD-principal fallback is blocked.",
                userName,
                disabledUser.UserId);
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
            await ShouldAutoProvisionExternalUserAsync(conn, principals, ct))
        {
            linkedUser = await TryAutoProvisionLinkedUserAsync(
                conn,
                provider.Value.ProviderId,
                userName,
                userKeys,
                "Windows identity",
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

        if (linkedUser is not null)
        {
            principals.Add(("OmpUser", linkedUser.Value.UserId.ToString(CultureInfo.InvariantCulture)));
        }

        return new OmpAuthenticatedUser
        {
            UserId = linkedUser?.UserId,
            ProviderId = provider.Value.ProviderId,
            DisplayName = linkedUser?.DisplayName ?? userName,
            Provider = OmpAuthDefaults.AdProviderDisplayName,
            ProviderUserKey = userKeys[0],
            SecurityStamp = linkedUser?.SecurityStamp,
            RolePrincipals = principals
        };
    }

    public async Task<OmpAuthenticatedUser?> ResolveOidcAsync(
        OmpOidcResolvedClaims oidcClaims,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(oidcClaims.ProviderUserKey) ||
            string.IsNullOrWhiteSpace(oidcClaims.UserName))
        {
            return null;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var providerName = NormalizeOidcProviderName(oidcClaims.ProviderName);
        var provider = await EnsureProviderAsync(conn, providerName, ct);
        if (provider is null)
        {
            return null;
        }

        var userKeys = BuildOidcProviderUserKeys(oidcClaims);
        if (userKeys.Count == 0)
        {
            _log.LogWarning("OIDC identity did not contain a provider user key that fits OMP storage limits.");
            return null;
        }

        var linkedUser = await TryResolveLinkedUserAsync(conn, provider.Value.ProviderId, userKeys, ct);
        if (linkedUser is not null)
        {
            await MarkUserAuthUsedAsync(conn, linkedUser.Value.UserAuthId, linkedUser.Value.UserId, ct);
        }
        else if (await TryResolveDisabledLinkedUserAsync(conn, provider.Value.ProviderId, userKeys, ct) is { } disabledUser)
        {
            _log.LogWarning(
                "OIDC identity with provider user key hash {ProviderUserKeyHash} matched disabled OMP user {UserId}. Principal fallback is blocked.",
                CreateLogHash(userKeys[0]),
                disabledUser.UserId);
            return null;
        }

        var principals = BuildOidcRolePrincipals(oidcClaims);
        var suppressAutoProvisioning = false;

        if (linkedUser is null &&
            string.Equals(providerName, OmpAuthDefaults.AdfsProviderDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            var adResolution = await TryResolveAdLinkedUserForOidcAsync(conn, oidcClaims, ct);
            switch (adResolution.Status)
            {
                case OmpAdLinkedUserResolutionStatus.UniqueActive:
                    linkedUser = await TryLinkOidcProviderToExistingUserAsync(
                        conn,
                        provider.Value.ProviderId,
                        adResolution.User!.Value,
                        userKeys,
                        providerName,
                        ct);
                    if (linkedUser is not null && !linkedUser.Value.IsActive)
                    {
                        _log.LogWarning(
                            "ADFS identity with provider user key hash {ProviderUserKeyHash} matched disabled OMP user {UserId} during AD cross-link retry. Principal fallback is blocked.",
                            CreateLogHash(userKeys[0]),
                            linkedUser.Value.UserId);
                        return null;
                    }

                    break;
                case OmpAdLinkedUserResolutionStatus.Disabled:
                    _log.LogWarning(
                        "ADFS identity with provider user key hash {ProviderUserKeyHash} matched disabled AD-linked OMP user {UserId}. Principal fallback is blocked.",
                        CreateLogHash(userKeys[0]),
                        adResolution.User?.UserId);
                    return null;
                case OmpAdLinkedUserResolutionStatus.AmbiguousActive:
                    suppressAutoProvisioning = true;
                    _log.LogWarning(
                        "ADFS identity with provider user key hash {ProviderUserKeyHash} matched {ActiveUserCount} active AD-linked OMP users across {MatchedUserCount} total matched users. Automatic cross-linking and auto-provisioning were skipped.",
                        CreateLogHash(userKeys[0]),
                        adResolution.ActiveUserCount,
                        adResolution.MatchedUserCount);
                    break;
            }
        }

        if (linkedUser is null &&
            !suppressAutoProvisioning &&
            await ShouldAutoProvisionExternalUserAsync(conn, principals, ct))
        {
            linkedUser = await TryAutoProvisionLinkedUserAsync(
                conn,
                provider.Value.ProviderId,
                oidcClaims.DisplayName,
                userKeys,
                "OIDC identity",
                ct);

            if (linkedUser is not null && !linkedUser.Value.IsActive)
            {
                _log.LogWarning(
                    "OIDC identity with provider user key hash {ProviderUserKeyHash} matched disabled OMP user {UserId} during auto-provisioning retry. Principal fallback is blocked.",
                    CreateLogHash(userKeys[0]),
                    linkedUser.Value.UserId);
                return null;
            }
        }

        if (linkedUser is not null)
        {
            principals.Add(("OmpUser", linkedUser.Value.UserId.ToString(CultureInfo.InvariantCulture)));
        }

        return new OmpAuthenticatedUser
        {
            UserId = linkedUser?.UserId,
            ProviderId = provider.Value.ProviderId,
            DisplayName = linkedUser?.DisplayName ?? oidcClaims.DisplayName,
            Provider = providerName,
            ProviderUserKey = userKeys[0],
            SecurityStamp = linkedUser?.SecurityStamp,
            RolePrincipals = principals
        };
    }

    public async Task<(OmpAuthenticatedUser? User, string? Error, bool IsInfrastructureError)> ResolveLocalPasswordAsync(
        string userName,
        string password,
        CancellationToken ct)
    {
        var normalizedUserName = NormalizeLocalUserName(userName);
        if (string.IsNullOrWhiteSpace(normalizedUserName))
        {
            return (null, "Enter a user name.", false);
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var provider = await EnsureProviderAsync(conn, LocalPasswordIdentity.ProviderDisplayName, ct);
        if (provider is null)
        {
            // R7-F16: the provider being disabled or missing is an
            // infrastructure/configuration condition -- no credential was ever
            // compared -- so the caller must not count it toward the lockout
            // budget that is meant to bound password guessing (R5-F8).
            return (null, "Local password sign-in is disabled.", true);
        }

        var storedHash = await GetLocalPasswordHashAsync(conn, normalizedUserName, ct);

        // R7-F15: verify against a dummy hash when the account does not exist,
        // so an unknown user name costs the same PBKDF2 work as a wrong
        // password and the response time cannot reveal which user names are
        // registered.
        var accountExists = !string.IsNullOrWhiteSpace(storedHash);
        if (!_passwordHasher.Verify(password, accountExists ? storedHash : UnknownUserDummyPasswordHash) ||
            !accountExists)
        {
            return (null, "The user name or password is incorrect.", false);
        }

        var linkedUser = await TryResolveLinkedUserAsync(
            conn,
            provider.Value.ProviderId,
            [normalizedUserName, "name:" + normalizedUserName],
            ct);

        if (linkedUser is null)
        {
            if (await TryResolveDisabledLinkedUserAsync(
                    conn,
                    provider.Value.ProviderId,
                    [normalizedUserName, "name:" + normalizedUserName],
                    ct) is { } disabledUser)
            {
                _log.LogWarning(
                    "Local password user '{UserName}' matched disabled OMP user {UserId}.",
                    normalizedUserName,
                    disabledUser.UserId);
                return (null, "The linked OMP user is disabled.", false);
            }

            _log.LogWarning(
                "Local password user '{UserName}' authenticated but has no omp.user_auth link.",
                normalizedUserName);
            return (null, "The local password account is not linked to an OMP user.", false);
        }

        await MarkUserAuthUsedAsync(conn, linkedUser.Value.UserAuthId, linkedUser.Value.UserId, ct);

        return (new OmpAuthenticatedUser
        {
            UserId = linkedUser.Value.UserId,
            ProviderId = provider.Value.ProviderId,
            DisplayName = linkedUser.Value.DisplayName,
            Provider = LocalPasswordIdentity.ProviderDisplayName,
            ProviderUserKey = normalizedUserName,
            SecurityStamp = linkedUser.Value.SecurityStamp,
            RolePrincipals =
            [
                ("OmpUser", linkedUser.Value.UserId.ToString(CultureInfo.InvariantCulture)),
                ("LocalUser", normalizedUserName)
            ]
        }, null, false);
    }

    public async Task<(OmpAuthenticatedUser? User, string? Error, bool IsInfrastructureError)> CreateLocalPasswordUserAsync(
        string userName,
        string password,
        CancellationToken ct)
    {
        var displayName = CreateLocalDisplayName(userName);
        var normalizedUserName = NormalizeLocalUserName(userName);
        if (string.IsNullOrWhiteSpace(normalizedUserName))
        {
            return (null, "Enter a user name.", false);
        }

        if (normalizedUserName.Length > 256)
        {
            return (null, "User name must be 256 characters or fewer.", false);
        }

        if (string.IsNullOrEmpty(password))
        {
            return (null, "Password is required.", false);
        }

        if (password.Length < 8)
        {
            return (null, "Password must be at least 8 characters.", false);
        }

        if (!await IsSelfRegistrationEnabledAsync(ct))
        {
            return (null, "Account registration is disabled.", false);
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var provider = await EnsureProviderAsync(conn, LocalPasswordIdentity.ProviderDisplayName, ct);
        if (provider is null)
        {
            // R7-F16: same rule as sign-in -- a disabled/missing provider is an
            // infrastructure condition, not a failed attempt by the caller.
            return (null, "Local password sign-in is disabled.", true);
        }

        var passwordHash = _passwordHasher.Hash(password);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            if (await LocalPasswordUserExistsAsync(conn, tx, normalizedUserName, ct) ||
                (await TryResolveLinkedUserAsync(conn, tx, provider.Value.ProviderId, [normalizedUserName, "name:" + normalizedUserName], ct)) is not null ||
                (await TryResolveDisabledLinkedUserAsync(conn, tx, provider.Value.ProviderId, [normalizedUserName, "name:" + normalizedUserName], ct)) is not null)
            {
                await tx.RollbackAsync(ct);
                return (null, "User name is already in use.", false);
            }

            var createdUser = await InsertActiveUserWithLastLoginAsync(conn, tx, displayName, ct);
            await InsertLocalPasswordUserAsync(conn, tx, normalizedUserName, passwordHash, ct);
            await InsertAuthLinkAsync(conn, tx, createdUser.UserId, provider.Value.ProviderId, normalizedUserName, ct);

            await tx.CommitAsync(ct);

            return (new OmpAuthenticatedUser
            {
                UserId = createdUser.UserId,
                ProviderId = provider.Value.ProviderId,
                DisplayName = displayName,
                Provider = LocalPasswordIdentity.ProviderDisplayName,
                ProviderUserKey = normalizedUserName,
                SecurityStamp = createdUser.SecurityStamp,
                RolePrincipals =
                [
                    ("OmpUser", createdUser.UserId.ToString(CultureInfo.InvariantCulture)),
                    ("LocalUser", normalizedUserName)
                ]
            }, null, false);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(ct);
            return (null, "User name is already in use.", false);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
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
        => await ResolveLinkedUserByAccountStatusAsync(conn, tx, providerId, providerUserKeys, activeAccountsOnly: true, ct);

    /// <summary>
    /// Finds an enabled auth link whose OMP user is disabled. The active-only
    /// resolution in <see cref="TryResolveLinkedUserAsync"/> never returns
    /// these rows (R7-F11); callers use this lookup to keep blocking sign-in
    /// and provisioning fallbacks for accounts that were deliberately
    /// disabled.
    /// </summary>
    private static async Task<LinkedUserRow?> TryResolveDisabledLinkedUserAsync(
        SqlConnection conn,
        int providerId,
        IReadOnlyList<string> providerUserKeys,
        CancellationToken ct)
        => await TryResolveDisabledLinkedUserAsync(conn, tx: null, providerId, providerUserKeys, ct);

    private static async Task<LinkedUserRow?> TryResolveDisabledLinkedUserAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        int providerId,
        IReadOnlyList<string> providerUserKeys,
        CancellationToken ct)
        => await ResolveLinkedUserByAccountStatusAsync(conn, tx, providerId, providerUserKeys, activeAccountsOnly: false, ct);

    private static async Task<LinkedUserRow?> ResolveLinkedUserByAccountStatusAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        int providerId,
        IReadOnlyList<string> providerUserKeys,
        bool activeAccountsOnly,
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
        // R7-F11: the disabled-account guard lives in the selection, not in the
        // sort order. Only accounts with the requested status are eligible, and
        // user_auth_id is unique, so ORDER BY ua.user_auth_id makes the
        // TOP (1) choice deterministic.
        var accountStatusPredicate = activeAccountsOnly
            ? "AND u.account_status = @active_account_status"
            : "AND u.account_status <> @active_account_status";
        var sql = $@"
SELECT TOP (1)
       ua.user_auth_id,
       u.user_id,
       u.display_name,
       u.account_status,
       u.security_stamp
FROM omp.user_auth ua
INNER JOIN omp.users u ON u.user_id = ua.user_id
WHERE ua.provider_id = @provider_id
  AND ua.provider_user_key IN ({inList})
  AND ua.auth_status = N'enabled'
  {accountStatusPredicate}
ORDER BY ua.user_auth_id;";

        await using var cmd = tx is null
            ? new SqlCommand(sql, conn)
            : new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@provider_id", providerId);
        cmd.Parameters.AddWithValue("@active_account_status", ActiveAccountStatus);
        for (var i = 0; i < keys.Length; i++)
        {
            cmd.Parameters.AddWithValue("@key" + i, keys[i]);
        }

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new LinkedUserRow(rdr.GetInt32(0), rdr.GetInt32(1), rdr.GetString(2), rdr.GetInt32(3), rdr.GetGuid(4));
    }

    private async Task<OmpAdLinkedUserResolution> TryResolveAdLinkedUserForOidcAsync(
        SqlConnection conn,
        OmpOidcResolvedClaims oidcClaims,
        CancellationToken ct)
    {
        var adKeys = OmpAdfsAdAccountLinker.BuildAdProviderLookupKeys(oidcClaims);
        if (adKeys.Count == 0)
        {
            return new OmpAdLinkedUserResolution(
                OmpAdLinkedUserResolutionStatus.NoMatch,
                User: null,
                MatchedUserCount: 0,
                ActiveUserCount: 0);
        }

        var adProvider = await EnsureProviderAsync(conn, OmpAuthDefaults.AdProviderDisplayName, ct);
        if (adProvider is null)
        {
            return new OmpAdLinkedUserResolution(
                OmpAdLinkedUserResolutionStatus.NoMatch,
                User: null,
                MatchedUserCount: 0,
                ActiveUserCount: 0);
        }

        var matches = await FindAdLinkedUserMatchesAsync(conn, adProvider.Value.ProviderId, adKeys, ct);
        return OmpAdfsAdAccountLinker.Resolve(matches);
    }

    private static async Task<IReadOnlyList<OmpAdLinkedUserCandidate>> FindAdLinkedUserMatchesAsync(
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
            return [];
        }

        var inList = string.Join(",", Enumerable.Range(0, keys.Length).Select(i => "@key" + i));
        var sql = $@"
SELECT ua.user_auth_id,
       u.user_id,
       u.display_name,
       u.account_status,
       u.security_stamp
FROM omp.user_auth ua
INNER JOIN omp.users u ON u.user_id = ua.user_id
WHERE ua.provider_id = @provider_id
  AND ua.provider_user_key IN ({inList})
  AND ua.auth_status = N'enabled'
ORDER BY ua.user_auth_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@provider_id", providerId);
        for (var i = 0; i < keys.Length; i++)
        {
            cmd.Parameters.AddWithValue("@key" + i, keys[i]);
        }

        var matches = new List<OmpAdLinkedUserCandidate>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            matches.Add(new OmpAdLinkedUserCandidate(
                rdr.GetInt32(0),
                rdr.GetInt32(1),
                rdr.GetString(2),
                rdr.GetInt32(3),
                rdr.GetGuid(4)));
        }

        return matches;
    }

    private async Task<LinkedUserRow?> TryLinkOidcProviderToExistingUserAsync(
        SqlConnection conn,
        int providerId,
        OmpAdLinkedUserCandidate adLinkedUser,
        IReadOnlyList<string> providerUserKeys,
        string providerName,
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
                await MarkUserAuthUsedAsync(conn, existing.Value.UserAuthId, existing.Value.UserId, ct);
                return existing;
            }

            var disabledExisting = await TryResolveDisabledLinkedUserAsync(conn, tx, providerId, keys, ct);
            if (disabledExisting is not null)
            {
                await tx.RollbackAsync(ct);
                return disabledExisting;
            }

            var primaryUserAuthId = 0;
            foreach (var key in keys)
            {
                var userAuthId = await InsertAuthLinkAsync(conn, tx, adLinkedUser.UserId, providerId, key, ct);
                if (primaryUserAuthId == 0)
                {
                    primaryUserAuthId = userAuthId;
                }
            }

            await tx.CommitAsync(ct);
            await MarkUserAuthUsedAsync(conn, primaryUserAuthId, adLinkedUser.UserId, ct);

            _log.LogInformation(
                "Linked first {ProviderName} sign-in to existing AD-linked OMP user {UserId} with {AuthLinkCount} auth link(s).",
                providerName,
                adLinkedUser.UserId,
                keys.Length);

            return new LinkedUserRow(
                primaryUserAuthId,
                adLinkedUser.UserId,
                adLinkedUser.DisplayName,
                adLinkedUser.AccountStatus,
                adLinkedUser.SecurityStamp);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(ct);

            var existing = await TryResolveLinkedUserAsync(conn, providerId, keys, ct);
            if (existing is not null)
            {
                await MarkUserAuthUsedAsync(conn, existing.Value.UserAuthId, existing.Value.UserId, ct);
                return existing;
            }

            return await TryResolveDisabledLinkedUserAsync(conn, providerId, keys, ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<ExternalUserProvisioningMode> GetExternalUserProvisioningModeAsync(CancellationToken ct)
    {
        var mode = await _configuration.GetGlobalStringAsync(
            OmpAuthDefaults.ConfigurationCategory,
            OmpAuthDefaults.ExternalUserProvisioningModeSetting,
            ct);

        var normalized = mode?.Trim();
        if (string.Equals(normalized, OmpAuthDefaults.ExternalUserProvisioningModeAutoIfRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, OmpAuthDefaults.ExternalUserProvisioningModeIfRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, OmpAuthDefaults.ExternalUserProvisioningModeAutomaticForAuthorizedUsers, StringComparison.OrdinalIgnoreCase))
        {
            return ExternalUserProvisioningMode.AutoIfRole;
        }

        if (string.Equals(normalized, OmpAuthDefaults.ExternalUserProvisioningModeAutoIfAuthenticated, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, OmpAuthDefaults.ExternalUserProvisioningModeIfAuthenticated, StringComparison.OrdinalIgnoreCase))
        {
            return ExternalUserProvisioningMode.AutoIfAuthenticated;
        }

        return ExternalUserProvisioningMode.Manual;
    }

    private async Task<bool> ShouldAutoProvisionExternalUserAsync(
        SqlConnection conn,
        IReadOnlyList<(string PrincipalType, string Principal)> principals,
        CancellationToken ct)
    {
        return await GetExternalUserProvisioningModeAsync(ct) switch
        {
            ExternalUserProvisioningMode.AutoIfRole => await HasNonSystemRoleAssignmentAsync(conn, principals, ct),
            ExternalUserProvisioningMode.AutoIfAuthenticated => await IsAuthenticatedUsersProvisioningTriggerAllowedAsync(principals, ct),
            _ => false
        };
    }

    private async Task<bool> IsAuthenticatedUsersProvisioningTriggerAllowedAsync(
        IReadOnlyList<(string PrincipalType, string Principal)> principals,
        CancellationToken ct)
    {
        var read = await _configuration.ReadGlobalStringAsync(
            OmpRbacDefaults.ConfigurationCategory,
            OmpRbacDefaults.AuthenticatedUsersWindowsDomainsSetting,
            ct);

        // R4-E1, same reasoning as RbacService: an absent value legitimately means "no
        // restriction configured", a failed read means nothing at all. This gate decides
        // whether an unknown principal gets provisioned on first sight, so reading a
        // database failure as "no restriction" is the one interpretation that must not
        // happen. Fail closed; the next request retries because nothing is cached.
        if (read.Failed)
        {
            _log.LogError(
                "The AuthenticatedUsers domain allowlist could not be read; refusing provisioning for this sign-in rather than assuming no restriction is configured.");
            return false;
        }

        var allowedDomains = SplitDomainList(read.Value);
        if (allowedDomains.Count == 0 || allowedDomains.Contains("*"))
        {
            return true;
        }

        return GetWindowsAccountDomains(principals).Any(allowedDomains.Contains);
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

    private static HashSet<string> SplitDomainList(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = (value ?? string.Empty).Split(
            [',', ';', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts.Where(part => !string.IsNullOrWhiteSpace(part)))
        {
            result.Add(part);
        }

        return result;
    }

    private static IEnumerable<string> GetWindowsAccountDomains(
        IReadOnlyList<(string PrincipalType, string Principal)> principals)
    {
        foreach (var principal in principals)
        {
            if (!string.Equals(principal.PrincipalType, "User", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(principal.PrincipalType, "ADUser", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slashIndex = principal.Principal.IndexOf('\\', StringComparison.Ordinal);
            if (slashIndex > 0)
            {
                yield return principal.Principal[..slashIndex];
            }
        }
    }

    private async Task<LinkedUserRow?> TryAutoProvisionLinkedUserAsync(
        SqlConnection conn,
        int providerId,
        string userName,
        IReadOnlyList<string> providerUserKeys,
        string providerLogLabel,
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
                await MarkUserAuthUsedAsync(conn, existing.Value.UserAuthId, existing.Value.UserId, ct);
                return existing;
            }

            var disabledExisting = await TryResolveDisabledLinkedUserAsync(conn, tx, providerId, keys, ct);
            if (disabledExisting is not null)
            {
                await tx.RollbackAsync(ct);
                return disabledExisting;
            }

            var displayName = CreateAutoProvisionedDisplayName(userName);
            var createdUser = await InsertActiveUserWithLastLoginAsync(conn, tx, displayName, ct);
            foreach (var key in keys)
            {
                await InsertAuthLinkAsync(conn, tx, createdUser.UserId, providerId, key, ct);
            }

            await tx.CommitAsync(ct);
            _log.LogInformation(
                "Auto-provisioned OMP user {UserId} for {ProviderLogLabel} '{UserName}' with {AuthLinkCount} auth link(s).",
                createdUser.UserId,
                providerLogLabel,
                userName,
                keys.Length);

            return new LinkedUserRow(0, createdUser.UserId, displayName, ActiveAccountStatus, createdUser.SecurityStamp);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(ct);

            var existing = await TryResolveLinkedUserAsync(conn, providerId, keys, ct);
            if (existing is not null)
            {
                await MarkUserAuthUsedAsync(conn, existing.Value.UserAuthId, existing.Value.UserId, ct);
                return existing;
            }

            return await TryResolveDisabledLinkedUserAsync(conn, providerId, keys, ct);
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

    private static IReadOnlyList<string> BuildOidcProviderUserKeys(OmpOidcResolvedClaims oidcClaims)
    {
        var keys = oidcClaims.ProviderUserKeyCandidates.Count > 0
            ? oidcClaims.ProviderUserKeyCandidates
            : new[]
            {
                oidcClaims.ProviderUserKey,
                string.IsNullOrWhiteSpace(oidcClaims.Subject) ? "" : "sub:" + oidcClaims.Subject,
                string.IsNullOrWhiteSpace(oidcClaims.UserName) ? "" : "name:" + oidcClaims.UserName,
                oidcClaims.UserName
            };

        return keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Where(key => key.Length <= ProviderUserKeyMaxLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static List<(string PrincipalType, string Principal)> BuildOidcRolePrincipals(
        OmpOidcResolvedClaims oidcClaims)
    {
        var principals = new List<(string PrincipalType, string Principal)>();

        AddPrincipal(principals, "User", oidcClaims.UserName);
        AddPrincipal(principals, "ADUser", oidcClaims.UserName);
        foreach (var candidate in oidcClaims.UserPrincipalCandidates)
        {
            AddPrincipal(principals, "ADUser", candidate);
        }

        AddPrincipal(principals, "OIDCUser", oidcClaims.ProviderUserKey);
        AddPrincipal(principals, "OIDCSubject", oidcClaims.Subject);

        foreach (var group in oidcClaims.Groups)
        {
            AddPrincipal(principals, "ADGroup", group);
        }

        return principals
            .Distinct()
            .ToList();
    }

    private static string NormalizeOidcProviderName(string? providerName)
        => string.IsNullOrWhiteSpace(providerName)
            ? OmpAuthDefaults.OidcProviderDisplayName
            : providerName.Trim();

    private static void AddPrincipal(
        List<(string PrincipalType, string Principal)> principals,
        string principalType,
        string? principal)
    {
        if (!string.IsNullOrWhiteSpace(principal) &&
            principal.Length <= 256)
        {
            principals.Add((principalType, principal.Trim()));
        }
    }

    private static string CreateLogHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    private static async Task<CreatedUserRow> InsertActiveUserWithLastLoginAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string displayName,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.users(display_name, account_status, last_login_at, created_at, updated_at)
OUTPUT inserted.user_id, inserted.security_stamp
VALUES(@display_name, @account_status, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@display_name", displayName);
        cmd.Parameters.AddWithValue("@account_status", ActiveAccountStatus);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            throw new InvalidOperationException("Inserting the OMP user returned no row.");
        }

        return new CreatedUserRow(rdr.GetInt32(0), rdr.GetGuid(1));
    }

    private static async Task<int> InsertAuthLinkAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int userId,
        int providerId,
        string providerUserKey,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.user_auth(user_id, provider_id, provider_user_key, last_used_at, auth_status, created_at)
VALUES(@user_id, @provider_id, @provider_user_key, SYSUTCDATETIME(), N'enabled', SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS int);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@provider_id", providerId);
        cmd.Parameters.AddWithValue("@provider_user_key", providerUserKey);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> LocalPasswordUserExistsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string userName,
        CancellationToken ct)
    {
        // R7-F12: the caller passes the canonical (NormalizeUserName) form and
        // the comparison is pinned to the shared binary collation, so matching
        // is an exact ordinal comparison no matter what collation the database
        // was created with.
        const string sql = @"
SELECT COUNT(1)
FROM omp.auth_provider_lpwd
WHERE user_name COLLATE " + LocalPasswordIdentity.UserNameBinaryCollation + @" = @user_name;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@user_name", userName);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
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
        cmd.Parameters.AddWithValue("@user_name", userName);
        cmd.Parameters.AddWithValue("@password_hash", passwordHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> GetLocalPasswordHashAsync(
        SqlConnection conn,
        string userName,
        CancellationToken ct)
    {
        // R7-F12: same canonical rule on both sides -- the user name was
        // normalized with LocalPasswordIdentity.NormalizeUserName and the
        // lookup is pinned to the shared binary collation, so a stored row
        // that does not already hold the canonical form can never be matched
        // by accident (including the wrong row under a case-insensitive
        // database collation).
        const string sql = @"
SELECT password_hash
FROM omp.auth_provider_lpwd
WHERE user_name COLLATE " + LocalPasswordIdentity.UserNameBinaryCollation + @" = @user_name;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_name", userName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    private static string CreateLocalDisplayName(string userName)
    {
        var displayName = string.IsNullOrWhiteSpace(userName)
            ? "Local user"
            : userName.Trim();

        return displayName.Length <= DisplayNameMaxLength
            ? displayName
            : displayName[..DisplayNameMaxLength];
    }

    private async Task<bool> IsSelfRegistrationEnabledAsync(CancellationToken ct)
    {
        var read = await _configuration.ReadGlobalStringAsync(
            OmpAuthDefaults.ConfigurationCategory,
            OmpAuthDefaults.SelfRegistrationEnabledSetting,
            ct);

        // R7-F17. Self-registration is opt-in (R3-F2): anonymous account
        // creation is an attack surface, so an absent value means disabled and
        // the feature is turned on deliberately through the configuration
        // table. A failed read fails closed for the same reason (R10-S3): a
        // database blip must not silently turn registration on for an
        // installation that had turned it off. Nothing is cached on failure,
        // so the next request retries.
        if (read.Failed)
        {
            _log.LogError(
                "The self-registration setting could not be read; treating self-registration as disabled for this request.");
            return false;
        }

        return OmpAuthDefaults.ParseEnabledConfigValue(read.Value, defaultValue: false);
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
WHERE user_auth_id = @user_auth_id
  AND auth_status = N'enabled';

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
    private readonly record struct CreatedUserRow(int UserId, Guid SecurityStamp);
    private readonly record struct LinkedUserRow(int UserAuthId, int UserId, string DisplayName, int AccountStatus, Guid SecurityStamp)
    {
        public bool IsActive => AccountStatus == 1;
    }
}
