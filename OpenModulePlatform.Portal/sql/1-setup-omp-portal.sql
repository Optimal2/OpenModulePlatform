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
