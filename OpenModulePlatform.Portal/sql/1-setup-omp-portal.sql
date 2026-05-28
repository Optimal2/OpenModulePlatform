-- File: OpenModulePlatform.Portal/sql/1-setup-omp-portal.sql
/*
Creates the OMP Portal schema.

Prerequisite:
- Run ../../sql/1-setup-openmoduleplatform.sql first.
*/
USE [OpenModulePlatform];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_portal')
    EXEC('CREATE SCHEMA [omp_portal]');
GO

IF OBJECT_ID(N'omp_portal.user_setting_definitions', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.user_setting_definitions
    (
        user_setting_definition_id int IDENTITY(1,1) NOT NULL,
        setting_category nvarchar(100) NOT NULL,
        setting_name nvarchar(200) NOT NULL,
        -- 1 = int-backed value, 2 = string-backed value. User value rows are
        -- split by type so high-volume numeric settings do not pay nvarchar(max)
        -- storage overhead.
        value_kind tinyint NOT NULL,
        default_int_value int NULL,
        default_string_value nvarchar(max) NULL,
        description nvarchar(1000) NULL,
        sort_order int NOT NULL CONSTRAINT DF_omp_portal_user_setting_definitions_sort_order DEFAULT(0),
        is_enabled bit NOT NULL CONSTRAINT DF_omp_portal_user_setting_definitions_is_enabled DEFAULT(1),
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_user_setting_definitions_created_at DEFAULT(SYSUTCDATETIME()),
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_user_setting_definitions_updated_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_user_setting_definitions PRIMARY KEY(user_setting_definition_id),
        CONSTRAINT UQ_omp_portal_user_setting_definitions_key UNIQUE(setting_category, setting_name),
        CONSTRAINT UQ_omp_portal_user_setting_definitions_id_kind UNIQUE(user_setting_definition_id, value_kind),
        CONSTRAINT CK_omp_portal_user_setting_definitions_value_kind CHECK(value_kind IN (1, 2)),
        CONSTRAINT CK_omp_portal_user_setting_definitions_default_value CHECK
        (
            (value_kind = 1 AND default_string_value IS NULL)
            OR (value_kind = 2 AND default_int_value IS NULL)
        )
    );
END
GO

IF OBJECT_ID(N'omp_portal.user_setting_int_values', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.user_setting_int_values
    (
        user_id int NOT NULL,
        user_setting_definition_id int NOT NULL,
        value_kind tinyint NOT NULL CONSTRAINT DF_omp_portal_user_setting_int_values_value_kind DEFAULT(1),
        setting_value int NOT NULL,
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_user_setting_int_values_updated_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_user_setting_int_values PRIMARY KEY(user_id, user_setting_definition_id),
        CONSTRAINT CK_omp_portal_user_setting_int_values_value_kind CHECK(value_kind = 1),
        CONSTRAINT FK_omp_portal_user_setting_int_values_user FOREIGN KEY(user_id)
            REFERENCES omp.users(user_id),
        CONSTRAINT FK_omp_portal_user_setting_int_values_definition FOREIGN KEY(user_setting_definition_id, value_kind)
            REFERENCES omp_portal.user_setting_definitions(user_setting_definition_id, value_kind)
    );
END
GO

IF OBJECT_ID(N'omp_portal.user_setting_string_values', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.user_setting_string_values
    (
        user_id int NOT NULL,
        user_setting_definition_id int NOT NULL,
        value_kind tinyint NOT NULL CONSTRAINT DF_omp_portal_user_setting_string_values_value_kind DEFAULT(2),
        setting_value nvarchar(max) NOT NULL,
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_user_setting_string_values_updated_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_user_setting_string_values PRIMARY KEY(user_id, user_setting_definition_id),
        CONSTRAINT CK_omp_portal_user_setting_string_values_value_kind CHECK(value_kind = 2),
        CONSTRAINT FK_omp_portal_user_setting_string_values_user FOREIGN KEY(user_id)
            REFERENCES omp.users(user_id),
        CONSTRAINT FK_omp_portal_user_setting_string_values_definition FOREIGN KEY(user_setting_definition_id, value_kind)
            REFERENCES omp_portal.user_setting_definitions(user_setting_definition_id, value_kind)
    );
END
GO

MERGE omp_portal.user_setting_definitions AS target
USING
(
    SELECT N'Portal' AS setting_category,
           N'AdminMetricsCollapsed' AS setting_name,
           CAST(1 AS tinyint) AS value_kind,
           CAST(0 AS int) AS default_int_value,
           CAST(NULL AS nvarchar(max)) AS default_string_value,
           N'Controls whether the Portal admin metrics panel starts collapsed for the signed-in user.' AS description,
           10 AS sort_order,
           CAST(1 AS bit) AS is_enabled
    UNION ALL
    SELECT N'Portal' AS setting_category,
           N'TopbarDropdownsOpenOnHover' AS setting_name,
           CAST(1 AS tinyint) AS value_kind,
           CAST(1 AS int) AS default_int_value,
           CAST(NULL AS nvarchar(max)) AS default_string_value,
           N'Controls whether top bar dropdown menus open on hover for the signed-in user.' AS description,
           20 AS sort_order,
           CAST(1 AS bit) AS is_enabled
    UNION ALL
    SELECT N'Portal' AS setting_category,
           N'ShowPortalNavbar' AS setting_name,
           CAST(1 AS tinyint) AS value_kind,
           CAST(1 AS int) AS default_int_value,
           CAST(NULL AS nvarchar(max)) AS default_string_value,
           N'Controls whether the Portal navbar below the topbar is shown for the signed-in user.' AS description,
           30 AS sort_order,
           CAST(1 AS bit) AS is_enabled
) AS source
ON target.setting_category = source.setting_category
AND target.setting_name = source.setting_name
WHEN MATCHED THEN
    UPDATE SET value_kind = source.value_kind,
               default_int_value = source.default_int_value,
               default_string_value = source.default_string_value,
               description = source.description,
               sort_order = source.sort_order,
               is_enabled = source.is_enabled,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(setting_category, setting_name, value_kind, default_int_value, default_string_value, description, sort_order, is_enabled)
    VALUES(source.setting_category, source.setting_name, source.value_kind, source.default_int_value, source.default_string_value, source.description, source.sort_order, source.is_enabled);
GO

IF OBJECT_ID(N'omp_portal.user_settings', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp_portal.user_settings', N'admin_metrics_collapsed') IS NOT NULL
BEGIN
    DECLARE @AdminMetricsSettingId int;

    SELECT @AdminMetricsSettingId = user_setting_definition_id
    FROM omp_portal.user_setting_definitions
    WHERE setting_category = N'Portal'
      AND setting_name = N'AdminMetricsCollapsed'
      AND value_kind = 1;

    MERGE omp_portal.user_setting_int_values AS target
    USING
    (
        SELECT user_id,
               @AdminMetricsSettingId AS user_setting_definition_id,
               CAST(1 AS tinyint) AS value_kind,
               1 AS setting_value,
               CONVERT(datetime2(3), updated_at) AS updated_at
        FROM omp_portal.user_settings
        WHERE admin_metrics_collapsed = 1
    ) AS source
    ON target.user_id = source.user_id
    AND target.user_setting_definition_id = source.user_setting_definition_id
    WHEN MATCHED THEN
        UPDATE SET setting_value = source.setting_value,
                   updated_at = source.updated_at
    WHEN NOT MATCHED THEN
        INSERT(user_id, user_setting_definition_id, value_kind, setting_value, updated_at)
        VALUES(source.user_id, source.user_setting_definition_id, source.value_kind, source.setting_value, source.updated_at);
END
GO

-- Keep the legacy table after copying values so module-definition repair can stay non-destructive.
-- It can be removed manually during a planned database cleanup if no older Portal version is in use.

IF OBJECT_ID(N'omp_portal.user_navigation_favorites', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.user_navigation_favorites
    (
        user_id int NOT NULL,
        entry_key nvarchar(200) NOT NULL,
        app_instance_id uniqueidentifier NULL,
        sort_order int NULL,
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_user_navigation_favorites_created_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_user_navigation_favorites PRIMARY KEY(user_id, entry_key),
        CONSTRAINT FK_omp_portal_user_navigation_favorites_user FOREIGN KEY(user_id)
            REFERENCES omp.users(user_id)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_portal_user_navigation_favorites_user_sort'
      AND object_id = OBJECT_ID(N'omp_portal.user_navigation_favorites')
)
BEGIN
    CREATE INDEX IX_omp_portal_user_navigation_favorites_user_sort
        ON omp_portal.user_navigation_favorites(user_id, sort_order, created_at)
        INCLUDE(entry_key, app_instance_id);
END
GO

IF OBJECT_ID(N'omp_portal.portal_entries', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.portal_entries
    (
        portal_entry_id int IDENTITY(1,1) NOT NULL,
        parent_entry_id int NULL,
        entry_key nvarchar(200) NOT NULL,
        display_name nvarchar(200) NOT NULL,
        description nvarchar(1000) NULL,
        logo_url nvarchar(600) NULL,
        icon_key nvarchar(100) NULL,
        target_url nvarchar(600) NULL,
        target_entry_key nvarchar(200) NULL,
        source_app_instance_id uniqueidentifier NULL,
        is_enabled bit NOT NULL CONSTRAINT DF_omp_portal_portal_entries_is_enabled DEFAULT(1),
        default_sort_order int NOT NULL CONSTRAINT DF_omp_portal_portal_entries_default_sort_order DEFAULT(0),
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_portal_entries_created_at DEFAULT(SYSUTCDATETIME()),
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_portal_entries_updated_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_portal_entries PRIMARY KEY(portal_entry_id),
        CONSTRAINT UQ_omp_portal_portal_entries_entry_key UNIQUE(entry_key),
        CONSTRAINT FK_omp_portal_portal_entries_parent FOREIGN KEY(parent_entry_id)
            REFERENCES omp_portal.portal_entries(portal_entry_id)
    );
END
GO

IF COL_LENGTH(N'omp_portal.portal_entries', N'parent_entry_id') IS NULL
BEGIN
    ALTER TABLE omp_portal.portal_entries
        ADD parent_entry_id int NULL;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_omp_portal_portal_entries_parent'
      AND parent_object_id = OBJECT_ID(N'omp_portal.portal_entries')
)
BEGIN
    ALTER TABLE omp_portal.portal_entries
        ADD CONSTRAINT FK_omp_portal_portal_entries_parent
            FOREIGN KEY(parent_entry_id)
            REFERENCES omp_portal.portal_entries(portal_entry_id);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_portal_portal_entries_parent_sort'
      AND object_id = OBJECT_ID(N'omp_portal.portal_entries')
)
BEGIN
    CREATE INDEX IX_omp_portal_portal_entries_parent_sort
        ON omp_portal.portal_entries(parent_entry_id, default_sort_order, display_name)
        INCLUDE(entry_key, is_enabled);
END
GO

IF OBJECT_ID(N'omp_portal.portal_user_entry_state', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.portal_user_entry_state
    (
        user_id int NOT NULL,
        portal_entry_id int NOT NULL,
        is_pinned bit NOT NULL CONSTRAINT DF_omp_portal_portal_user_entry_state_is_pinned DEFAULT(0),
        is_hidden bit NOT NULL CONSTRAINT DF_omp_portal_portal_user_entry_state_is_hidden DEFAULT(0),
        sort_order int NULL,
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_portal_user_entry_state_updated_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_portal_user_entry_state PRIMARY KEY(user_id, portal_entry_id),
        CONSTRAINT FK_omp_portal_portal_user_entry_state_user FOREIGN KEY(user_id)
            REFERENCES omp.users(user_id),
        CONSTRAINT FK_omp_portal_portal_user_entry_state_entry FOREIGN KEY(portal_entry_id)
            REFERENCES omp_portal.portal_entries(portal_entry_id)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_portal_portal_user_entry_state_user_sort'
      AND object_id = OBJECT_ID(N'omp_portal.portal_user_entry_state')
)
BEGIN
    CREATE INDEX IX_omp_portal_portal_user_entry_state_user_sort
        ON omp_portal.portal_user_entry_state(user_id, is_pinned, is_hidden, sort_order)
        INCLUDE(portal_entry_id);
END
GO

;WITH app_entries AS
(
    SELECT N'app:' + LOWER(REPLACE(CONVERT(nvarchar(36), ai.AppInstanceId), N'-', N'')) + N':home' AS entry_key,
           ai.DisplayName AS display_name,
           ai.Description AS description,
           N'app:' + LOWER(REPLACE(CONVERT(nvarchar(36), ai.AppInstanceId), N'-', N'')) + N':home' AS target_entry_key,
           ai.AppInstanceId AS source_app_instance_id,
           ai.SortOrder AS default_sort_order
    FROM omp.AppInstances ai
    INNER JOIN omp.Apps a ON a.AppId = ai.AppId
    WHERE ai.IsEnabled = 1
      AND ai.IsAllowed = 1
      AND a.AppType IN (N'Portal', N'WebApp')
)
MERGE omp_portal.portal_entries AS target
USING app_entries AS source
    ON target.entry_key = source.entry_key
WHEN MATCHED THEN
    UPDATE SET display_name = source.display_name,
               description = source.description,
               target_entry_key = source.target_entry_key,
               source_app_instance_id = source.source_app_instance_id,
               default_sort_order = source.default_sort_order,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT(entry_key, display_name, description, target_entry_key, source_app_instance_id, is_enabled, default_sort_order)
    VALUES(source.entry_key, source.display_name, source.description, source.target_entry_key, source.source_app_instance_id, 1, source.default_sort_order);
GO

;WITH child_entry_definitions AS
(
    SELECT *
    FROM (VALUES
        (N'omp_portal', N'portal:admin-overview', N'Admin overview', N'Portal administration overview.', N'/admin/overview', 10),
        (N'omp_portal', N'portal:admin-portal-entries', N'Portal entries', N'Manage global Portal Entries.', N'/admin/portalentries', 20),
        (N'omp_portal', N'portal:admin-users', N'Users', N'Manage OMP users and auth links.', N'/admin/users', 30),
        (N'omp_portal', N'portal:admin-security', N'Security', N'Manage roles and permissions.', N'/admin/security', 40),
        (N'omp_portal', N'portal:admin-config-settings', N'Config settings', N'Manage OMP configuration settings.', N'/admin/configsettings', 50),
        (N'omp_portal', N'portal:admin-module-packages', N'Import/export', N'Import and export OMP installation objects.', N'/admin/modulepackageimport', 60),
        (N'omp_portal', N'portal:admin-artifacts', N'Artifacts', N'Manage artifact metadata and configuration files.', N'/admin/artifacts', 70),

        (N'content_webapp_webapp', N'content:admin', N'Admin', N'Manage content pages.', N'/content/admin', 10),
        (N'content_webapp_webapp', N'content:create', N'Create page', N'Create a new content page.', N'/content/admin/create', 20),

        (N'example_webapp_webapp', N'example-webapp:configurations', N'Configurations', N'Manage example web app configurations.', N'/ExampleWebAppModule/configurations', 10),
        (N'example_webapp_webapp', N'example-webapp:opendocviewer-demo', N'OpenDocViewer demo', N'Open the OpenDocViewer integration demo.', N'/ExampleWebAppModule/opendocviewer-demo', 20),

        (N'example_webapp_blazor_webapp', N'example-webapp-blazor:configurations', N'Configurations', N'Manage example Blazor app configurations.', N'/ExampleWebAppBlazorModule/configurations', 10),
        (N'example_webapp_blazor_webapp', N'example-webapp-blazor:opendocviewer-demo', N'OpenDocViewer demo', N'Open the OpenDocViewer integration demo.', N'/ExampleWebAppBlazorModule/opendocviewer-demo', 20),

        (N'example_serviceapp_webapp', N'example-serviceapp:app-instances', N'App instances', N'Manage service app instances.', N'/ExampleServiceAppModule/appinstances', 10),
        (N'example_serviceapp_webapp', N'example-serviceapp:configurations', N'Configurations', N'Manage service app configurations.', N'/ExampleServiceAppModule/configurations', 20),
        (N'example_serviceapp_webapp', N'example-serviceapp:jobs', N'Jobs', N'Queue and inspect service app jobs.', N'/ExampleServiceAppModule/jobs', 30),
        (N'example_serviceapp_webapp', N'example-serviceapp:opendocviewer-demo', N'OpenDocViewer demo', N'Open the OpenDocViewer integration demo.', N'/ExampleServiceAppModule/opendocviewer-demo', 40),

        (N'example_workerapp_webapp', N'example-workerapp:app-instances', N'App instances', N'Manage worker app instances.', N'/ExampleWorkerAppModule/appinstances', 10),
        (N'example_workerapp_webapp', N'example-workerapp:configurations', N'Configurations', N'Manage worker app configurations.', N'/ExampleWorkerAppModule/configurations', 20),
        (N'example_workerapp_webapp', N'example-workerapp:jobs', N'Jobs', N'Queue and inspect worker app jobs.', N'/ExampleWorkerAppModule/jobs', 30),
        (N'example_workerapp_webapp', N'example-workerapp:opendocviewer-demo', N'OpenDocViewer demo', N'Open the OpenDocViewer integration demo.', N'/ExampleWorkerAppModule/opendocviewer-demo', 40)
    ) AS entries(parent_app_instance_key, entry_key, display_name, description, target_url, default_sort_order)
),
child_entries AS
(
    SELECT parent.portal_entry_id AS parent_entry_id,
           child.entry_key,
           child.display_name,
           child.description,
           child.target_url,
           child.default_sort_order
    FROM child_entry_definitions child
    INNER JOIN omp.AppInstances ai ON ai.AppInstanceKey = child.parent_app_instance_key
    INNER JOIN omp_portal.portal_entries parent
        ON parent.source_app_instance_id = ai.AppInstanceId
       AND parent.parent_entry_id IS NULL
    WHERE ai.IsEnabled = 1
      AND ai.IsAllowed = 1
)
MERGE omp_portal.portal_entries AS target
USING child_entries AS source
    ON target.entry_key = source.entry_key
WHEN MATCHED THEN
    UPDATE SET parent_entry_id = source.parent_entry_id,
               display_name = source.display_name,
               description = source.description,
               target_url = source.target_url,
               target_entry_key = NULL,
               source_app_instance_id = NULL,
               default_sort_order = source.default_sort_order,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT(entry_key, parent_entry_id, display_name, description, target_url, is_enabled, default_sort_order)
    VALUES(source.entry_key, source.parent_entry_id, source.display_name, source.description, source.target_url, 1, source.default_sort_order);
GO

UPDATE omp_portal.portal_entries
SET is_enabled = 0,
    updated_at = SYSUTCDATETIME()
WHERE entry_key = N'portal:admin-dashboard-widgets';
GO

IF OBJECT_ID(N'omp_portal.widgets', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.widgets
    (
        widget_id int IDENTITY(1,1) NOT NULL,
        widget_key nvarchar(200) NULL,
        title nvarchar(200) NOT NULL,
        widget_type nvarchar(50) NOT NULL,
        payload nvarchar(max) NULL,
        module_key nvarchar(100) NULL,
        author nvarchar(200) NULL,
        modified_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_widgets_modified_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_widgets PRIMARY KEY(widget_id)
    );
END
GO

IF COL_LENGTH(N'omp_portal.widgets', N'widget_key') IS NULL
BEGIN
    ALTER TABLE omp_portal.widgets
        ADD widget_key nvarchar(200) NULL;
END
GO

;WITH legacy_widgets AS
(
    SELECT widget_id,
           ROW_NUMBER() OVER (ORDER BY widget_id) AS rn
    FROM omp_portal.widgets
    WHERE widget_key IS NULL
      AND module_key = N'omp_portal'
      AND widget_type = N'portal'
      AND payload = N'blank-rectangle'
)
UPDATE w
SET widget_key = N'blank-rectangle'
FROM omp_portal.widgets w
INNER JOIN legacy_widgets legacy ON legacy.widget_id = w.widget_id
WHERE legacy.rn = 1;
GO

;WITH legacy_widgets AS
(
    SELECT widget_id,
           ROW_NUMBER() OVER (ORDER BY widget_id) AS rn
    FROM omp_portal.widgets
    WHERE widget_key IS NULL
      AND module_key = N'omp_portal'
      AND widget_type = N'portal'
      AND payload = N'admin-overview'
)
UPDATE w
SET widget_key = N'admin-overview'
FROM omp_portal.widgets w
INNER JOIN legacy_widgets legacy ON legacy.widget_id = w.widget_id
WHERE legacy.rn = 1;
GO

IF OBJECT_ID(N'omp_portal.widget_permissions', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.widget_permissions
    (
        widget_permission_id int IDENTITY(1,1) NOT NULL,
        widget_id int NOT NULL,
        permission_id int NULL,
        role_id int NULL,

        CONSTRAINT PK_omp_portal_widget_permissions PRIMARY KEY(widget_permission_id),
        CONSTRAINT FK_omp_portal_widget_permissions_widget FOREIGN KEY(widget_id)
            REFERENCES omp_portal.widgets(widget_id),
        CONSTRAINT FK_omp_portal_widget_permissions_permission FOREIGN KEY(permission_id)
            REFERENCES omp.Permissions(PermissionId),
        CONSTRAINT FK_omp_portal_widget_permissions_role FOREIGN KEY(role_id)
            REFERENCES omp.Roles(RoleId),
        CONSTRAINT CK_omp_portal_widget_permissions_target CHECK
        (
            (permission_id IS NOT NULL AND role_id IS NULL)
            OR (permission_id IS NULL AND role_id IS NOT NULL)
        )
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_portal_widget_permissions_widget'
      AND object_id = OBJECT_ID(N'omp_portal.widget_permissions')
)
BEGIN
    CREATE INDEX IX_omp_portal_widget_permissions_widget
        ON omp_portal.widget_permissions(widget_id)
        INCLUDE(permission_id, role_id);
END
GO

IF OBJECT_ID(N'omp.users', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM omp.users WHERE user_id = 0)
BEGIN
    SET IDENTITY_INSERT omp.users ON;

    INSERT INTO omp.users(user_id, display_name, account_status, created_at, updated_at)
    VALUES(0, N'Portal default dashboard', 3, SYSUTCDATETIME(), SYSUTCDATETIME());

    SET IDENTITY_INSERT omp.users OFF;
END
GO

IF OBJECT_ID(N'omp_portal.user_active_widgets', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.user_active_widgets
    (
        user_active_widget_id bigint IDENTITY(1,1) NOT NULL,
        widget_id int NOT NULL,
        user_id int NOT NULL,
        offset_top int NOT NULL CONSTRAINT DF_omp_portal_user_active_widgets_offset_top DEFAULT(0),
        offset_left int NOT NULL CONSTRAINT DF_omp_portal_user_active_widgets_offset_left DEFAULT(0),
        width int NOT NULL CONSTRAINT DF_omp_portal_user_active_widgets_width DEFAULT(320),
        height int NOT NULL CONSTRAINT DF_omp_portal_user_active_widgets_height DEFAULT(192),
        order_priority int NOT NULL CONSTRAINT DF_omp_portal_user_active_widgets_order_priority DEFAULT(0),
        title nvarchar(200) NULL,
        int_data int NULL,
        string_data nvarchar(20) NULL,
        content_scale int NOT NULL CONSTRAINT DF_omp_portal_user_active_widgets_content_scale DEFAULT(0),
        hide_titlebar_when_viewing bit NOT NULL CONSTRAINT DF_omp_portal_user_active_widgets_hide_titlebar_when_viewing DEFAULT(0),

        CONSTRAINT PK_omp_portal_user_active_widgets PRIMARY KEY(user_active_widget_id),
        CONSTRAINT FK_omp_portal_user_active_widgets_widget FOREIGN KEY(widget_id)
            REFERENCES omp_portal.widgets(widget_id),
        CONSTRAINT FK_omp_portal_user_active_widgets_user FOREIGN KEY(user_id)
            REFERENCES omp.users(user_id),
        CONSTRAINT CK_omp_portal_user_active_widgets_size CHECK(width >= 160 AND height >= 96),
        CONSTRAINT CK_omp_portal_user_active_widgets_offset CHECK(offset_top >= 0 AND offset_left >= 0)
    );
END
GO

IF COL_LENGTH(N'omp_portal.user_active_widgets', N'content_scale') IS NULL
BEGIN
    ALTER TABLE omp_portal.user_active_widgets
        ADD content_scale int NOT NULL
            CONSTRAINT DF_omp_portal_user_active_widgets_content_scale DEFAULT(0);
END
GO

IF COL_LENGTH(N'omp_portal.user_active_widgets', N'hide_titlebar_when_viewing') IS NULL
BEGIN
    ALTER TABLE omp_portal.user_active_widgets
        ADD hide_titlebar_when_viewing bit NOT NULL
            CONSTRAINT DF_omp_portal_user_active_widgets_hide_titlebar_when_viewing DEFAULT(0);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_portal_user_active_widgets_user_order'
      AND object_id = OBJECT_ID(N'omp_portal.user_active_widgets')
)
BEGIN
    CREATE INDEX IX_omp_portal_user_active_widgets_user_order
        ON omp_portal.user_active_widgets(user_id, order_priority, user_active_widget_id)
        INCLUDE(widget_id, offset_top, offset_left, width, height, title, int_data, string_data, content_scale);
END
GO

IF OBJECT_ID(N'omp_portal.user_active_widget_data', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.user_active_widget_data
    (
        user_active_widget_id bigint NOT NULL,
        data_value nvarchar(max) NOT NULL,

        CONSTRAINT PK_omp_portal_user_active_widget_data PRIMARY KEY(user_active_widget_id),
        CONSTRAINT FK_omp_portal_user_active_widget_data_active_widget FOREIGN KEY(user_active_widget_id)
            REFERENCES omp_portal.user_active_widgets(user_active_widget_id)
            ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID(N'omp_portal.widget_data', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.widget_data
    (
        widget_id int NOT NULL,
        data_key nvarchar(128) NOT NULL,
        json_data nvarchar(max) NOT NULL,
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_widget_data_updated_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_widget_data PRIMARY KEY(widget_id, data_key),
        CONSTRAINT FK_omp_portal_widget_data_widget FOREIGN KEY(widget_id)
            REFERENCES omp_portal.widgets(widget_id)
            ON DELETE CASCADE,
        CONSTRAINT CK_omp_portal_widget_data_json CHECK(ISJSON(json_data) = 1)
    );
END
GO

IF OBJECT_ID(N'omp_portal.widget_binary_data', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.widget_binary_data
    (
        binary_data_id bigint IDENTITY(1,1) NOT NULL,
        owner_ref nvarchar(128) NOT NULL,
        file_name nvarchar(260) NOT NULL,
        content_type nvarchar(128) NOT NULL,
        content_length bigint NOT NULL,
        content_hash varbinary(32) NOT NULL,
        data_value varbinary(max) NOT NULL,
        is_enabled bit NOT NULL CONSTRAINT DF_omp_portal_widget_binary_data_is_enabled DEFAULT(1),
        created_by_user_id int NULL,
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_widget_binary_data_created_at DEFAULT(SYSUTCDATETIME()),
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_widget_binary_data_updated_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_widget_binary_data PRIMARY KEY(binary_data_id),
        CONSTRAINT FK_omp_portal_widget_binary_data_created_by_user FOREIGN KEY(created_by_user_id)
            REFERENCES omp.users(user_id),
        CONSTRAINT CK_omp_portal_widget_binary_data_length CHECK(content_length >= 0)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_portal_widget_binary_data_owner'
      AND object_id = OBJECT_ID(N'omp_portal.widget_binary_data')
)
BEGIN
    CREATE INDEX IX_omp_portal_widget_binary_data_owner
        ON omp_portal.widget_binary_data(owner_ref, is_enabled, binary_data_id)
        INCLUDE(file_name, content_type, content_length, content_hash);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_portal_widget_binary_data_hash'
      AND object_id = OBJECT_ID(N'omp_portal.widget_binary_data')
)
BEGIN
    CREATE INDEX IX_omp_portal_widget_binary_data_hash
        ON omp_portal.widget_binary_data(content_hash, content_length)
        INCLUDE(owner_ref, file_name, is_enabled);
END
GO

IF OBJECT_ID(N'omp_portal.user_dashboard_preferences', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.user_dashboard_preferences
    (
        user_id int NOT NULL,
        align_to_grid bit NOT NULL CONSTRAINT DF_omp_portal_user_dashboard_preferences_align_to_grid DEFAULT(1),
        expanded_canvas bit NOT NULL CONSTRAINT DF_omp_portal_user_dashboard_preferences_expanded_canvas DEFAULT(0),
        has_custom_dashboard_layout bit NOT NULL CONSTRAINT DF_omp_portal_user_dashboard_preferences_has_custom_dashboard_layout DEFAULT(0),
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_user_dashboard_preferences_updated_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_user_dashboard_preferences PRIMARY KEY(user_id),
        CONSTRAINT FK_omp_portal_user_dashboard_preferences_user FOREIGN KEY(user_id)
            REFERENCES omp.users(user_id)
    );
END
GO

IF OBJECT_ID(N'omp_portal.user_dashboard_preferences', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp_portal.user_dashboard_preferences', N'has_custom_dashboard_layout') IS NULL
BEGIN
    ALTER TABLE omp_portal.user_dashboard_preferences
        ADD has_custom_dashboard_layout bit NOT NULL
            CONSTRAINT DF_omp_portal_user_dashboard_preferences_has_custom_dashboard_layout DEFAULT(0);
END
GO

IF OBJECT_ID(N'omp_portal.user_dashboard_preferences', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp_portal.user_dashboard_preferences', N'expanded_canvas') IS NULL
BEGIN
    ALTER TABLE omp_portal.user_dashboard_preferences
        ADD expanded_canvas bit NOT NULL CONSTRAINT DF_omp_portal_user_dashboard_preferences_expanded_canvas DEFAULT(0);
END
GO

IF EXISTS
(
    SELECT 1
    FROM omp_portal.widgets
    WHERE widget_key IS NOT NULL
    GROUP BY widget_key
    HAVING COUNT(*) > 1
)
BEGIN
    ;WITH duplicate_widgets AS
    (
        SELECT widget_id,
               MIN(widget_id) OVER (PARTITION BY widget_key) AS keep_widget_id
        FROM omp_portal.widgets
        WHERE widget_key IS NOT NULL
    )
    UPDATE active_widgets
    SET widget_id = duplicate_widgets.keep_widget_id
    FROM omp_portal.user_active_widgets active_widgets
    INNER JOIN duplicate_widgets ON duplicate_widgets.widget_id = active_widgets.widget_id
    WHERE duplicate_widgets.widget_id <> duplicate_widgets.keep_widget_id;

    ;WITH duplicate_widgets AS
    (
        SELECT widget_id,
               MIN(widget_id) OVER (PARTITION BY widget_key) AS keep_widget_id
        FROM omp_portal.widgets
        WHERE widget_key IS NOT NULL
    ),
    duplicate_permissions AS
    (
        SELECT duplicate_widgets.keep_widget_id,
               widget_permissions.permission_id,
               widget_permissions.role_id
        FROM omp_portal.widget_permissions widget_permissions
        INNER JOIN duplicate_widgets ON duplicate_widgets.widget_id = widget_permissions.widget_id
        WHERE duplicate_widgets.widget_id <> duplicate_widgets.keep_widget_id
    )
    INSERT INTO omp_portal.widget_permissions(widget_id, permission_id, role_id)
    SELECT DISTINCT keep_widget_id,
           permission_id,
           role_id
    FROM duplicate_permissions dp
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM omp_portal.widget_permissions target
        WHERE target.widget_id = dp.keep_widget_id
          AND ISNULL(target.permission_id, -1) = ISNULL(dp.permission_id, -1)
          AND ISNULL(target.role_id, -1) = ISNULL(dp.role_id, -1)
    );

    ;WITH duplicate_widgets AS
    (
        SELECT widget_id,
               MIN(widget_id) OVER (PARTITION BY widget_key) AS keep_widget_id
        FROM omp_portal.widgets
        WHERE widget_key IS NOT NULL
    )
    DELETE widget_permissions
    FROM omp_portal.widget_permissions widget_permissions
    INNER JOIN duplicate_widgets ON duplicate_widgets.widget_id = widget_permissions.widget_id
    WHERE duplicate_widgets.widget_id <> duplicate_widgets.keep_widget_id;

    ;WITH duplicate_widgets AS
    (
        SELECT widget_id,
               MIN(widget_id) OVER (PARTITION BY widget_key) AS keep_widget_id
        FROM omp_portal.widgets
        WHERE widget_key IS NOT NULL
    )
    DELETE widgets
    FROM omp_portal.widgets widgets
    INNER JOIN duplicate_widgets ON duplicate_widgets.widget_id = widgets.widget_id
    WHERE duplicate_widgets.widget_id <> duplicate_widgets.keep_widget_id;
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_omp_portal_widgets_module_key_widget_key'
      AND object_id = OBJECT_ID(N'omp_portal.widgets')
)
BEGIN
    DROP INDEX UX_omp_portal_widgets_module_key_widget_key ON omp_portal.widgets;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_omp_portal_widgets_widget_key'
      AND object_id = OBJECT_ID(N'omp_portal.widgets')
)
BEGIN
    CREATE UNIQUE INDEX UX_omp_portal_widgets_widget_key
        ON omp_portal.widgets(widget_key)
        WHERE widget_key IS NOT NULL;
END
GO

IF OBJECT_ID(N'omp_portal.user_dashboard_preferences', N'U') IS NOT NULL
   AND OBJECT_ID(N'omp_portal.user_active_widgets', N'U') IS NOT NULL
BEGIN
    INSERT INTO omp_portal.user_dashboard_preferences(user_id, align_to_grid, expanded_canvas, has_custom_dashboard_layout, updated_at)
    SELECT DISTINCT aw.user_id, 1, 1, 1, SYSUTCDATETIME()
    FROM omp_portal.user_active_widgets aw
    WHERE aw.user_id <> 0
      AND NOT EXISTS
      (
          SELECT 1
          FROM omp_portal.user_dashboard_preferences p
          WHERE p.user_id = aw.user_id
      );

    UPDATE p
    SET has_custom_dashboard_layout = 1,
        updated_at = SYSUTCDATETIME()
    FROM omp_portal.user_dashboard_preferences p
    WHERE p.user_id <> 0
      AND p.has_custom_dashboard_layout = 0
      AND EXISTS
      (
          SELECT 1
          FROM omp_portal.user_active_widgets aw
          WHERE aw.user_id = p.user_id
      );
END
GO

MERGE omp_portal.widgets AS target
USING
(
    SELECT N'blank-rectangle' AS widget_key,
           N'Blank widget' AS title,
           N'portal' AS widget_type,
           N'blank-rectangle' AS payload,
           N'omp_portal' AS module_key,
           N'OpenModulePlatform' AS author
    UNION ALL
    SELECT N'admin-overview' AS widget_key,
           N'Admin overview' AS title,
           N'portal' AS widget_type,
           N'admin-overview' AS payload,
           N'omp_portal' AS module_key,
           N'OpenModulePlatform' AS author
    UNION ALL
    SELECT N'portal-entry-favorites' AS widget_key,
           N'Favorite portal entries' AS title,
           N'portal' AS widget_type,
           N'portal-entry-favorites' AS payload,
           N'omp_portal' AS module_key,
           N'OpenModulePlatform' AS author
    UNION ALL
    SELECT N'portal-entry-list' AS widget_key,
           N'All portal entries' AS title,
           N'portal' AS widget_type,
           N'portal-entry-list' AS payload,
           N'omp_portal' AS module_key,
           N'OpenModulePlatform' AS author
    UNION ALL
    SELECT N'portal-entry-combolist' AS widget_key,
           N'Combolist' AS title,
           N'portal' AS widget_type,
           N'portal-entry-combolist' AS payload,
           N'omp_portal' AS module_key,
           N'OpenModulePlatform' AS author
    UNION ALL
    SELECT N'portal-navbar-links' AS widget_key,
           N'Portal navigation' AS title,
           N'portal' AS widget_type,
           N'portal-navbar-links' AS payload,
           N'omp_portal' AS module_key,
           N'OpenModulePlatform' AS author
    UNION ALL
    SELECT N'user-roles' AS widget_key,
           N'User roles' AS title,
           N'portal' AS widget_type,
           N'user-roles' AS payload,
           N'omp_portal' AS module_key,
           N'OpenModulePlatform' AS author
    UNION ALL
    SELECT N'content-pages' AS widget_key,
           N'Content pages' AS title,
           N'portal' AS widget_type,
           N'content-pages' AS payload,
           N'omp_portal' AS module_key,
           N'OpenModulePlatform' AS author
    UNION ALL
    SELECT N'weekday-date' AS widget_key,
           N'Weekday and date' AS title,
           N'portal' AS widget_type,
           N'weekday-date' AS payload,
           N'omp_portal' AS module_key,
           N'OpenModulePlatform' AS author
) AS source
ON target.widget_key = source.widget_key
WHEN MATCHED THEN
    UPDATE SET title = source.title,
               widget_type = source.widget_type,
               payload = source.payload,
               module_key = source.module_key,
               author = source.author,
               modified_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(widget_key, title, widget_type, payload, module_key, author, modified_at)
    VALUES(source.widget_key, source.title, source.widget_type, source.payload, source.module_key, source.author, SYSUTCDATETIME());
GO

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'OMP.Portal.Admin')
BEGIN
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(N'OMP.Portal.Admin', N'Administrative access to the OMP Portal');
END
GO

DECLARE @AdminOverviewWidgetId int;
DECLARE @PortalAdminPermissionId int;

SELECT @AdminOverviewWidgetId = widget_id
FROM omp_portal.widgets
WHERE widget_key = N'admin-overview';

SELECT @PortalAdminPermissionId = PermissionId
FROM omp.Permissions
WHERE Name = N'OMP.Portal.Admin';

IF @AdminOverviewWidgetId IS NOT NULL
   AND @PortalAdminPermissionId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp_portal.widget_permissions
       WHERE widget_id = @AdminOverviewWidgetId
         AND permission_id = @PortalAdminPermissionId
   )
BEGIN
    INSERT INTO omp_portal.widget_permissions(widget_id, permission_id, role_id)
    VALUES(@AdminOverviewWidgetId, @PortalAdminPermissionId, NULL);
END
GO
