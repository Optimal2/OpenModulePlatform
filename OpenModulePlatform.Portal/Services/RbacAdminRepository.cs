// File: OpenModulePlatform.Portal/Services/RbacAdminRepository.cs
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Provides Portal-facing RBAC administration over the core OMP security tables.
/// </summary>
/// <remarks>
/// <para>
/// This repository intentionally keeps the RBAC model simple at the Portal layer:
/// roles and permissions are treated as first-class entities, while role-permission
/// and role-principal links are managed from the role screen where the operator
/// already has the relevant context.
/// </para>
/// <para>
/// The implementation uses explicit SQL rather than a generic abstraction so that the
/// table shape remains easy to understand and adjust while the schema is still evolving.
/// </para>
/// </remarks>
public sealed class RbacAdminRepository
{
    private readonly SqlConnectionFactory _db;

    public RbacAdminRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns all roles together with lightweight usage counts.
    /// </summary>
    public async Task<IReadOnlyList<RoleRow>> GetRolesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT r.RoleId,
       r.Name,
       r.Description,
       (SELECT COUNT(1)
        FROM omp.RolePermissions rp
        WHERE rp.RoleId = r.RoleId) AS PermissionCount,
       (SELECT COUNT(1)
        FROM omp.RolePrincipals rpr
        WHERE rpr.RoleId = r.RoleId) AS PrincipalCount
FROM omp.Roles r
ORDER BY r.Name;";

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
                Description = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                PermissionCount = rdr.GetInt32(3),
                PrincipalCount = rdr.GetInt32(4)
            });
        }

        return rows;
    }

    /// <summary>
    /// Returns all permissions together with a count of referencing roles.
    /// </summary>
    public async Task<IReadOnlyList<PermissionRow>> GetPermissionsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT p.PermissionId,
       p.Name,
       p.Description,
       (SELECT COUNT(1)
        FROM omp.RolePermissions rp
        WHERE rp.PermissionId = p.PermissionId) AS RoleCount
