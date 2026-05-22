// File: OpenModulePlatform.Portal/Services/PortalEntryService.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Security;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Builds and persists the personalized Portal Entries list.
/// </summary>
public sealed class PortalEntryService
{
    private const string AppEntryPrefix = "app:";
    private const string AppEntrySuffix = ":home";
    private static readonly Regex EntryKeyUnsafeCharacters = new("[^a-z0-9._-]+", RegexOptions.Compiled);

    private readonly SqlConnectionFactory _db;
    private readonly AppCatalogService _catalog;

    public PortalEntryService(SqlConnectionFactory db, AppCatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<PortalEntry>> GetEntriesAsync(
        HttpRequest request,
        int? userId,
        IReadOnlySet<string> permissions,
        bool includeHidden,
        CancellationToken ct)
    {
        var rows = await GetRowsAsync(userId, ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var rowsById = rows.ToDictionary(row => row.PortalEntryId);
        var allApps = await _catalog.GetEnabledWebAppsAsync(ct);
        var accessibleApps = _catalog.FilterByPermissions(allApps, permissions)
            .ToDictionary(app => app.AppInstanceId);
        var navigationFavorites = userId.HasValue
            ? (await GetNavigationFavoriteEntryKeysAsync(userId.Value, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var entries = new List<PortalEntry>();
        foreach (var row in rows)
        {
            if (!row.IsEnabled || (!includeHidden && row.IsHidden))
            {
                continue;
            }

            if (row.SourceAppInstanceId is Guid sourceAppInstanceId && !accessibleApps.ContainsKey(sourceAppInstanceId))
            {
                continue;
            }

            if (TryParseAppInstanceId(row.TargetEntryKey, out var targetAppInstanceId)
                && !accessibleApps.ContainsKey(targetAppInstanceId))
            {
                continue;
            }

            var href = ResolveTargetHref(request, row, accessibleApps);
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            if (!CanAccessTargetHref(request, href, allApps, accessibleApps, permissions))
            {
                continue;
            }

            entries.Add(new PortalEntry
            {
                PortalEntryId = row.PortalEntryId,
                ParentEntryId = row.ParentEntryId,
                EntryKey = row.EntryKey,
                DisplayName = row.DisplayName,
                ContextName = BuildContextName(row, rowsById),
                Description = row.Description,
                LogoUrl = row.LogoUrl,
                IconKey = row.IconKey,
                TargetHref = href,
                TargetEntryKey = row.TargetEntryKey,
                IsPinned = row.IsPinned,
                IsHidden = row.IsHidden,
                IsNavigationFavorite = navigationFavorites.Contains(row.EntryKey),
                UserSortOrder = row.SortOrder,
                DefaultSortOrder = row.DefaultSortOrder
            });
        }

        return entries;
    }

    public async Task<IReadOnlyList<PortalEntry>> GetNavigationFavoriteEntriesAsync(
        HttpRequest request,
        int userId,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        var entries = await GetEntriesAsync(request, userId, permissions, includeHidden: false, ct);
        if (entries.Count == 0)
        {
            return [];
        }

        var favoriteEntryKeys = entries
            .Where(entry => entry.IsNavigationFavorite)
            .Select(entry => entry.EntryKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (favoriteEntryKeys.Count == 0)
        {
            return [];
        }

        var entriesByKey = entries.ToDictionary(entry => entry.EntryKey, StringComparer.OrdinalIgnoreCase);
        var favorites = new List<PortalEntry>();
        foreach (var entryKey in await GetNavigationFavoriteEntryKeysAsync(userId, ct))
        {
            if (favoriteEntryKeys.Contains(entryKey) && entriesByKey.TryGetValue(entryKey, out var entry))
            {
                favorites.Add(entry);
            }
        }

        return favorites;
    }

    public async Task<IReadOnlyList<PortalEntryAdminRow>> GetAdminRowsAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT portal_entry_id,
       parent_entry_id,
       parent_display_name,
       entry_key,
       display_name,
       description,
       logo_url,
       icon_key,
       target_url,
       target_entry_key,
       source_app_instance_id,
       is_enabled,
       default_sort_order
FROM
(
    SELECT pe.portal_entry_id,
           pe.parent_entry_id,
           parent.display_name AS parent_display_name,
           pe.entry_key,
           pe.display_name,
           pe.description,
           pe.logo_url,
           pe.icon_key,
           pe.target_url,
           pe.target_entry_key,
           pe.source_app_instance_id,
           pe.is_enabled,
           pe.default_sort_order
    FROM omp_portal.portal_entries pe
    LEFT JOIN omp_portal.portal_entries parent ON parent.portal_entry_id = pe.parent_entry_id
) rows
ORDER BY COALESCE(parent_display_name, display_name),
         CASE WHEN parent_entry_id IS NULL THEN 0 ELSE 1 END,
         default_sort_order,
         display_name;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<PortalEntryAdminRow>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new PortalEntryAdminRow
            {
                PortalEntryId = rdr.GetInt32(0),
                ParentEntryId = rdr.IsDBNull(1) ? null : rdr.GetInt32(1),
                ParentDisplayName = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                EntryKey = rdr.GetString(3),
                DisplayName = rdr.GetString(4),
                Description = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                LogoUrl = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                IconKey = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                TargetUrl = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                TargetEntryKey = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                SourceAppInstanceId = rdr.IsDBNull(10) ? null : rdr.GetGuid(10),
                IsEnabled = rdr.GetBoolean(11),
                DefaultSortOrder = rdr.GetInt32(12)
            });
        }

        return rows;
    }

    public async Task<PortalEntryAdminRow?> GetAdminRowAsync(int portalEntryId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT portal_entry_id,
       parent_entry_id,
       entry_key,
       display_name,
       description,
       logo_url,
       icon_key,
       target_url,
       target_entry_key,
       source_app_instance_id,
       is_enabled,
       default_sort_order
FROM omp_portal.portal_entries
WHERE portal_entry_id = @portal_entry_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryId;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new PortalEntryAdminRow
        {
            PortalEntryId = rdr.GetInt32(0),
            ParentEntryId = rdr.IsDBNull(1) ? null : rdr.GetInt32(1),
            EntryKey = rdr.GetString(2),
            DisplayName = rdr.GetString(3),
            Description = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            LogoUrl = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            IconKey = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            TargetUrl = rdr.IsDBNull(7) ? null : rdr.GetString(7),
            TargetEntryKey = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            SourceAppInstanceId = rdr.IsDBNull(9) ? null : rdr.GetGuid(9),
            IsEnabled = rdr.GetBoolean(10),
            DefaultSortOrder = rdr.GetInt32(11)
        };
    }

    public async Task<IReadOnlyList<OptionItem>> GetParentOptionsAsync(int? excludePortalEntryId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT portal_entry_id,
       display_name,
       entry_key
FROM omp_portal.portal_entries
WHERE (@exclude_portal_entry_id IS NULL OR portal_entry_id <> @exclude_portal_entry_id)
ORDER BY display_name,
         entry_key;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@exclude_portal_entry_id", SqlDbType.Int).Value =
            excludePortalEntryId.HasValue ? excludePortalEntryId.Value : DBNull.Value;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var options = new List<OptionItem>();
        while (await rdr.ReadAsync(ct))
        {
            options.Add(new OptionItem
            {
                Value = rdr.GetInt32(0).ToString(CultureInfo.InvariantCulture),
                Label = $"{rdr.GetString(1)} ({rdr.GetString(2)})"
            });
        }

        return options;
    }

    public async Task<bool> WouldCreateCycleAsync(int portalEntryId, int? parentEntryId, CancellationToken ct)
    {
        if (!parentEntryId.HasValue)
        {
            return false;
        }

        if (parentEntryId.Value == portalEntryId)
        {
            return true;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var visited = new HashSet<int> { portalEntryId };
        var currentParentId = parentEntryId;
        while (currentParentId.HasValue)
        {
            if (!visited.Add(currentParentId.Value))
            {
                return true;
            }

            const string sql = @"
SELECT parent_entry_id
FROM omp_portal.portal_entries
WHERE portal_entry_id = @portal_entry_id;";

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = currentParentId.Value;
            var result = await cmd.ExecuteScalarAsync(ct);

            currentParentId = result is null or DBNull
                ? null
                : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }

        return false;
    }

    public async Task<int> CreateAsync(PortalEntryCreateData data, CancellationToken ct)
    {
        var entryKey = await CreateUniqueEntryKeyAsync(data.DisplayName, ct);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
INSERT INTO omp_portal.portal_entries
(
    entry_key,
    parent_entry_id,
    display_name,
    description,
    logo_url,
    icon_key,
    target_url,
    target_entry_key,
    is_enabled,
    default_sort_order
)
OUTPUT INSERTED.portal_entry_id
VALUES
(
    @entry_key,
    @parent_entry_id,
    @display_name,
    @description,
    @logo_url,
    @icon_key,
    @target_url,
    @target_entry_key,
    @is_enabled,
    @default_sort_order
);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@entry_key", SqlDbType.NVarChar, 200).Value = entryKey;
        cmd.Parameters.Add("@parent_entry_id", SqlDbType.Int).Value = data.ParentEntryId.HasValue ? data.ParentEntryId.Value : DBNull.Value;
        cmd.Parameters.Add("@display_name", SqlDbType.NVarChar, 200).Value = data.DisplayName.Trim();
        cmd.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value = Clean(data.Description) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@logo_url", SqlDbType.NVarChar, 600).Value = Clean(data.LogoUrl) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@icon_key", SqlDbType.NVarChar, 100).Value = Clean(data.IconKey) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@target_url", SqlDbType.NVarChar, 600).Value = Clean(data.TargetUrl) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@target_entry_key", SqlDbType.NVarChar, 200).Value = Clean(data.TargetEntryKey) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = data.IsEnabled;
        cmd.Parameters.Add("@default_sort_order", SqlDbType.Int).Value = data.DefaultSortOrder;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<bool> UpdateAsync(PortalEntryEditData data, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
UPDATE omp_portal.portal_entries
SET parent_entry_id = @parent_entry_id,
    display_name = @display_name,
    description = @description,
    logo_url = @logo_url,
    icon_key = @icon_key,
    target_url = @target_url,
    target_entry_key = @target_entry_key,
    is_enabled = @is_enabled,
    default_sort_order = @default_sort_order,
    updated_at = SYSUTCDATETIME()
WHERE portal_entry_id = @portal_entry_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = data.PortalEntryId;
        cmd.Parameters.Add("@parent_entry_id", SqlDbType.Int).Value = data.ParentEntryId.HasValue ? data.ParentEntryId.Value : DBNull.Value;
        cmd.Parameters.Add("@display_name", SqlDbType.NVarChar, 200).Value = data.DisplayName.Trim();
        cmd.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value = Clean(data.Description) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@logo_url", SqlDbType.NVarChar, 600).Value = Clean(data.LogoUrl) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@icon_key", SqlDbType.NVarChar, 100).Value = Clean(data.IconKey) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@target_url", SqlDbType.NVarChar, 600).Value = Clean(data.TargetUrl) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@target_entry_key", SqlDbType.NVarChar, 200).Value = Clean(data.TargetEntryKey) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = data.IsEnabled;
        cmd.Parameters.Add("@default_sort_order", SqlDbType.Int).Value = data.DefaultSortOrder;
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task UpdateLayoutAsync(IReadOnlyList<PortalEntryLayoutUpdate> updates, CancellationToken ct)
    {
        if (updates.Count == 0)
        {
            return;
        }

        var distinctUpdates = updates
            .GroupBy(update => update.PortalEntryId)
            .Select(group => group.Last())
            .ToArray();

        ValidateLayoutUpdates(distinctUpdates);
        ApplyParentVisibility(distinctUpdates);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            const string sql = @"
UPDATE omp_portal.portal_entries
SET parent_entry_id = @parent_entry_id,
    is_enabled = @is_enabled,
    default_sort_order = @default_sort_order,
    updated_at = SYSUTCDATETIME()
WHERE portal_entry_id = @portal_entry_id;";

            foreach (var update in distinctUpdates)
            {
                await using var cmd = new SqlCommand(sql, conn, tx);
                cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = update.PortalEntryId;
                cmd.Parameters.Add("@parent_entry_id", SqlDbType.Int).Value = update.ParentEntryId.HasValue ? update.ParentEntryId.Value : DBNull.Value;
                cmd.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = update.IsEnabled;
                cmd.Parameters.Add("@default_sort_order", SqlDbType.Int).Value = update.SortOrder;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static void ApplyParentVisibility(IReadOnlyList<PortalEntryLayoutUpdate> updates)
    {
        var updatesById = updates.ToDictionary(update => update.PortalEntryId);
        foreach (var update in updates.Where(update => HasHiddenAncestor(update, updatesById)))
        {
            update.IsEnabled = false;
        }
    }

    private static bool HasHiddenAncestor(
        PortalEntryLayoutUpdate update,
        IReadOnlyDictionary<int, PortalEntryLayoutUpdate> updatesById)
    {
        var visited = new HashSet<int> { update.PortalEntryId };
        var parentId = update.ParentEntryId;
        while (parentId.HasValue)
        {
            if (!visited.Add(parentId.Value) || !updatesById.TryGetValue(parentId.Value, out var parent))
            {
                return false;
            }

            if (!parent.IsEnabled)
            {
                return true;
            }

            parentId = parent.ParentEntryId;
        }

        return false;
    }

    public async Task<bool> DeleteAsync(int portalEntryId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            const string selectSql = @"
SELECT parent_entry_id,
       entry_key
FROM omp_portal.portal_entries
WHERE portal_entry_id = @portal_entry_id;";

            int? parentEntryId;
            string entryKey;
            await using (var selectCmd = new SqlCommand(selectSql, conn, tx))
            {
                selectCmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryId;
                await using var rdr = await selectCmd.ExecuteReaderAsync(ct);
                if (!await rdr.ReadAsync(ct))
                {
                    await tx.RollbackAsync(ct);
                    return false;
                }

                parentEntryId = rdr.IsDBNull(0) ? null : rdr.GetInt32(0);
                entryKey = rdr.GetString(1);
            }

            if (!entryKey.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            {
                const string disableSql = @"
UPDATE omp_portal.portal_entries
SET is_enabled = 0,
    updated_at = SYSUTCDATETIME()
WHERE portal_entry_id = @portal_entry_id;";

                await using var disableCmd = new SqlCommand(disableSql, conn, tx);
                disableCmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryId;
                await disableCmd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                return true;
            }

            const string reparentSql = @"
UPDATE omp_portal.portal_entries
SET parent_entry_id = @parent_entry_id,
    updated_at = SYSUTCDATETIME()
WHERE parent_entry_id = @portal_entry_id;";

            await using (var reparentCmd = new SqlCommand(reparentSql, conn, tx))
            {
                reparentCmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryId;
                reparentCmd.Parameters.Add("@parent_entry_id", SqlDbType.Int).Value = parentEntryId.HasValue ? parentEntryId.Value : DBNull.Value;
                await reparentCmd.ExecuteNonQueryAsync(ct);
            }

            const string deleteStateSql = "DELETE FROM omp_portal.portal_user_entry_state WHERE portal_entry_id = @portal_entry_id;";
            await using (var stateCmd = new SqlCommand(deleteStateSql, conn, tx))
            {
                stateCmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryId;
                await stateCmd.ExecuteNonQueryAsync(ct);
            }

            const string deleteFavoriteSql = "DELETE FROM omp_portal.user_navigation_favorites WHERE entry_key = @entry_key;";
            await using (var favoriteCmd = new SqlCommand(deleteFavoriteSql, conn, tx))
            {
                favoriteCmd.Parameters.Add("@entry_key", SqlDbType.NVarChar, 200).Value = entryKey;
                await favoriteCmd.ExecuteNonQueryAsync(ct);
            }

            const string deleteSql = "DELETE FROM omp_portal.portal_entries WHERE portal_entry_id = @portal_entry_id;";
            await using (var deleteCmd = new SqlCommand(deleteSql, conn, tx))
            {
                deleteCmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryId;
                await deleteCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SetPinnedAsync(int userId, int portalEntryId, bool isPinned, CancellationToken ct)
    {
        var sortOrder = isPinned
            ? await GetNextPinnedSortOrderAsync(userId, ct)
            : (int?)null;

        const string sql = @"
MERGE omp_portal.portal_user_entry_state AS target
USING (SELECT @user_id AS user_id, @portal_entry_id AS portal_entry_id) AS source
    ON target.user_id = source.user_id
   AND target.portal_entry_id = source.portal_entry_id
WHEN MATCHED THEN
    UPDATE SET is_pinned = @is_pinned,
               sort_order = @sort_order,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(user_id, portal_entry_id, is_pinned, is_hidden, sort_order, updated_at)
    VALUES(@user_id, @portal_entry_id, @is_pinned, 0, @sort_order, SYSUTCDATETIME());";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryId;
        cmd.Parameters.Add("@is_pinned", SqlDbType.Bit).Value = isPinned;
        cmd.Parameters.Add("@sort_order", SqlDbType.Int).Value = sortOrder.HasValue ? sortOrder.Value : DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetHiddenAsync(int userId, int portalEntryId, bool isHidden, CancellationToken ct)
    {
        const string sql = @"
MERGE omp_portal.portal_user_entry_state AS target
USING (SELECT @user_id AS user_id, @portal_entry_id AS portal_entry_id) AS source
    ON target.user_id = source.user_id
   AND target.portal_entry_id = source.portal_entry_id
WHEN MATCHED THEN
    UPDATE SET is_hidden = @is_hidden,
               is_pinned = CASE WHEN @is_hidden = 1 THEN 0 ELSE is_pinned END,
               sort_order = CASE WHEN @is_hidden = 1 THEN NULL ELSE sort_order END,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(user_id, portal_entry_id, is_pinned, is_hidden, sort_order, updated_at)
    VALUES(@user_id, @portal_entry_id, 0, @is_hidden, NULL, SYSUTCDATETIME());";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryId;
        cmd.Parameters.Add("@is_hidden", SqlDbType.Bit).Value = isHidden;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdatePinnedSortOrderAsync(int userId, IReadOnlyList<int> portalEntryIds, CancellationToken ct)
    {
        if (portalEntryIds.Count == 0)
        {
            return;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            const string sql = @"
UPDATE omp_portal.portal_user_entry_state
SET sort_order = @sort_order,
    updated_at = SYSUTCDATETIME()
WHERE user_id = @user_id
  AND portal_entry_id = @portal_entry_id
  AND is_pinned = 1
  AND is_hidden = 0;";

            for (var i = 0; i < portalEntryIds.Count; i++)
            {
                await using var cmd = new SqlCommand(sql, conn, tx);
                cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
                cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryIds[i];
                cmd.Parameters.Add("@sort_order", SqlDbType.Int).Value = (i + 1) * 10;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<int> GetNextPinnedSortOrderAsync(int userId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT COALESCE(MAX(sort_order), 0) + 10
FROM omp_portal.portal_user_entry_state
WHERE user_id = @user_id
  AND is_pinned = 1
  AND is_hidden = 0;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<string>> GetNavigationFavoriteEntryKeysAsync(int userId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
IF OBJECT_ID(N'omp_portal.user_navigation_favorites', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS nvarchar(200)) AS entry_key
    WHERE 1 = 0;
END
ELSE
BEGIN
    SELECT entry_key
    FROM omp_portal.user_navigation_favorites
    WHERE user_id = @user_id
    ORDER BY COALESCE(sort_order, 2147483647),
             created_at,
             entry_key;
END";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<string>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(rdr.GetString(0));
        }

        return rows;
    }

    private async Task<string> CreateUniqueEntryKeyAsync(string displayName, CancellationToken ct)
    {
        var baseKey = "custom:" + ToKeySlug(displayName);
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        for (var i = 0; i < 100; i++)
        {
            var candidate = i == 0
                ? baseKey
                : $"{baseKey}-{i + 1}";

            const string sql = "SELECT COUNT(1) FROM omp_portal.portal_entries WHERE entry_key = @entry_key;";
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@entry_key", SqlDbType.NVarChar, 200).Value = candidate;
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (count == 0)
            {
                return candidate;
            }
        }

        return $"{baseKey}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
    }

    private static void ValidateLayoutUpdates(IReadOnlyList<PortalEntryLayoutUpdate> updates)
    {
        var ids = updates.Select(update => update.PortalEntryId).ToHashSet();
        var parentsById = updates.ToDictionary(update => update.PortalEntryId, update => update.ParentEntryId);

        foreach (var update in updates)
        {
            if (update.ParentEntryId.HasValue && !ids.Contains(update.ParentEntryId.Value))
            {
                throw new InvalidOperationException("Layout parent entry was not found.");
            }

            if (update.ParentEntryId == update.PortalEntryId)
            {
                throw new InvalidOperationException("A Portal Entry cannot be its own parent.");
            }

            var visited = new HashSet<int> { update.PortalEntryId };
            var parentId = update.ParentEntryId;
            while (parentId.HasValue)
            {
                if (!visited.Add(parentId.Value))
                {
                    throw new InvalidOperationException("The layout update would create a Portal Entry cycle.");
                }

                parentId = parentsById.TryGetValue(parentId.Value, out var nextParentId)
                    ? nextParentId
                    : null;
            }
        }
    }

    private static string ToKeySlug(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        var slug = EntryKeyUnsafeCharacters.Replace(builder.ToString().Normalize(NormalizationForm.FormC), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "portal-entry" : slug[..Math.Min(slug.Length, 180)];
    }

    private async Task<IReadOnlyList<PortalEntryRow>> GetRowsAsync(int? userId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT pe.portal_entry_id,
       pe.parent_entry_id,
       pe.entry_key,
       pe.display_name,
       pe.description,
       pe.logo_url,
       pe.icon_key,
       pe.target_url,
       pe.target_entry_key,
       pe.source_app_instance_id,
       pe.is_enabled,
       pe.default_sort_order,
       COALESCE(us.is_pinned, CAST(0 AS bit)) AS is_pinned,
       COALESCE(us.is_hidden, CAST(0 AS bit)) AS is_hidden,
       us.sort_order
FROM omp_portal.portal_entries pe
LEFT JOIN omp_portal.portal_user_entry_state us
    ON us.portal_entry_id = pe.portal_entry_id
   AND us.user_id = @user_id
ORDER BY pe.default_sort_order,
         pe.display_name;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId.HasValue ? userId.Value : DBNull.Value;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<PortalEntryRow>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new PortalEntryRow
            {
                PortalEntryId = rdr.GetInt32(0),
                ParentEntryId = rdr.IsDBNull(1) ? null : rdr.GetInt32(1),
                EntryKey = rdr.GetString(2),
                DisplayName = rdr.GetString(3),
                Description = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                LogoUrl = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                IconKey = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                TargetUrl = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                TargetEntryKey = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                SourceAppInstanceId = rdr.IsDBNull(9) ? null : rdr.GetGuid(9),
                IsEnabled = rdr.GetBoolean(10),
                DefaultSortOrder = rdr.GetInt32(11),
                IsPinned = rdr.GetBoolean(12),
                IsHidden = rdr.GetBoolean(13),
                SortOrder = rdr.IsDBNull(14) ? null : rdr.GetInt32(14)
            });
        }

        return rows;
    }

    private static string? ResolveTargetHref(
        HttpRequest request,
        PortalEntryRow row,
        IReadOnlyDictionary<Guid, PortalAppEntry> accessibleApps)
    {
        if (row.SourceAppInstanceId is Guid sourceAppInstanceId
            && accessibleApps.TryGetValue(sourceAppInstanceId, out var sourceApp))
        {
            return AppLinkBuilder.ResolveHref(request, sourceApp);
        }

        if (TryParseAppInstanceId(row.TargetEntryKey, out var targetAppInstanceId)
            && accessibleApps.TryGetValue(targetAppInstanceId, out var targetApp))
        {
            return AppLinkBuilder.ResolveHref(request, targetApp);
        }

        return ResolveTargetUrl(request, row.TargetUrl);
    }

    private static string? ResolveTargetUrl(HttpRequest request, string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return null;
        }

        var trimmed = targetUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var publicRoot = request.GetPublicBaseUrl().TrimEnd('/');
        return $"{publicRoot}/{trimmed.TrimStart('/')}";
    }

    private static string? BuildContextName(
        PortalEntryRow row,
        IReadOnlyDictionary<int, PortalEntryRow> rowsById)
    {
        if (!row.ParentEntryId.HasValue)
        {
            return null;
        }

        var visited = new HashSet<int> { row.PortalEntryId };
        var contextNames = new Stack<string>();
        var parentId = row.ParentEntryId;
        while (parentId.HasValue
               && rowsById.TryGetValue(parentId.Value, out var parent)
               && visited.Add(parent.PortalEntryId))
        {
            if (!string.IsNullOrWhiteSpace(parent.DisplayName))
            {
                contextNames.Push(parent.DisplayName);
            }

            parentId = parent.ParentEntryId;
        }

        return contextNames.Count == 0
            ? null
            : string.Join(" / ", contextNames);
    }

    private static bool CanAccessTargetHref(
        HttpRequest request,
        string href,
        IReadOnlyList<PortalAppEntry> apps,
        IReadOnlyDictionary<Guid, PortalAppEntry> accessibleApps,
        IReadOnlySet<string> permissions)
    {
        var path = href;
        if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
        {
            path = absoluteUri.AbsolutePath;
        }

        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
        {
            return true;
        }

        var normalizedPath = NormalizeAbsolutePath(path);
        if (IsPathMatch(normalizedPath, "/admin"))
        {
            return permissions.Contains(OmpPortalPermissions.Admin);
        }

        var app = FindBestMatchingApp(request, apps, normalizedPath);
        if (app is null)
        {
            return true;
        }

        if (!accessibleApps.ContainsKey(app.AppInstanceId))
        {
            return false;
        }

        var appRelativePath = GetAppRelativePath(request, normalizedPath, app);
        return !RequiresElevatedAccess(appRelativePath)
            || HasInferredAdminAccess(app, permissions);
    }

    private static PortalAppEntry? FindBestMatchingApp(
        HttpRequest request,
        IReadOnlyList<PortalAppEntry> apps,
        string normalizedPath)
        => apps
            .Select(app => new
            {
                App = app,
                Path = NormalizeAbsolutePath(AppLinkBuilder.ResolveHref(request, app))
            })
            .Where(item => IsPathMatch(normalizedPath, item.Path))
            .OrderByDescending(item => item.Path.Length)
            .Select(item => item.App)
            .FirstOrDefault();

    private static string GetAppRelativePath(HttpRequest request, string normalizedPath, PortalAppEntry app)
    {
        var appPath = NormalizeAbsolutePath(AppLinkBuilder.ResolveHref(request, app));
        if (!IsPathMatch(normalizedPath, appPath))
        {
            return "/";
        }

        return normalizedPath.Length == appPath.Length
            ? "/"
            : normalizedPath[appPath.Length..].TrimStart('/');
    }

    private static bool RequiresElevatedAccess(string appRelativePath)
    {
        var normalized = NormalizeAbsolutePath(appRelativePath);
        return IsPathMatch(normalized, "/admin")
            || IsPathMatch(normalized, "/create")
            || normalized.Contains("/admin/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasInferredAdminAccess(PortalAppEntry app, IReadOnlySet<string> permissions)
    {
        var candidatePermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var permission in app.RequiredPermissions)
        {
            if (permission.EndsWith(".View", StringComparison.OrdinalIgnoreCase))
            {
                candidatePermissions.Add($"{permission[..^5]}.Admin");
            }
        }

        candidatePermissions.Add($"{app.AppKey}.Admin");
        candidatePermissions.Add($"{app.AppInstanceKey}.Admin");

        return candidatePermissions.Any(permissions.Contains);
    }

    private static string NormalizeAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out var absoluteUri))
        {
            path = absoluteUri.AbsolutePath;
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed.TrimEnd('/').ToLowerInvariant() switch
        {
            "" => "/",
            var value => value
        };
    }

    private static bool IsPathMatch(string normalizedPath, string normalizedPrefix)
    {
        normalizedPrefix = NormalizeAbsolutePath(normalizedPrefix);
        if (string.Equals(normalizedPrefix, "/", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath.StartsWith("/", StringComparison.Ordinal);
        }

        return string.Equals(normalizedPath, normalizedPrefix, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith($"{normalizedPrefix}/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseAppInstanceId(string? targetEntryKey, out Guid appInstanceId)
    {
        appInstanceId = default;
        if (string.IsNullOrWhiteSpace(targetEntryKey)
            || !targetEntryKey.StartsWith(AppEntryPrefix, StringComparison.OrdinalIgnoreCase)
            || !targetEntryKey.EndsWith(AppEntrySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var idText = targetEntryKey[AppEntryPrefix.Length..^AppEntrySuffix.Length];
        return Guid.TryParseExact(idText, "N", out appInstanceId);
    }

    private sealed class PortalEntryRow
    {
        public int PortalEntryId { get; set; }

        public int? ParentEntryId { get; set; }

        public string EntryKey { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string? LogoUrl { get; set; }

        public string? IconKey { get; set; }

        public string? TargetUrl { get; set; }

        public string? TargetEntryKey { get; set; }

        public Guid? SourceAppInstanceId { get; set; }

        public bool IsEnabled { get; set; }

        public int DefaultSortOrder { get; set; }

        public bool IsPinned { get; set; }

        public bool IsHidden { get; set; }

        public int? SortOrder { get; set; }
    }
}
