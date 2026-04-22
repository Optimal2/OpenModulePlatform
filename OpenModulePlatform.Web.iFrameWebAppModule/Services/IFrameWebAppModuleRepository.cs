using OpenModulePlatform.Web.iFrameWebAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Web.iFrameWebAppModule.Services;

public sealed class IFrameWebAppModuleRepository
{
    private readonly SqlConnectionFactory _db;

    public IFrameWebAppModuleRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<IFrameUrlRow>> GetConfiguredUrlsAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT [id],
       [url],
       [displayname],
       [allowed_roles],
       [enabled]
FROM omp_iframe_module.urls
WHERE [id] IN (1, 2, 3)
ORDER BY [id];";

        var rows = new List<IFrameUrlRow>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new IFrameUrlRow
            {
                Id = rdr.GetInt32(0),
                Url = rdr.GetString(1),
                DisplayName = rdr.GetString(2),
                AllowedRoles = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                Enabled = rdr.GetBoolean(4)
            });
        }

        return rows;
    }
}
