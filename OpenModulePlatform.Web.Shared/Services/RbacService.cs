using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
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
    private readonly ActiveRoleState _activeRoleState;
    private readonly ILogger<RbacService> _log;

    public RbacService(
        SqlConnectionFactory db,
        IHttpContextAccessor httpContextAccessor,
        ActiveRoleState activeRoleState,
        ILogger<RbacService> log)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _activeRoleState = activeRoleState;
        _log = log;
    }

    public async Task<HashSet<string>> GetUserPermissionsAsync(
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var roleContext = await GetUserRoleContextAsync(user, ct);
        return roleContext.EffectivePermissions;
    }

    public async Task<UserRoleContext> GetUserRoleContextAsync(
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return UserRoleContext.Empty;
        }

        var userName = user.Identity?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userName))
        {
            return UserRoleContext.Empty;
        }

        var groupPrincipals = OperatingSystem.IsWindows()
            ? GetGroupPrincipalsFromUser(user)
            : [];

        try
        {
            await using var conn = _db.Create();
            await conn.OpenAsync(ct);

            var roles = await GetAssignableRolesAsync(conn, userName, groupPrincipals, ct);
            if (roles.Count == 0)
            {
                return UserRoleContext.Empty;
            }

            var activeRoleId = ResolveActiveRoleIdFromCookie(roles)
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

    private int? ResolveActiveRoleIdFromCookie(IReadOnlyList<UserRoleOption> roles)
    {
        var cookieValue = _httpContextAccessor.HttpContext?.Request.Cookies[ActiveRoleCookie.CookieName];
        if (int.TryParse(cookieValue, out var cookieRoleId) && roles.Any(x => x.RoleId == cookieRoleId))
        {
            _activeRoleState.ActiveRoleId = cookieRoleId;
            return cookieRoleId;
        }

        var cachedRoleId = _activeRoleState.ActiveRoleId;
        return cachedRoleId is int roleId && roles.Any(x => x.RoleId == roleId)
            ? roleId
            : null;
    }

    private static async Task<List<UserRoleOption>> GetAssignableRolesAsync(
        SqlConnection conn,
        string userName,
        IReadOnlyList<string> groupPrincipals,
        CancellationToken ct)
    {
        var sql = BuildAssignableRolesQuery(groupPrincipals.Count);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user", userName);

        for (var i = 0; i < groupPrincipals.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@g{i}", groupPrincipals[i]);
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

    private static string BuildAssignableRolesQuery(int groupCount)
    {
        var inList = groupCount > 0
            ? string.Join(",", Enumerable.Range(0, groupCount).Select(i => $"@g{i}"))
            : string.Empty;

        var groupClause = groupCount > 0
            ? $" OR (rp.PrincipalType='ADGroup' AND rp.Principal IN ({inList}))"
            : string.Empty;

        return $@"
SELECT DISTINCT r.RoleId,
       r.Name,
       r.Description
FROM omp.RolePrincipals rp
INNER JOIN omp.Roles r ON r.RoleId = rp.RoleId
WHERE (rp.PrincipalType='User' AND rp.Principal = @user)
{groupClause}
ORDER BY r.Name, r.RoleId;";
    }

    /// <summary>
    /// Enumerates Windows groups for the current user and stores both SID and translated name.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private List<string> GetGroupPrincipalsFromUser(ClaimsPrincipal user)
    {
        try
        {
            if (user.Identity is not WindowsIdentity windowsIdentity ||
                windowsIdentity.Groups is null)
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
}
