// File: OpenModulePlatform.Portal/Services/PortalDashboardService.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;
using System.Globalization;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Loads and persists the first user-customizable Portal dashboard.
/// </summary>
public sealed class PortalDashboardService
{
    private const string ContentManagePermission = "ContentWebAppModule.Manage";
    private const string ContentAppKey = "content_webapp_webapp";
    private const bool DefaultAlignToGrid = true;
    private const bool DefaultExpandedCanvas = true;
    private const int DefaultWidgetWidth = 320;
    private const int DefaultWidgetHeight = 192;
    private const int MinWidgetWidth = 160;
    private const int MinWidgetHeight = 96;
    private const int MaxWidgetWidth = 1800;
    private const int MaxWidgetHeight = 1400;
    private const int MaxWidgetOffset = 10000;
    private const int MaxWidgetOrder = 10000;

    private readonly SqlConnectionFactory _db;
    private readonly AppCatalogService _catalog;

    public PortalDashboardService(SqlConnectionFactory db, AppCatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<DashboardPreferences> GetPreferencesAsync(int userId, CancellationToken ct)
    {
        const string sql = @"
SELECT align_to_grid,
       expanded_canvas
FROM omp_portal.user_dashboard_preferences
WHERE user_id = @user_id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return new DashboardPreferences(DefaultAlignToGrid, DefaultExpandedCanvas);
        }

        return new DashboardPreferences(
            rdr.IsDBNull(0) ? DefaultAlignToGrid : Convert.ToBoolean(rdr.GetValue(0), CultureInfo.InvariantCulture),
            rdr.IsDBNull(1) ? DefaultExpandedCanvas : Convert.ToBoolean(rdr.GetValue(1), CultureInfo.InvariantCulture));
    }

    public async Task SetPreferencesAsync(int userId, bool alignToGrid, bool expandedCanvas, CancellationToken ct)
    {
        const string sql = @"
MERGE omp_portal.user_dashboard_preferences AS target
USING (SELECT @user_id AS user_id) AS source
ON target.user_id = source.user_id
WHEN MATCHED THEN
    UPDATE SET align_to_grid = @align_to_grid,
               expanded_canvas = @expanded_canvas,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(user_id, align_to_grid, expanded_canvas, updated_at)
    VALUES(@user_id, @align_to_grid, @expanded_canvas, SYSUTCDATETIME());";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@align_to_grid", SqlDbType.Bit).Value = alignToGrid;
        cmd.Parameters.Add("@expanded_canvas", SqlDbType.Bit).Value = expandedCanvas;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DashboardActiveWidget>> GetActiveWidgetsAsync(
        int userId,
        IReadOnlySet<int> roleIds,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        var definitions = await GetAccessibleDefinitionMapAsync(roleIds, permissions, ct);
        if (definitions.Count == 0)
        {
            return [];
        }

        const string sql = @"
SELECT uaw.user_active_widget_id,
       uaw.widget_id,
       uaw.offset_top,
       uaw.offset_left,
       uaw.width,
       uaw.height,
       uaw.order_priority,
       uaw.title,
       uaw.int_data,
       uaw.string_data
FROM omp_portal.user_active_widgets uaw
WHERE uaw.user_id = @user_id
ORDER BY uaw.order_priority,
         uaw.user_active_widget_id;";

        var widgets = new List<DashboardActiveWidget>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var widgetId = rdr.GetInt32(1);
            if (!definitions.TryGetValue(widgetId, out var definition))
            {
                continue;
            }

            widgets.Add(new DashboardActiveWidget
            {
                UserActiveWidgetId = rdr.GetInt64(0),
                WidgetId = widgetId,
                WidgetTitle = definition.Title,
                WidgetType = definition.WidgetType,
                Payload = definition.Payload,
                OffsetTop = rdr.GetInt32(2),
                OffsetLeft = rdr.GetInt32(3),
                Width = rdr.GetInt32(4),
                Height = rdr.GetInt32(5),
                OrderPriority = rdr.GetInt32(6),
                Title = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                IntData = rdr.IsDBNull(8) ? null : rdr.GetInt32(8),
                StringData = rdr.IsDBNull(9) ? null : rdr.GetString(9)
            });
        }

