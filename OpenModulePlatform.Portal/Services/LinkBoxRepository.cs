// File: OpenModulePlatform.Portal/Services/LinkBoxRepository.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Services;

// User-curated link boxes (omp.link_boxes + omp.link_box_items). The tables
// are platform storage: any web app can read its own boxes, while Portal
// hosts the one generic editor (/admin/navigation). Labels and titles are
// localization keys resolved resx-first at render time (unknown keys render
// as their own text, so user-entered free text just works).
public sealed class LinkBoxRepository
{
    private readonly SqlConnectionFactory _db;

    public LinkBoxRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<LinkBoxRow?> GetBoxAsync(string boxKey, CancellationToken ct)
    {
        const string sql = @"
SELECT box_key, title, required_permission, sort_order
FROM omp.link_boxes
WHERE box_key = @boxKey;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@boxKey", boxKey);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return ReadBox(rdr);
    }

    public async Task<IReadOnlyList<LinkBoxRow>> GetBoxesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT box_key, title, required_permission, sort_order
FROM omp.link_boxes
ORDER BY sort_order, box_key;";

        var rows = new List<LinkBoxRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(ReadBox(rdr));
        }

        return rows;
    }

    public async Task UpsertBoxAsync(string boxKey, string title, string? requiredPermission, CancellationToken ct)
    {
        const string sql = @"
MERGE omp.link_boxes AS target
USING (SELECT @boxKey AS box_key) AS source
    ON target.box_key = source.box_key
WHEN MATCHED THEN
    UPDATE SET title = @title, required_permission = @requiredPermission, updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (box_key, title, required_permission) VALUES (@boxKey, @title, @requiredPermission);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@boxKey", boxKey);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@requiredPermission", (object?)requiredPermission ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<LinkBoxItemRow>> GetItemsAsync(string boxKey, CancellationToken ct)
    {
        const string sql = @"
SELECT link_box_item_id, box_key, label, url, group_key, sort_order, required_permission
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
                SortOrder = rdr.GetInt32(5),
                RequiredPermission = rdr.IsDBNull(6) ? null : rdr.GetString(6)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<string>> GetBoxKeysAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT box_key FROM omp.link_boxes
UNION
SELECT box_key FROM omp.link_box_items
ORDER BY box_key;";

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

    public async Task AddItemAsync(string boxKey, string label, string url, string? groupKey, string? requiredPermission, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.link_box_items(box_key, label, url, group_key, sort_order, required_permission)
SELECT @boxKey, @label, @url, @groupKey,
       ISNULL((SELECT MAX(sort_order) + 1 FROM omp.link_box_items WHERE box_key = @boxKey), 0),
       @requiredPermission;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@boxKey", boxKey);
        cmd.Parameters.AddWithValue("@label", label);
        cmd.Parameters.AddWithValue("@url", url);
        cmd.Parameters.AddWithValue("@groupKey", (object?)groupKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requiredPermission", (object?)requiredPermission ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateItemAsync(long linkBoxItemId, string label, string url, string? groupKey, string? requiredPermission, CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.link_box_items
SET label = @label, url = @url, group_key = @groupKey, required_permission = @requiredPermission, updated_at = SYSUTCDATETIME()
WHERE link_box_item_id = @id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", linkBoxItemId);
        cmd.Parameters.AddWithValue("@label", label);
        cmd.Parameters.AddWithValue("@url", url);
        cmd.Parameters.AddWithValue("@groupKey", (object?)groupKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requiredPermission", (object?)requiredPermission ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateSortOrdersAsync(string boxKey, IReadOnlyList<long> orderedIds, CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.link_box_items
SET sort_order = @sortOrder, updated_at = SYSUTCDATETIME()
WHERE link_box_item_id = @id AND box_key = @boxKey;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        for (var index = 0; index < orderedIds.Count; index++)
        {
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", orderedIds[index]);
            cmd.Parameters.AddWithValue("@boxKey", boxKey);
            cmd.Parameters.AddWithValue("@sortOrder", index);
            await cmd.ExecuteNonQueryAsync(ct);
        }
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

    public async Task<IReadOnlyList<string>> GetPermissionNamesAsync(CancellationToken ct)
    {
        const string sql = "SELECT Name FROM omp.Permissions ORDER BY Name;";

        var names = new List<string>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            names.Add(rdr.GetString(0));
        }

        return names;
    }

    private static LinkBoxRow ReadBox(SqlDataReader rdr)
    {
        return new LinkBoxRow
        {
            BoxKey = rdr.GetString(0),
            Title = rdr.GetString(1),
            RequiredPermission = rdr.IsDBNull(2) ? null : rdr.GetString(2),
            SortOrder = rdr.GetInt32(3)
        };
    }
}
