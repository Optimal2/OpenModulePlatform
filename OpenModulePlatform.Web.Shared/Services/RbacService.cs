using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Web.Shared.Security;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Resolves the current user's OMP roles and effective permissions.
/// </summary>
/// <remarks>
/// <para>
/// The service maps the current identity to rows in <c>omp.RolePrincipals</c> and then
/// resolves a single active role for the current request. Effective permissions are taken
/// only from that active role rather than the union of all assigned roles.
/// </para>
/// <para>
/// The active role is stored in a shared cookie so the selected role follows the user
/// between the Portal and individual module web applications when they share a host.
/// </para>
/// </remarks>
public sealed class RbacService
{
    private readonly SqlConnectionFactory _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<RbacService> _log;

    public RbacService(
        SqlConnectionFactory db,
        IHttpContextAccessor httpContextAccessor,
        ILogger<RbacService> log)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _log = log;
    }

    public async Task<HashSet<string>> GetUserPermissionsAsync(
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var roleContext = await GetUserRoleContextAsync(user, ct);
        return roleContext.EffectivePermissions;
    }

    public async Task<HashSet<string>> GetPermissionsForRoleAsync(
        ClaimsPrincipal user,
        int? roleId,
        CancellationToken ct)
    {
        if (roleId is not int requestedRoleId)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var roleContext = await GetUserRoleContextAsync(user, ct);
        if (!roleContext.AvailableRoles.Any(x => x.RoleId == requestedRoleId))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (roleContext.ActiveRoleId == requestedRoleId)
        {
            return roleContext.EffectivePermissions;
        }

        try
        {
            await using var conn = _db.Create();
            await conn.OpenAsync(ct);
            return await GetRolePermissionsAsync(conn, requestedRoleId, ct);
        }
        catch (SqlException ex)
        {
            _log.LogError(ex, "Failed to load RBAC permissions for role {RoleId} from the OMP database.", requestedRoleId);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException ex)
        {
            _log.LogError(ex, "Failed to load RBAC permissions for role {RoleId} from the OMP database.", requestedRoleId);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<UserRoleContext> GetUserRoleContextAsync(
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return UserRoleContext.Empty;
        }

        var rolePrincipals = GetRolePrincipalsFromUser(user);
        if (rolePrincipals.Count == 0)
        {
            return UserRoleContext.Empty;
        }

        try
        {
            await using var conn = _db.Create();
            await conn.OpenAsync(ct);

            var roles = await GetAssignableRolesAsync(conn, rolePrincipals, ct);
            if (roles.Count == 0)
            {
                return UserRoleContext.Empty;
            }

            var activeRoleId = ResolveActiveRoleId(user, roles)
                ?? roles[0].RoleId;

            var activeRole = roles.FirstOrDefault(x => x.RoleId == activeRoleId) ?? roles[0];
            var permissions = await GetRolePermissionsAsync(conn, activeRole.RoleId, ct);

            return new UserRoleContext
            {
                AvailableRoles = roles,
                ActiveRoleId = activeRole.RoleId,
                ActiveRoleName = activeRole.Name,
                EffectivePermissions = permissions
            };
        }
        catch (SqlException ex)
        {
            _log.LogError(ex, "Failed to load RBAC role context from the OMP database.");
            return UserRoleContext.Empty;
        }
        catch (InvalidOperationException ex)
        {
            _log.LogError(ex, "Failed to load RBAC role context from the OMP database.");
            return UserRoleContext.Empty;
        }
    }

    private int? ResolveActiveRoleId(ClaimsPrincipal user, IReadOnlyList<UserRoleOption> roles)
    {
        var claimValue = user.FindFirstValue(ActiveRoleCookie.ClaimType);
        if (int.TryParse(claimValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var claimRoleId) && roles.Any(x => x.RoleId == claimRoleId))
        {
            return claimRoleId;
        }

        var cookieValue = _httpContextAccessor.HttpContext?.Request.Cookies[ActiveRoleCookie.CookieName];
        if (!int.TryParse(cookieValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cookieRoleId))
        {
            return null;
        }

        return roles.Any(x => x.RoleId == cookieRoleId)
            ? cookieRoleId
            : null;
    }

    private static async Task<List<UserRoleOption>> GetAssignableRolesAsync(
        SqlConnection conn,
        IReadOnlyList<RolePrincipalKey> rolePrincipals,
        CancellationToken ct)
    {
        if (rolePrincipals.Count == 0)
        {
            return [];
        }

        var sql = BuildAssignableRolesQuery(rolePrincipals.Count);
        await using var cmd = new SqlCommand(sql, conn);

        for (var i = 0; i < rolePrincipals.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@pt{i}", rolePrincipals[i].PrincipalType);
            cmd.Parameters.AddWithValue($"@p{i}", rolePrincipals[i].Principal);
        }

        var roles = new List<UserRoleOption>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            roles.Add(new UserRoleOption(
                rdr.GetInt32(0),
                rdr.GetString(1),
                rdr.IsDBNull(2) ? null : rdr.GetString(2)));
        }

        return roles;
    }

    private static async Task<HashSet<string>> GetRolePermissionsAsync(
        SqlConnection conn,
        int roleId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT p.Name
FROM omp.RolePermissions rp
INNER JOIN omp.Permissions p ON p.PermissionId = rp.PermissionId
WHERE rp.RoleId = @roleId;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@roleId", roleId);

        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var permission = rdr.GetString(0);
            if (!string.IsNullOrWhiteSpace(permission))
            {
                permissions.Add(permission);
            }
        }

        return permissions;
    }

    private static string BuildAssignableRolesQuery(int principalCount)
    {
        var values = string.Join(
            ",",
            Enumerable.Range(0, principalCount).Select(i => $"(@pt{i}, @p{i})"));

        return $@"
WITH RequestedPrincipals(PrincipalType, Principal) AS
(
    SELECT v.PrincipalType, v.Principal
    FROM (VALUES {values}) AS v(PrincipalType, Principal)
)
SELECT DISTINCT r.RoleId,
       r.Name,
       r.Description
FROM omp.RolePrincipals rp
INNER JOIN omp.Roles r ON r.RoleId = rp.RoleId
INNER JOIN RequestedPrincipals requested
    ON requested.PrincipalType = rp.PrincipalType
   AND requested.Principal = rp.Principal
ORDER BY r.Name, r.RoleId;";
    }

    private List<RolePrincipalKey> GetRolePrincipalsFromUser(ClaimsPrincipal user)
    {
        var result = new HashSet<RolePrincipalKey>();

        foreach (var claim in user.FindAll(OmpAuthDefaults.PrincipalClaimType))
        {
            if (TryParsePrincipalClaim(claim.Value, out var principal))
            {
                result.Add(principal);
            }
        }

        var userIdClaim = user.FindFirstValue(OmpAuthDefaults.UserIdClaimType);
        if (!string.IsNullOrWhiteSpace(userIdClaim))
        {
            result.Add(new RolePrincipalKey("OmpUser", userIdClaim.Trim()));
        }

        if (user.Identity is WindowsIdentity windowsIdentity)
        {
            var userName = user.Identity.Name;
            if (!string.IsNullOrWhiteSpace(userName))
            {
                result.Add(new RolePrincipalKey("User", userName));
                result.Add(new RolePrincipalKey("ADUser", userName));
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (var group in GetGroupPrincipalsFromUser(windowsIdentity))
                {
                    result.Add(new RolePrincipalKey("ADGroup", group));
                }
            }
        }

        return result.ToList();
    }

    private static bool TryParsePrincipalClaim(string? value, out RolePrincipalKey principal)
    {
        principal = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        principal = new RolePrincipalKey(parts[0], parts[1]);
        return true;
    }

    /// <summary>
    /// Enumerates Windows groups for the current user and stores both SID and translated name.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private List<string> GetGroupPrincipalsFromUser(WindowsIdentity windowsIdentity)
    {
        try
        {
            if (windowsIdentity.Groups is null)
            {
                return [];
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sid in windowsIdentity.Groups)
            {
                var sidValue = sid.Value;
                if (!string.IsNullOrWhiteSpace(sidValue))
                {
                    result.Add(sidValue);
                }

                try
                {
                    var nt = sid.Translate(typeof(NTAccount)) as NTAccount;
                    var ntValue = nt?.Value;
                    if (!string.IsNullOrWhiteSpace(ntValue))
                    {
                        result.Add(ntValue);
                    }
                }
                catch (IdentityNotMappedException ex)
                {
                    _log.LogDebug(
                        ex,
                        "Skipped SID to NTAccount translation for SID {SidValue}.",
                        sidValue);
                }
                catch (SystemException ex)
                {
                    _log.LogDebug(
                        ex,
                        "Skipped SID to NTAccount translation for SID {SidValue}.",
                        sidValue);
                }
            }

            return result.ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogWarning(
                ex,
                "Failed to enumerate Windows group memberships for the current user.");
            return [];
        }
        catch (SystemException ex)
        {
            _log.LogWarning(
                ex,
                "Failed to enumerate Windows group memberships for the current user.");
            return [];
        }
    }

    private readonly record struct RolePrincipalKey(string PrincipalType, string Principal);
}
