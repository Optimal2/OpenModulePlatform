-- File: OpenModulePlatform.Portal/sql/5-ensure-dashboard-widgets.sql
/*
Creates the first Portal dashboard widget tables and seeds the initial blank
Portal widget.

Prerequisite:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run 1-setup-omp-portal.sql at least once
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

IF OBJECT_ID(N'omp_portal.user_dashboard_preferences', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.user_dashboard_preferences
    (
        user_id int NOT NULL,
        align_to_grid bit NOT NULL CONSTRAINT DF_omp_portal_user_dashboard_preferences_align_to_grid DEFAULT(1),
        expanded_canvas bit NOT NULL CONSTRAINT DF_omp_portal_user_dashboard_preferences_expanded_canvas DEFAULT(0),
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_portal_user_dashboard_preferences_updated_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_user_dashboard_preferences PRIMARY KEY(user_id),
        CONSTRAINT FK_omp_portal_user_dashboard_preferences_user FOREIGN KEY(user_id)
            REFERENCES omp.users(user_id)
    );
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
    UNION ALL
    SELECT N'music-player' AS widget_key,
           N'Music player' AS title,
           N'portal' AS widget_type,
           N'music-player' AS payload,
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
