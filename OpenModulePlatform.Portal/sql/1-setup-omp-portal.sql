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

IF OBJECT_ID(N'omp_portal.user_settings', N'U') IS NULL
BEGIN
    CREATE TABLE omp_portal.user_settings
    (
        user_id int NOT NULL,
        admin_metrics_collapsed bit NOT NULL CONSTRAINT DF_omp_portal_user_settings_admin_metrics_collapsed DEFAULT(0),
        updated_at datetime2(0) NOT NULL CONSTRAINT DF_omp_portal_user_settings_updated_at DEFAULT(SYSUTCDATETIME()),

        CONSTRAINT PK_omp_portal_user_settings PRIMARY KEY(user_id),
        CONSTRAINT FK_omp_portal_user_settings_user FOREIGN KEY(user_id)
            REFERENCES omp.users(user_id)
    );
END
GO

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
        CONSTRAINT UQ_omp_portal_portal_entries_entry_key UNIQUE(entry_key)
    );
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
