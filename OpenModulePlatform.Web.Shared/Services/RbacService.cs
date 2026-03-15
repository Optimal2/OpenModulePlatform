// File: OpenModulePlatform.Web.Shared/Services/RbacService.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;

namespace OpenModulePlatform.Web.Shared.Services;

public sealed class RbacService
{
    private readonly SqlConnectionFactory _db;
    private readonly ILogger<RbacService> _log;

    public RbacService(SqlConnectionFactory db, ILogger<RbacService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<HashSet<string>> GetUserPermissionsAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated != true)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var userName = user.Identity?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userName))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var groupPrincipals = OperatingSystem.IsWindows()
            ? GetGroupPrincipalsFromUser(user)
            : [];

        try
        {
            await using var conn = _db.Create();
            await conn.OpenAsync(ct);

            var sql = BuildPermissionQuery(groupPrincipals.Count);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@user", userName);

            for (var i = 0; i < groupPrincipals.Count; i++)
                cmd.Parameters.AddWithValue($"@g{i}", groupPrincipals[i]);

            var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var permission = rdr.GetString(0);
                if (!string.IsNullOrWhiteSpace(permission))
                    permissions.Add(permission);
            }

            return permissions;
        }
        catch (SqlException ex)
        {
            _log.LogError(ex, "Failed to load permissions from the OMP database.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException ex)
        {
            _log.LogError(ex, "Failed to load permissions from the OMP database.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string BuildPermissionQuery(int groupCount)
    {
        var inList = groupCount > 0
            ? string.Join(",", Enumerable.Range(0, groupCount).Select(i => $"@g{i}"))
            : string.Empty;

        var groupClause = groupCount > 0
            ? $" OR (rp.PrincipalType='ADGroup' AND rp.Principal IN ({inList}))"
            : string.Empty;

        return $@"
SELECT DISTINCT p.Name
FROM omp.RolePrincipals rp
INNER JOIN omp.RolePermissions rperm ON rperm.RoleId = rp.RoleId
INNER JOIN omp.Permissions p ON p.PermissionId = rperm.PermissionId
WHERE (rp.PrincipalType='User' AND rp.Principal = @user)
{groupClause};";
    }

    [SupportedOSPlatform("windows")]
    private List<string> GetGroupPrincipalsFromUser(ClaimsPrincipal user)
    {
        try
        {
            if (user.Identity is not WindowsIdentity windowsIdentity || windowsIdentity.Groups is null)
                return [];

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sid in windowsIdentity.Groups)
            {
                var sidValue = sid.Value;
                if (!string.IsNullOrWhiteSpace(sidValue))
                    result.Add(sidValue);

                try
                {
                    var nt = sid.Translate(typeof(NTAccount)) as NTAccount;
                    var ntValue = nt?.Value;
                    if (!string.IsNullOrWhiteSpace(ntValue))
                        result.Add(ntValue);
                }
                catch (IdentityNotMappedException ex)
                {
                    _log.LogDebug(ex, "Skipped SID to NTAccount translation for SID {SidValue}.", sidValue);
                }
                catch (SystemException ex)
                {
                    _log.LogDebug(ex, "Skipped SID to NTAccount translation for SID {SidValue}.", sidValue);
                }
            }

            return result.ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogWarning(ex, "Failed to enumerate Windows group memberships for the current user.");
            return [];
        }
        catch (SystemException ex)
        {
            _log.LogWarning(ex, "Failed to enumerate Windows group memberships for the current user.");
            return [];
        }
    }
}
