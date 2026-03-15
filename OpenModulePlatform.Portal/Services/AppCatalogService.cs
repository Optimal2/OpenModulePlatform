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
SELECT ai.AppInstanceId,
       ai.AppInstanceKey,
       a.AppKey,
       ai.DisplayName,
       ai.RoutePath,
       ai.PublicUrl,
       ai.SortOrder,
       ai.Description,
       p.Name AS PermissionName,
       ap.RequireAll
FROM omp.AppInstances ai
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
LEFT JOIN omp.AppPermissions ap ON ap.AppId = a.AppId
LEFT JOIN omp.Permissions p ON p.PermissionId = ap.PermissionId
WHERE ai.IsEnabled = 1
  AND ai.IsAllowed = 1
  AND a.AppType IN (N'Portal', N'WebApp')
ORDER BY ai.SortOrder, ai.DisplayName;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

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
                    SortOrder = rdr.GetInt32(6),
                    Description = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    RequireAll = !rdr.IsDBNull(9) && rdr.GetBoolean(9)
                };
                map[appInstanceId] = entry;
            }
            else
            {
                entry.RequireAll = entry.RequireAll || (!rdr.IsDBNull(9) && rdr.GetBoolean(9));
            }

            if (!rdr.IsDBNull(8))
                entry.RequiredPermissions.Add(rdr.GetString(8));
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
