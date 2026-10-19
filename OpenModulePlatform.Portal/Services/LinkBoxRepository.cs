// File: OpenModulePlatform.Portal/Services/LinkBoxRepository.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;

namespace OpenModulePlatform.Portal.Services;

// User-curated link boxes (omp.link_boxes + omp.link_box_items). The tables
// are platform storage: any web app can read its own boxes, while Portal
// hosts the one generic editor (/admin/navigation). Labels and titles are
// localization keys resolved resx-first at render time (unknown keys render
// as their own text, so user-entered free text just works).
public sealed class LinkBoxRepository
{
    // Parameter sizes follow omp.link_boxes and omp.link_box_items
    // (sql/1-setup-openmoduleplatform.sql).
    private const int BoxKeyColumnLength = 200;
    private const int TitleColumnLength = 200;
    private const int RequiredPermissionColumnLength = 200;
    private const int LabelColumnLength = 200;
    private const int UrlColumnLength = 400;
    private const int GroupKeyColumnLength = 100;

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
        AddNVarChar(cmd, "@boxKey", boxKey, BoxKeyColumnLength);
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
        AddNVarChar(cmd, "@boxKey", boxKey, BoxKeyColumnLength);
        AddNVarChar(cmd, "@title", title, TitleColumnLength);
        AddNVarChar(cmd, "@requiredPermission", requiredPermission, RequiredPermissionColumnLength);
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
        AddNVarChar(cmd, "@boxKey", boxKey, BoxKeyColumnLength);
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
        AddNVarChar(cmd, "@boxKey", boxKey, BoxKeyColumnLength);
        AddNVarChar(cmd, "@label", label, LabelColumnLength);
        AddNVarChar(cmd, "@url", url, UrlColumnLength);
        AddNVarChar(cmd, "@groupKey", groupKey, GroupKeyColumnLength);
        AddNVarChar(cmd, "@requiredPermission", requiredPermission, RequiredPermissionColumnLength);
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
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = linkBoxItemId;
        AddNVarChar(cmd, "@label", label, LabelColumnLength);
        AddNVarChar(cmd, "@url", url, UrlColumnLength);
        AddNVarChar(cmd, "@groupKey", groupKey, GroupKeyColumnLength);
        AddNVarChar(cmd, "@requiredPermission", requiredPermission, RequiredPermissionColumnLength);
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
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = orderedIds[index];
            AddNVarChar(cmd, "@boxKey", boxKey, BoxKeyColumnLength);
            cmd.Parameters.Add("@sortOrder", SqlDbType.Int).Value = index;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // Renames a group on every link in the box (renaming to an existing group
    // merges the two); an empty new name removes the group from its links.
    public async Task RenameGroupAsync(string boxKey, string oldGroup, string? newGroup, CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.link_box_items
SET group_key = @newGroup, updated_at = SYSUTCDATETIME()
WHERE box_key = @boxKey AND group_key = @oldGroup;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        AddNVarChar(cmd, "@boxKey", boxKey, BoxKeyColumnLength);
        AddNVarChar(cmd, "@oldGroup", oldGroup, GroupKeyColumnLength);
        AddNVarChar(cmd, "@newGroup", newGroup, GroupKeyColumnLength);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <remarks>R8-P3-9: returns whether a row was actually removed, so the caller can tell
    /// "deleted" from "already gone" instead of reporting success either way. The module-repository half
    /// of this was done in R7-C7; the OMP siblings were never carried across.</remarks>
    public async Task<bool> DeleteItemAsync(long linkBoxItemId, CancellationToken ct)
    {
        const string sql = "DELETE FROM omp.link_box_items WHERE link_box_item_id = @id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = linkBoxItemId;
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
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

    /// <summary>
    /// Adds an nvarchar parameter sized to its column. A longer value keeps its own
    /// length instead of being truncated by SqlClient, so an over-long write still
    /// fails and an over-long lookup still finds nothing, exactly as before.
    /// </summary>
    private static void AddNVarChar(SqlCommand cmd, string name, string? value, int columnLength)
    {
        var size = value is not null && value.Length > columnLength ? value.Length : columnLength;
        cmd.Parameters.Add(name, SqlDbType.NVarChar, size).Value = (object?)value ?? DBNull.Value;
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
