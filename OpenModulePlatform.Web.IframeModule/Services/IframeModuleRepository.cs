using OpenModulePlatform.Web.IframeModule.ViewModels;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Web.IframeModule.Services;

/// <summary>
/// Data access for the iframe module proof-of-concept.
/// </summary>
public sealed class IframeModuleRepository
{
    private readonly SqlConnectionFactory _db;

    public IframeModuleRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<IframeUrlRow>> GetTopUrlsAsync(string? activeRoleName, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (3)
       u.Id,
       u.Url,
       u.DisplayName,
       u.AllowedRoles,
       u.SortOrder
FROM omp_iframe_module.urls u
WHERE u.Enabled = 1
  AND
  (
      NULLIF(LTRIM(RTRIM(ISNULL(u.AllowedRoles, N''))), N'') IS NULL
      OR
      (
          @activeRoleName IS NOT NULL
          AND EXISTS
          (
              SELECT 1
              FROM string_split(replace(u.AllowedRoles, ';', ','), ',') allowed
              WHERE LTRIM(RTRIM(allowed.value)) = @activeRoleName
          )
      )
  )
ORDER BY u.SortOrder, u.Id;";

        var rows = new List<IframeUrlRow>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@activeRoleName", (object?)activeRoleName ?? DBNull.Value);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new IframeUrlRow
            {
                Id = rdr.GetInt32(0),
                Url = rdr.GetString(1),
                DisplayName = rdr.GetString(2),
                AllowedRoles = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                SortOrder = rdr.GetInt32(4)
            });
        }

        return rows;
    }
}
