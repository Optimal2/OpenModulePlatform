-- File: OpenModulePlatform.Portal/sql/1-setup-omp-portal.sql
/*
Creates the OMP Portal schema.

Prerequisite:
- Run ../../sql/1-setup-openmoduleplatform.sql first.
*/
USE [OpenModulePlatform];
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
        (N'omp_portal', N'portal:admin-module-packages', N'Module packages', N'Import and export portable module packages.', N'/admin/modulepackageimport', 60),

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
