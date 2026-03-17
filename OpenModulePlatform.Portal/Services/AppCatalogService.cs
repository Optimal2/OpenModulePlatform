// File: OpenModulePlatform.Portal/Services/AppCatalogService.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Builds the Portal start-page catalog from enabled <c>omp.AppInstances</c>.
/// </summary>
/// <remarks>
/// This is an important architectural choice: navigation is derived from app instances,
/// not app definitions. That keeps the Portal aligned with the instance-centric runtime
/// model where route, host placement and artifact choice belong to <c>AppInstance</c>.
/// </remarks>
public sealed class AppCatalogService
{
    private readonly SqlConnectionFactory _db;

    public AppCatalogService(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<PortalAppEntry>> GetEnabledWebAppsAsync(
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var hasHostBaseUrl = await HostBaseUrlColumnExistsAsync(conn, ct);
        var hostBaseUrlSelect = hasHostBaseUrl
            ? "h.BaseUrl"
            : "CAST(NULL AS nvarchar(300)) AS BaseUrl";

        var sql = $@"
SELECT ai.AppInstanceId,
       ai.AppInstanceKey,
       a.AppKey,
       ai.DisplayName,
       ai.RoutePath,
       ai.PublicUrl,
       h.HostKey,
       {hostBaseUrlSelect},
       ai.SortOrder,
       ai.Description,
       p.Name AS PermissionName,
       ap.RequireAll
FROM omp.AppInstances ai
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
LEFT JOIN omp.Hosts h ON h.HostId = ai.HostId
LEFT JOIN omp.AppPermissions ap ON ap.AppId = a.AppId
LEFT JOIN omp.Permissions p ON p.PermissionId = ap.PermissionId
WHERE ai.IsEnabled = 1
  AND ai.IsAllowed = 1
  AND a.AppType IN (N'Portal', N'WebApp')
ORDER BY ai.SortOrder,
         ai.DisplayName;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var map = new Dictionary<Guid, PortalAppEntry>();
        while (await rdr.ReadAsync(ct))
        {
            var appInstanceId = rdr.GetGuid(0);
            if (!map.TryGetValue(appInstanceId, out var entry))
            {
                entry = new PortalAppEntry
                {
                    AppInstanceId = appInstanceId,
                    AppInstanceKey = rdr.GetString(1),
                    AppKey = rdr.GetString(2),
                    DisplayName = rdr.GetString(3),
                    RoutePath = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    PublicUrl = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    HostKey = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    HostBaseUrl = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    SortOrder = rdr.GetInt32(8),
                    Description = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                    RequireAll = !rdr.IsDBNull(11) && rdr.GetBoolean(11)
                };

                map[appInstanceId] = entry;
            }
            else
            {
                entry.RequireAll = entry.RequireAll ||
                    (!rdr.IsDBNull(11) && rdr.GetBoolean(11));
            }

            if (!rdr.IsDBNull(10))
            {
                entry.RequiredPermissions.Add(rdr.GetString(10));
            }
        }

        return map.Values
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.DisplayName)
            .ToList();
    }

    private static async Task<bool> HostBaseUrlColumnExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = "SELECT CAST(CASE WHEN COL_LENGTH('omp.Hosts', 'BaseUrl') IS NULL THEN 0 ELSE 1 END AS bit);";
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Applies RBAC filtering to catalog entries already loaded from the database.
    /// </summary>
    public IReadOnlyList<PortalAppEntry> FilterByPermissions(
        IReadOnlyList<PortalAppEntry> apps,
        IReadOnlySet<string> permissions)
    {
        return apps.Where(app =>
        {
            if (app.RequiredPermissions.Count == 0)
            {
                return true;
            }

            return app.RequireAll
                ? app.RequiredPermissions.All(permissions.Contains)
                : app.RequiredPermissions.Any(permissions.Contains);
        }).ToList();
    }
}