FROM omp.Permissions p
ORDER BY p.Name;";

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
                Description = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                RoleCount = rdr.GetInt32(3)
            });
        }

        return rows;
    }

    public async Task<RoleRow?> GetRoleAsync(int roleId, CancellationToken ct)
    {
        const string sql = @"
SELECT r.RoleId,
       r.Name,
       r.Description,
       (SELECT COUNT(1)
        FROM omp.RolePermissions rp
        WHERE rp.RoleId = r.RoleId) AS PermissionCount,
       (SELECT COUNT(1)
        FROM omp.RolePrincipals rpr
        WHERE rpr.RoleId = r.RoleId) AS PrincipalCount
FROM omp.Roles r
WHERE r.RoleId = @roleId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@roleId", roleId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new RoleRow
        {
            RoleId = rdr.GetInt32(0),
            Name = rdr.GetString(1),
            Description = rdr.IsDBNull(2) ? null : rdr.GetString(2),
            PermissionCount = rdr.GetInt32(3),
            PrincipalCount = rdr.GetInt32(4)
        };
    }

    public async Task<PermissionRow?> GetPermissionAsync(int permissionId, CancellationToken ct)
    {
        const string sql = @"
SELECT p.PermissionId,
       p.Name,
       p.Description,
       (SELECT COUNT(1)
        FROM omp.RolePermissions rp
        WHERE rp.PermissionId = p.PermissionId) AS RoleCount
FROM omp.Permissions p
WHERE p.PermissionId = @permissionId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@permissionId", permissionId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new PermissionRow
        {
            PermissionId = rdr.GetInt32(0),
            Name = rdr.GetString(1),
            Description = rdr.IsDBNull(2) ? null : rdr.GetString(2),
            RoleCount = rdr.GetInt32(3)
        };
    }

    /// <summary>
    /// Returns the permissions that are currently linked to a role.
    /// </summary>
    public async Task<IReadOnlyList<RolePermissionRow>> GetRolePermissionsAsync(int roleId, CancellationToken ct)
    {
        const string sql = @"
SELECT rp.RoleId,
       rp.PermissionId,
       p.Name
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

    /// <summary>
    /// Returns the principals that are currently linked to a role.
    /// </summary>
    public async Task<IReadOnlyList<RolePrincipalRow>> GetRolePrincipalsAsync(int roleId, CancellationToken ct)
    {
        const string sql = @"
SELECT RoleId,
       PrincipalType,
       Principal
FROM omp.RolePrincipals
WHERE RoleId = @roleId
ORDER BY PrincipalType,
         Principal;";

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

    /// <summary>
    /// Returns permissions that are not yet linked to the specified role.
    /// </summary>
    public async Task<IReadOnlyList<PermissionRow>> GetAvailablePermissionsForRoleAsync(int roleId, CancellationToken ct)
    {
        const string sql = @"
SELECT p.PermissionId,
       p.Name,
       p.Description,
       CAST(0 AS int) AS RoleCount
FROM omp.Permissions p
WHERE NOT EXISTS
(
    SELECT 1
    FROM omp.RolePermissions rp
    WHERE rp.RoleId = @roleId
      AND rp.PermissionId = p.PermissionId
)
ORDER BY p.Name;";

        var rows = new List<PermissionRow>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@roleId", roleId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new PermissionRow
            {
                PermissionId = rdr.GetInt32(0),
                Name = rdr.GetString(1),
                Description = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                RoleCount = rdr.GetInt32(3)
            });
        }

        return rows;
    }

    public async Task<int> SaveRoleAsync(RoleEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.RoleId == 0)
        {
            const string sql = @"
INSERT INTO omp.Roles(Name, Description)
VALUES (@Name, @Description);

SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var cmd = new SqlCommand(sql, conn);
            Add(cmd, "@Name", input.Name);
            Add(cmd, "@Description", input.Description);

            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        const string updateSql = @"
UPDATE omp.Roles
SET Name = @Name,
    Description = @Description
WHERE RoleId = @RoleId;";

        await using (var update = new SqlCommand(updateSql, conn))
        {
            Add(update, "@RoleId", input.RoleId);
            Add(update, "@Name", input.Name);
            Add(update, "@Description", input.Description);
            await update.ExecuteNonQueryAsync(ct);
        }

        return input.RoleId;
    }

    public async Task<int> SavePermissionAsync(PermissionEditData input, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (input.PermissionId == 0)
        {
            const string sql = @"
INSERT INTO omp.Permissions(Name, Description)
VALUES (@Name, @Description);

SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var cmd = new SqlCommand(sql, conn);
            Add(cmd, "@Name", input.Name);
            Add(cmd, "@Description", input.Description);

            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        const string updateSql = @"
UPDATE omp.Permissions
SET Name = @Name,
    Description = @Description
WHERE PermissionId = @PermissionId;";

        await using (var update = new SqlCommand(updateSql, conn))
        {
            Add(update, "@PermissionId", input.PermissionId);
            Add(update, "@Name", input.Name);
            Add(update, "@Description", input.Description);
            await update.ExecuteNonQueryAsync(ct);
        }

        return input.PermissionId;
    }

    /// <summary>
    /// Deletes a role and its linking rows in a single transaction.
    /// </summary>
    public async Task DeleteRoleAsync(int roleId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await DeleteByRoleAsync(
                conn,
                (SqlTransaction)tx,
                "DELETE FROM omp.RolePrincipals WHERE RoleId = @RoleId;",
                roleId,
                ct);

            await DeleteByRoleAsync(
                conn,
                (SqlTransaction)tx,
                "DELETE FROM omp.RolePermissions WHERE RoleId = @RoleId;",
                roleId,
                ct);

            await DeleteByRoleAsync(
                conn,
                (SqlTransaction)tx,
                "DELETE FROM omp.Roles WHERE RoleId = @RoleId;",
                roleId,
                ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Deletes a permission and any role links in a single transaction.
    /// </summary>
    public async Task DeletePermissionAsync(int permissionId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await DeleteByPermissionAsync(
                conn,
                (SqlTransaction)tx,
                "DELETE FROM omp.RolePermissions WHERE PermissionId = @PermissionId;",
                permissionId,
                ct);

            await DeleteByPermissionAsync(
                conn,
                (SqlTransaction)tx,
                "DELETE FROM omp.Permissions WHERE PermissionId = @PermissionId;",
                permissionId,
                ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task AddPermissionToRoleAsync(int roleId, int permissionId, CancellationToken ct)
    {
        const string sql = @"
IF NOT EXISTS
(
    SELECT 1
    FROM omp.RolePermissions
    WHERE RoleId = @RoleId
      AND PermissionId = @PermissionId
)
BEGIN
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES (@RoleId, @PermissionId);
END";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@RoleId", roleId);
        Add(cmd, "@PermissionId", permissionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemovePermissionFromRoleAsync(int roleId, int permissionId, CancellationToken ct)
    {
        const string sql = @"
DELETE FROM omp.RolePermissions
WHERE RoleId = @RoleId
  AND PermissionId = @PermissionId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@RoleId", roleId);
        Add(cmd, "@PermissionId", permissionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddPrincipalToRoleAsync(
        int roleId,
        string principalType,
        string principal,
        CancellationToken ct)
    {
        const string sql = @"
IF NOT EXISTS
(
    SELECT 1
    FROM omp.RolePrincipals
    WHERE RoleId = @RoleId
      AND PrincipalType = @PrincipalType
      AND Principal = @Principal
)
BEGIN
    INSERT INTO omp.RolePrincipals(RoleId, PrincipalType, Principal)
    VALUES (@RoleId, @PrincipalType, @Principal);
END";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@RoleId", roleId);
        Add(cmd, "@PrincipalType", principalType);
        Add(cmd, "@Principal", principal);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemovePrincipalFromRoleAsync(
        int roleId,
        string principalType,
        string principal,
        CancellationToken ct)
    {
        const string sql = @"
DELETE FROM omp.RolePrincipals
WHERE RoleId = @RoleId
  AND PrincipalType = @PrincipalType
  AND Principal = @Principal;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@RoleId", roleId);
        Add(cmd, "@PrincipalType", principalType);
        Add(cmd, "@Principal", principal);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteByRoleAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string sql,
        int roleId,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@RoleId", roleId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteByPermissionAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string sql,
        int permissionId,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@PermissionId", permissionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Add(SqlCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}

/// <summary>
/// Lightweight role row used in list and detail views.
/// </summary>
public sealed class RoleRow
{
    public int RoleId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int PermissionCount { get; set; }

    public int PrincipalCount { get; set; }
}

/// <summary>
/// Lightweight permission row used in list and detail views.
/// </summary>
public sealed class PermissionRow
{
    public int PermissionId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int RoleCount { get; set; }
}

/// <summary>
/// Link row between a role and a permission.
/// </summary>
public sealed class RolePermissionRow
{
    public int RoleId { get; set; }

    public int PermissionId { get; set; }

    public string PermissionName { get; set; } = string.Empty;
}

/// <summary>
/// Link row between a role and a principal.
/// </summary>
public sealed class RolePrincipalRow
{
    public int RoleId { get; set; }

    public string PrincipalType { get; set; } = string.Empty;

    public string Principal { get; set; } = string.Empty;
}

/// <summary>
/// Mutable edit model for a role.
/// </summary>
public sealed class RoleEditData
{
    public int RoleId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }
}

/// <summary>
/// Mutable edit model for a permission.
/// </summary>
public sealed class PermissionEditData
{
    public int PermissionId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }
}