        return widgets;
    }

    public async Task<IReadOnlyList<DashboardWidgetDefinition>> GetAvailableWidgetsAsync(
        IReadOnlySet<int> roleIds,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        var definitions = await GetAccessibleDefinitionMapAsync(roleIds, permissions, ct);
        return definitions.Values
            .OrderBy(widget => widget.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<DashboardContentPageLink>> GetReadableContentPagesAsync(
        HttpRequest request,
        IReadOnlySet<int> roleIds,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        var allApps = await _catalog.GetEnabledWebAppsAsync(ct);
        var contentApps = _catalog.FilterByPermissions(allApps, permissions)
            .Where(app => string.Equals(app.AppKey, ContentAppKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (contentApps.Length == 0)
        {
            return [];
        }

        var canManageAll = permissions.Contains(ContentManagePermission);
        if (!canManageAll && roleIds.Count == 0)
        {
            return [];
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        if (!await TableExistsAsync(conn, "omp_content.contents", ct)
            || !await TableExistsAsync(conn, "omp_content.content_role_access", ct))
        {
            return [];
        }

        var appParameters = contentApps
            .Select((app, index) => new { App = app, Name = $"@app{index}" })
            .ToArray();
        var roleParameters = roleIds
            .Select((roleId, index) => new { RoleId = roleId, Name = $"@role{index}" })
            .ToArray();
        var accessFilter = canManageAll
            ? string.Empty
            : $@"
  AND EXISTS
  (
      SELECT 1
      FROM omp_content.content_role_access a
      WHERE a.content_id = c.content_id
        AND a.role_id IN ({string.Join(", ", roleParameters.Select(item => item.Name))})
        AND a.can_read = 1
  )";

        var sql = $@"
SELECT c.content_id,
       c.app_instance_id,
       c.slug,
       c.title,
       c.content_type
FROM omp_content.contents c
WHERE c.app_instance_id IN ({string.Join(", ", appParameters.Select(item => item.Name))})
  AND c.is_enabled = 1
{accessFilter}
ORDER BY COALESCE(c.sort_order, 2147483647),
         c.title,
         c.slug;";

        var appsById = contentApps.ToDictionary(app => app.AppInstanceId);
        var pages = new List<DashboardContentPageLink>();
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var item in appParameters)
        {
            cmd.Parameters.Add(item.Name, SqlDbType.UniqueIdentifier).Value = item.App.AppInstanceId;
        }

        foreach (var item in roleParameters)
        {
            cmd.Parameters.Add(item.Name, SqlDbType.Int).Value = item.RoleId;
        }

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var appInstanceId = rdr.GetGuid(1);
            if (!appsById.TryGetValue(appInstanceId, out var app))
            {
                continue;
            }

            var slug = rdr.GetString(2);
            var href = BuildContentPageHref(request, app, slug);
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            pages.Add(new DashboardContentPageLink(
                rdr.GetGuid(0),
                rdr.GetString(3),
                slug,
                href,
                contentApps.Length > 1 ? app.DisplayName : null,
                rdr.GetString(4)));
        }

        return pages;
    }

    public async Task<DashboardActiveWidget?> CreateWidgetDraftAsync(
        int widgetId,
        IReadOnlySet<int> roleIds,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        var definitions = await GetAccessibleDefinitionMapAsync(roleIds, permissions, ct);
        if (!definitions.TryGetValue(widgetId, out var definition))
        {
            return null;
        }
        var defaultWidth = GetDefaultWidgetWidth(definition);
        var defaultHeight = GetDefaultWidgetHeight(definition);

        return new DashboardActiveWidget
        {
            UserActiveWidgetId = 0,
            WidgetId = widgetId,
            WidgetTitle = definition.Title,
            WidgetType = definition.WidgetType,
            Payload = definition.Payload,
            OffsetTop = 32,
            OffsetLeft = 32,
            Width = defaultWidth,
            Height = defaultHeight,
            OrderPriority = 10
        };
    }

    public async Task<DashboardWidgetSaveResult> SaveLayoutAsync(
        int userId,
        IReadOnlyList<DashboardWidgetLayoutUpdate> updates,
        IReadOnlySet<int> roleIds,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        if (updates.Count == 0)
        {
            return new DashboardWidgetSaveResult([]);
        }

        var hasDraftWidgets = updates.Any(update => update.UserActiveWidgetId <= 0);
        var accessibleDefinitions = hasDraftWidgets
            ? await GetAccessibleDefinitionMapAsync(roleIds, permissions, ct)
            : new Dictionary<int, DashboardWidgetDefinition>();
        var createdWidgets = new List<DashboardWidgetSaveCreatedItem>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            const string sql = @"
UPDATE omp_portal.user_active_widgets
SET offset_top = @offset_top,
    offset_left = @offset_left,
    width = @width,
    height = @height,
    order_priority = @order_priority,
    title = @title,
    int_data = @int_data,
    string_data = @string_data
WHERE user_active_widget_id = @user_active_widget_id
  AND user_id = @user_id;";

            foreach (var update in updates.GroupBy(item => item.UserActiveWidgetId).Select(group => group.Last()))
            {
                if (update.UserActiveWidgetId > 0)
                {
                    await using var cmd = new SqlCommand(sql, conn, tx);
                    BindLayoutParameters(cmd, userId, update);
                    await cmd.ExecuteNonQueryAsync(ct);
                    continue;
                }

                if (update.WidgetId <= 0 || !accessibleDefinitions.ContainsKey(update.WidgetId))
                {
                    continue;
                }

                const string insertSql = @"
INSERT INTO omp_portal.user_active_widgets
(
    widget_id,
    user_id,
    offset_top,
    offset_left,
    width,
    height,
    order_priority,
    title,
    int_data,
    string_data
)
OUTPUT INSERTED.user_active_widget_id
VALUES
(
    @widget_id,
    @user_id,
    @offset_top,
    @offset_left,
    @width,
    @height,
    @order_priority,
    @title,
    @int_data,
    @string_data
);";

                await using var insertCmd = new SqlCommand(insertSql, conn, tx);
                insertCmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = update.WidgetId;
                BindLayoutParameters(insertCmd, userId, update);
                var userActiveWidgetId = Convert.ToInt64(await insertCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
                createdWidgets.Add(new DashboardWidgetSaveCreatedItem(update.UserActiveWidgetId, userActiveWidgetId));
            }

            await tx.CommitAsync(ct);
            return new DashboardWidgetSaveResult(createdWidgets);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task RemoveWidgetAsync(int userId, long userActiveWidgetId, CancellationToken ct)
    {
        const string sql = @"
DELETE awd
FROM omp_portal.user_active_widget_data awd
INNER JOIN omp_portal.user_active_widgets aw
    ON aw.user_active_widget_id = awd.user_active_widget_id
WHERE aw.user_id = @user_id
  AND aw.user_active_widget_id = @user_active_widget_id;

DELETE FROM omp_portal.user_active_widgets
WHERE user_id = @user_id
  AND user_active_widget_id = @user_active_widget_id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@user_active_widget_id", SqlDbType.BigInt).Value = userActiveWidgetId;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ResetDashboardAsync(int userId, CancellationToken ct)
    {
        const string sql = @"
DELETE awd
FROM omp_portal.user_active_widget_data awd
INNER JOIN omp_portal.user_active_widgets aw
    ON aw.user_active_widget_id = awd.user_active_widget_id
WHERE aw.user_id = @user_id;

DELETE FROM omp_portal.user_active_widgets
WHERE user_id = @user_id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<Dictionary<int, DashboardWidgetDefinition>> GetAccessibleDefinitionMapAsync(
        IReadOnlySet<int> roleIds,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        const string sql = @"
SELECT w.widget_id,
       w.widget_key,
       w.title,
       w.widget_type,
       w.payload,
       w.module_key,
       w.author,
       w.modified_at,
       wp.role_id,
       p.Name AS permission_name
FROM omp_portal.widgets w
LEFT JOIN omp_portal.widget_permissions wp ON wp.widget_id = w.widget_id
LEFT JOIN omp.Permissions p ON p.PermissionId = wp.permission_id
ORDER BY w.title,
         w.widget_id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var definitions = new Dictionary<int, DashboardWidgetDefinition>();
        var restrictions = new Dictionary<int, List<WidgetAccessRule>>();
        while (await rdr.ReadAsync(ct))
        {
            var widgetId = rdr.GetInt32(0);
            if (!definitions.ContainsKey(widgetId))
            {
                definitions[widgetId] = new DashboardWidgetDefinition
                {
                    WidgetId = widgetId,
                    WidgetKey = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1),
                    Title = rdr.GetString(2),
                    WidgetType = rdr.GetString(3),
                    Payload = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    ModuleKey = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    Author = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    ModifiedUtc = rdr.GetDateTime(7)
                };
            }

            if (!rdr.IsDBNull(8) || !rdr.IsDBNull(9))
            {
                if (!restrictions.TryGetValue(widgetId, out var rules))
                {
                    rules = [];
                    restrictions[widgetId] = rules;
                }

                rules.Add(new WidgetAccessRule(
                    rdr.IsDBNull(8) ? null : rdr.GetInt32(8),
                    rdr.IsDBNull(9) ? null : rdr.GetString(9)));
            }
        }

        var inaccessibleWidgetIds = definitions.Values
            .Where(definition => !CanAccessWidget(definition.WidgetId, restrictions, roleIds, permissions))
            .Select(definition => definition.WidgetId)
            .ToArray();

        foreach (var widgetId in inaccessibleWidgetIds)
        {
            definitions.Remove(widgetId);
        }

        return definitions;
    }

    private static bool CanAccessWidget(
        int widgetId,
        IReadOnlyDictionary<int, List<WidgetAccessRule>> restrictions,
        IReadOnlySet<int> roleIds,
        IReadOnlySet<string> permissions)
    {
        if (!restrictions.TryGetValue(widgetId, out var rules) || rules.Count == 0)
        {
            return true;
        }

        return rules.Any(rule =>
            (rule.RoleId.HasValue && roleIds.Contains(rule.RoleId.Value))
            || (!string.IsNullOrWhiteSpace(rule.PermissionName) && permissions.Contains(rule.PermissionName)));
    }

    private static string? CleanTitle(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    private static string? CleanStringData(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.Length > 20 ? trimmed[..20] : trimmed;
    }

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    private static async Task<bool> TableExistsAsync(SqlConnection conn, string objectName, CancellationToken ct)
    {
        const string sql = "SELECT CAST(CASE WHEN OBJECT_ID(@object_name, N'U') IS NULL THEN 0 ELSE 1 END AS bit);";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@object_name", SqlDbType.NVarChar, 256).Value = objectName;
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static string? BuildContentPageHref(HttpRequest request, PortalAppEntry app, string slug)
    {
        var appHref = AppLinkBuilder.ResolveHref(request, app);
        if (string.IsNullOrWhiteSpace(appHref))
        {
            return null;
        }

        var normalizedSlug = slug.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            return appHref;
        }

        var escapedSlug = string.Join(
            "/",
            normalizedSlug.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));
        return $"{appHref.TrimEnd('/')}/{escapedSlug}";
    }

    private static void BindLayoutParameters(SqlCommand cmd, int userId, DashboardWidgetLayoutUpdate update)
    {
        cmd.Parameters.Add("@user_active_widget_id", SqlDbType.BigInt).Value = update.UserActiveWidgetId;
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@offset_top", SqlDbType.Int).Value = Clamp(update.OffsetTop, 0, MaxWidgetOffset);
        cmd.Parameters.Add("@offset_left", SqlDbType.Int).Value = Clamp(update.OffsetLeft, 0, MaxWidgetOffset);
        cmd.Parameters.Add("@width", SqlDbType.Int).Value = Clamp(update.Width, MinWidgetWidth, MaxWidgetWidth);
        cmd.Parameters.Add("@height", SqlDbType.Int).Value = Clamp(update.Height, MinWidgetHeight, MaxWidgetHeight);
        cmd.Parameters.Add("@order_priority", SqlDbType.Int).Value = Clamp(update.OrderPriority, 0, MaxWidgetOrder);
        cmd.Parameters.Add("@title", SqlDbType.NVarChar, 200).Value = CleanTitle(update.Title) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@int_data", SqlDbType.Int).Value = update.IntData.HasValue ? update.IntData.Value : DBNull.Value;
        cmd.Parameters.Add("@string_data", SqlDbType.NVarChar, 20).Value = CleanStringData(update.StringData) ?? (object)DBNull.Value;
    }

    private static int GetDefaultWidgetWidth(DashboardWidgetDefinition definition)
        => definition.Payload switch
        {
            "admin-overview" => 768,
            "portal-entry-favorites" or "portal-entry-list" or "portal-entry-combolist" or "portal-navbar-links" => 416,
            "content-pages" => 416,
            "user-roles" => 384,
            "music-player" => 384,
            "weekday-date" => 288,
            _ => DefaultWidgetWidth
        };

    private static int GetDefaultWidgetHeight(DashboardWidgetDefinition definition)
        => definition.Payload switch
        {
            "admin-overview" => 384,
            "portal-entry-favorites" or "portal-entry-list" or "portal-entry-combolist" or "portal-navbar-links" => 384,
            "content-pages" => 384,
            "user-roles" => 320,
            "music-player" => 256,
            "weekday-date" => 160,
            _ => DefaultWidgetHeight
        };

    private readonly record struct WidgetAccessRule(int? RoleId, string? PermissionName);
}
