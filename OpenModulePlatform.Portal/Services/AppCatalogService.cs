// File: OpenModulePlatform.Portal/Services/AppCatalogService.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Services;

public sealed class AppCatalogService
{
    private readonly SqlConnectionFactory _db;

    public AppCatalogService(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<PortalAppEntry>> GetEnabledWebAppsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT a.AppId,
       a.AppKey,
       a.DisplayName,
       a.RouteBasePath,
       a.SortOrder,
       a.Description,
       p.Name AS PermissionName,
       ap.RequireAll
FROM omp.Apps a
LEFT JOIN omp.AppPermissions ap ON ap.AppId = a.AppId
LEFT JOIN omp.Permissions p ON p.PermissionId = ap.PermissionId
WHERE a.IsEnabled = 1
  AND a.AppType IN (N'Portal', N'WebApp')
ORDER BY a.SortOrder, a.DisplayName;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var map = new Dictionary<int, PortalAppEntry>();
        while (await rdr.ReadAsync(ct))
        {
            var appId = rdr.GetInt32(0);
            if (!map.TryGetValue(appId, out var entry))
            {
                entry = new PortalAppEntry
                {
                    AppId = appId,
                    AppKey = rdr.GetString(1),
                    DisplayName = rdr.GetString(2),
                    RouteBasePath = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    SortOrder = rdr.GetInt32(4),
                    Description = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    RequireAll = !rdr.IsDBNull(7) && rdr.GetBoolean(7)
                };
                map[appId] = entry;
            }
            else
            {
                entry.RequireAll = entry.RequireAll || (!rdr.IsDBNull(7) && rdr.GetBoolean(7));
            }

            if (!rdr.IsDBNull(6))
                entry.RequiredPermissions.Add(rdr.GetString(6));
        }

        return map.Values.OrderBy(x => x.SortOrder).ThenBy(x => x.DisplayName).ToList();
    }

    public IReadOnlyList<PortalAppEntry> FilterByPermissions(IReadOnlyList<PortalAppEntry> apps, IReadOnlySet<string> permissions)
    {
        return apps.Where(app =>
        {
            if (app.RequiredPermissions.Count == 0)
                return true;

            return app.RequireAll
                ? app.RequiredPermissions.All(permissions.Contains)
                : app.RequiredPermissions.Any(permissions.Contains);
        }).ToList();
    }
}
