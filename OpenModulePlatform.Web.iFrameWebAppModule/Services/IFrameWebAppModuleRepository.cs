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

    public async Task<IFrameUrlSetRow?> GetUrlSetAsync(string setKey, CancellationToken ct)
    {
        const string sql = @"
SELECT [id],
       [set_key],
       [displayname],
       [enabled]
FROM omp_iframe.url_sets
WHERE [set_key] = @SetKey;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@SetKey", setKey);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new IFrameUrlSetRow
        {
            Id = rdr.GetInt32(0),
            SetKey = rdr.GetString(1),
            DisplayName = rdr.GetString(2),
            Enabled = rdr.GetBoolean(3)
        };
    }

    public async Task<IReadOnlyList<IFrameUrlRow>> GetConfiguredUrlsForSetAsync(int urlSetId, CancellationToken ct)
    {
        const string sql = @"
SELECT u.[id],
       u.[url],
       u.[displayname],
       u.[allowed_roles],
       u.[enabled]
FROM omp_iframe.url_set_urls usu
INNER JOIN omp_iframe.urls u ON u.[id] = usu.[url_id]
WHERE usu.[url_set_id] = @UrlSetId
ORDER BY usu.[sort_order], u.[id];";

        var rows = new List<IFrameUrlRow>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@UrlSetId", urlSetId);
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
