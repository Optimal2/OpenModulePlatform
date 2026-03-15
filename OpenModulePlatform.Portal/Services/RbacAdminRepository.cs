// File: OpenModulePlatform.Portal/Services/RbacAdminRepository.cs
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Services;

public sealed class RbacAdminRepository
{
    private readonly SqlConnectionFactory _db;

    public RbacAdminRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<RoleRow>> GetRolesAsync(CancellationToken ct)
    {
        const string sql = @"SELECT RoleId, Name, Description FROM omp.Roles ORDER BY Name;";
        var rows = new List<RoleRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new RoleRow
            {
                RoleId = rdr.GetInt32(0),
                Name = rdr.GetString(1),
                Description = rdr.IsDBNull(2) ? null : rdr.GetString(2)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<PermissionRow>> GetPermissionsAsync(CancellationToken ct)
    {
        const string sql = @"SELECT PermissionId, Name, Description FROM omp.Permissions ORDER BY Name;";
        var rows = new List<PermissionRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new PermissionRow
            {
                PermissionId = rdr.GetInt32(0),
                Name = rdr.GetString(1),
                Description = rdr.IsDBNull(2) ? null : rdr.GetString(2)
            });
        }
        return rows;
    }

    public async Task<RoleRow?> GetRoleAsync(int roleId, CancellationToken ct)
    {
        const string sql = @"SELECT RoleId, Name, Description FROM omp.Roles WHERE RoleId = @roleId;";
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@roleId", roleId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
            return null;

        return new RoleRow
        {
            RoleId = rdr.GetInt32(0),
            Name = rdr.GetString(1),
            Description = rdr.IsDBNull(2) ? null : rdr.GetString(2)
        };
    }

    public async Task<IReadOnlyList<RolePermissionRow>> GetRolePermissionsAsync(int roleId, CancellationToken ct)
    {
        const string sql = @"
SELECT rp.RoleId, rp.PermissionId, p.Name
FROM omp.RolePermissions rp
INNER JOIN omp.Permissions p ON p.PermissionId = rp.PermissionId
WHERE rp.RoleId = @roleId
ORDER BY p.Name;";

        var rows = new List<RolePermissionRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@roleId", roleId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new RolePermissionRow
            {
                RoleId = rdr.GetInt32(0),
                PermissionId = rdr.GetInt32(1),
                PermissionName = rdr.GetString(2)
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<RolePrincipalRow>> GetRolePrincipalsAsync(int roleId, CancellationToken ct)
    {
        const string sql = @"
SELECT RoleId, PrincipalType, Principal
FROM omp.RolePrincipals
WHERE RoleId = @roleId
ORDER BY PrincipalType, Principal;";

        var rows = new List<RolePrincipalRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@roleId", roleId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new RolePrincipalRow
            {
                RoleId = rdr.GetInt32(0),
                PrincipalType = rdr.GetString(1),
                Principal = rdr.GetString(2)
            });
        }
        return rows;
    }
}

public sealed class RoleRow
{
    public int RoleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class PermissionRow
{
    public int PermissionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class RolePermissionRow
{
    public int RoleId { get; set; }
    public int PermissionId { get; set; }
    public string PermissionName { get; set; } = string.Empty;
}

public sealed class RolePrincipalRow
{
    public int RoleId { get; set; }
    public string PrincipalType { get; set; } = string.Empty;
    public string Principal { get; set; } = string.Empty;
}
