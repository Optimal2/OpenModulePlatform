// File: OpenModulePlatform.Portal/Services/LinkBoxRepository.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Services;

// User-curated link box links (omp.link_box_items). The table is platform
// storage: any web app can read its own boxes, while Portal hosts the one
// generic editor (/admin/navigation).
public sealed class LinkBoxRepository
{
    private readonly SqlConnectionFactory _db;

    public LinkBoxRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LinkBoxItemRow>> GetItemsAsync(string boxKey, CancellationToken ct)
    {
        const string sql = @"
SELECT link_box_item_id, box_key, label, url, group_key, sort_order
FROM omp.link_box_items
WHERE box_key = @boxKey
ORDER BY sort_order, label;";

        var rows = new List<LinkBoxItemRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@boxKey", boxKey);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new LinkBoxItemRow
            {
                LinkBoxItemId = rdr.GetInt64(0),
                BoxKey = rdr.GetString(1),
                Label = rdr.GetString(2),
                Url = rdr.GetString(3),
                GroupKey = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                SortOrder = rdr.GetInt32(5)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<string>> GetBoxKeysAsync(CancellationToken ct)
    {
        const string sql = "SELECT DISTINCT box_key FROM omp.link_box_items ORDER BY box_key;";

        var keys = new List<string>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            keys.Add(rdr.GetString(0));
        }

        return keys;
    }

    public async Task AddItemAsync(string boxKey, string label, string url, string? groupKey, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.link_box_items(box_key, label, url, group_key, sort_order)
SELECT @boxKey, @label, @url, @groupKey,
       ISNULL((SELECT MAX(sort_order) + 1 FROM omp.link_box_items WHERE box_key = @boxKey), 0);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@boxKey", boxKey);
        cmd.Parameters.AddWithValue("@label", label);
        cmd.Parameters.AddWithValue("@url", url);
        cmd.Parameters.AddWithValue("@groupKey", (object?)groupKey ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteItemAsync(long linkBoxItemId, CancellationToken ct)
    {
        const string sql = "DELETE FROM omp.link_box_items WHERE link_box_item_id = @id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", linkBoxItemId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
