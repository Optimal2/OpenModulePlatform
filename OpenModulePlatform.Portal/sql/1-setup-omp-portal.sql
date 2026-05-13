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
