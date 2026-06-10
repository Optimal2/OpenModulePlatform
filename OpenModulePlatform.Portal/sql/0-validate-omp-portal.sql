SET NOCOUNT ON;

DECLARE @Missing int = 0;

;WITH RequiredTables(SchemaName, TableName) AS
(
    SELECT v.SchemaName, v.TableName
    FROM (VALUES
        (N'omp_portal', N'user_setting_definitions'),
        (N'omp_portal', N'user_setting_int_values'),
        (N'omp_portal', N'user_setting_string_values'),
        (N'omp_portal', N'schema_migrations'),
        (N'omp_portal', N'user_navigation_favorites'),
        (N'omp_portal', N'portal_entries'),
        (N'omp_portal', N'portal_user_entry_state'),
        (N'omp_portal', N'widgets'),
        (N'omp_portal', N'widget_permissions'),
        (N'omp_portal', N'user_active_widgets'),
        (N'omp_portal', N'user_active_widget_data'),
        (N'omp_portal', N'widget_data'),
        (N'omp_portal', N'widget_binary_data'),
        (N'omp_portal', N'user_dashboard_preferences')
    ) AS v(SchemaName, TableName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredTables required
WHERE OBJECT_ID(required.SchemaName + N'.' + required.TableName, N'U') IS NULL;

;WITH RequiredColumns(SchemaName, TableName, ColumnName) AS
(
    SELECT v.SchemaName, v.TableName, v.ColumnName
    FROM (VALUES
        (N'omp_portal', N'user_setting_definitions', N'setting_category'),
        (N'omp_portal', N'user_setting_definitions', N'setting_name'),
        (N'omp_portal', N'user_setting_definitions', N'value_kind'),
        (N'omp_portal', N'schema_migrations', N'migration_key'),
        (N'omp_portal', N'portal_entries', N'entry_key'),
        (N'omp_portal', N'portal_entries', N'parent_entry_id'),
        (N'omp_portal', N'portal_entries', N'target_url'),
        (N'omp_portal', N'portal_entries', N'is_enabled'),
        (N'omp_portal', N'widgets', N'widget_key'),
        (N'omp_portal', N'widgets', N'module_key'),
        (N'omp_portal', N'widgets', N'title'),
        (N'omp_portal', N'widgets', N'description'),
        (N'omp_portal', N'widgets', N'widget_type'),
        (N'omp_portal', N'widgets', N'payload'),
        (N'omp_portal', N'widgets', N'widget_version'),
        (N'omp_portal', N'widgets', N'is_enabled'),
        (N'omp_portal', N'widget_permissions', N'widget_id'),
        (N'omp_portal', N'widget_permissions', N'permission_id'),
        (N'omp_portal', N'widget_permissions', N'role_id'),
        (N'omp_portal', N'user_active_widgets', N'user_id'),
        (N'omp_portal', N'user_active_widgets', N'widget_id'),
        (N'omp_portal', N'user_active_widgets', N'width'),
        (N'omp_portal', N'user_active_widgets', N'height'),
        (N'omp_portal', N'user_active_widgets', N'content_scale'),
        (N'omp_portal', N'user_active_widgets', N'hide_titlebar_when_viewing'),
        (N'omp_portal', N'widget_data', N'widget_id'),
        (N'omp_portal', N'widget_data', N'data_key'),
        (N'omp_portal', N'widget_data', N'json_data'),
        (N'omp_portal', N'widget_binary_data', N'binary_data_id'),
        (N'omp_portal', N'widget_binary_data', N'owner_ref'),
        (N'omp_portal', N'widget_binary_data', N'file_name'),
        (N'omp_portal', N'widget_binary_data', N'content_type'),
        (N'omp_portal', N'widget_binary_data', N'content_length'),
        (N'omp_portal', N'widget_binary_data', N'content_hash'),
        (N'omp_portal', N'widget_binary_data', N'data_value'),
        (N'omp_portal', N'widget_binary_data', N'is_enabled'),
        (N'omp_portal', N'user_dashboard_preferences', N'user_id'),
        (N'omp_portal', N'user_dashboard_preferences', N'align_to_grid'),
        (N'omp_portal', N'user_dashboard_preferences', N'expanded_canvas'),
        (N'omp_portal', N'user_dashboard_preferences', N'has_custom_dashboard_layout')
    ) AS v(SchemaName, TableName, ColumnName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredColumns required
WHERE COL_LENGTH(required.SchemaName + N'.' + required.TableName, required.ColumnName) IS NULL;

;WITH RequiredIndexes(ObjectName, IndexName) AS
(
    SELECT v.ObjectName, v.IndexName
    FROM (VALUES
        (N'omp_portal.portal_entries', N'UQ_omp_portal_portal_entries_entry_key'),
        (N'omp_portal.portal_entries', N'IX_omp_portal_portal_entries_parent_sort'),
        (N'omp_portal.widgets', N'UX_omp_portal_widgets_widget_key'),
        (N'omp_portal.widget_permissions', N'IX_omp_portal_widget_permissions_widget'),
        (N'omp_portal.user_active_widgets', N'IX_omp_portal_user_active_widgets_user_order'),
        (N'omp_portal.widget_binary_data', N'IX_omp_portal_widget_binary_data_owner'),
        (N'omp_portal.widget_binary_data', N'IX_omp_portal_widget_binary_data_hash')
    ) AS v(ObjectName, IndexName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredIndexes required
WHERE NOT EXISTS
(
    SELECT 1
    FROM sys.indexes idx
    WHERE idx.object_id = OBJECT_ID(required.ObjectName)
      AND idx.name = required.IndexName
);

IF OBJECT_ID(N'omp_portal.user_setting_definitions', N'U') IS NOT NULL
BEGIN
    SELECT @Missing = @Missing + CASE
        WHEN EXISTS (SELECT 1 FROM omp_portal.user_setting_definitions WHERE setting_category = N'Portal' AND setting_name = N'TopbarDropdownsOpenOnHover')
         AND EXISTS (SELECT 1 FROM omp_portal.user_setting_definitions WHERE setting_category = N'Portal' AND setting_name = N'ShowPortalNavbar')
         AND EXISTS (SELECT 1 FROM omp_portal.user_setting_definitions WHERE setting_category = N'Portal' AND setting_name = N'NotificationToastsMuted')
        THEN 0 ELSE 1 END;
END;

IF OBJECT_ID(N'omp_portal.portal_entries', N'U') IS NOT NULL
BEGIN
    SELECT @Missing = @Missing + CASE
        WHEN EXISTS (SELECT 1 FROM omp_portal.portal_entries WHERE entry_key = N'portal:home')
         AND EXISTS (SELECT 1 FROM omp_portal.portal_entries WHERE entry_key = N'portal:admin')
         AND EXISTS (SELECT 1 FROM omp_portal.portal_entries WHERE entry_key = N'portal:admin-module-packages')
        THEN 0 ELSE 1 END;
END;

IF OBJECT_ID(N'omp_portal.widgets', N'U') IS NOT NULL
BEGIN
    SELECT @Missing = @Missing + CASE
        WHEN EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'blank-rectangle')
         AND EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'admin-overview')
         AND EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'portal-entry-favorites')
         AND EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'portal-entry-list')
         AND EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'portal-entry-combolist')
         AND EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'portal-navbar-links')
         AND EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'user-roles')
         AND EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'content-pages')
         AND EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'notification-feed')
         AND EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'message-conversations')
         AND EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'weekday-date')
         AND EXISTS (SELECT 1 FROM omp_portal.widgets WHERE widget_key = N'music-player')
        THEN 0 ELSE 1 END;
END;

SELECT
    CAST(CASE WHEN @Missing = 0 THEN 1 ELSE 0 END AS bit) AS IsHealthy,
    CASE
        WHEN @Missing = 0 THEN N'Portal module storage is healthy.'
        ELSE CONCAT(N'Portal module storage is missing ', @Missing, N' required object(s).')
    END AS Message;
