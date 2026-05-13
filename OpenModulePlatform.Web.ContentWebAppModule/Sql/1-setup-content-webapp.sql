-- File: OpenModulePlatform.Web.ContentWebAppModule/Sql/1-setup-content-webapp.sql
/*
Creates the Content Web App module schema and tables.

Prerequisite:
- Run ../../sql/1-setup-openmoduleplatform.sql first.
*/
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_content')
    EXEC('CREATE SCHEMA [omp_content]');
GO

IF OBJECT_ID(N'omp_content.contents', N'U') IS NULL
BEGIN
    CREATE TABLE omp_content.contents
    (
        content_id uniqueidentifier NOT NULL CONSTRAINT DF_omp_content_contents_content_id DEFAULT NEWID(),
        app_instance_id uniqueidentifier NOT NULL,
        slug nvarchar(256) NOT NULL,
        title nvarchar(200) NOT NULL,
        content_type nvarchar(20) NOT NULL CONSTRAINT DF_omp_content_contents_content_type DEFAULT(N'markdown'),
        body nvarchar(max) NOT NULL,
        server_report_key nvarchar(128) NULL,
        is_enabled bit NOT NULL CONSTRAINT DF_omp_content_contents_is_enabled DEFAULT(1),
        sort_order int NULL,
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_content_contents_created_at DEFAULT SYSUTCDATETIME(),
        created_by nvarchar(256) NULL,
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_content_contents_updated_at DEFAULT SYSUTCDATETIME(),
        updated_by nvarchar(256) NULL,

        CONSTRAINT PK_omp_content_contents PRIMARY KEY(content_id),
        CONSTRAINT FK_omp_content_contents_app_instance FOREIGN KEY(app_instance_id)
            REFERENCES omp.AppInstances(AppInstanceId),
        CONSTRAINT CK_omp_content_contents_content_type CHECK(content_type IN (N'markdown', N'html', N'server_report'))
    );
END
GO

IF COL_LENGTH(N'omp_content.contents', N'server_report_key') IS NULL
BEGIN
    ALTER TABLE omp_content.contents
        ADD server_report_key nvarchar(128) NULL;
END
GO

DECLARE @ContentTypeConstraint sysname;
SELECT @ContentTypeConstraint = name
FROM sys.check_constraints
WHERE parent_object_id = OBJECT_ID(N'omp_content.contents')
  AND name = N'CK_omp_content_contents_content_type';

IF @ContentTypeConstraint IS NOT NULL
BEGIN
    DECLARE @DropContentTypeConstraintSql nvarchar(max) =
        N'ALTER TABLE omp_content.contents DROP CONSTRAINT ' + QUOTENAME(@ContentTypeConstraint);
    EXEC sys.sp_executesql @DropContentTypeConstraintSql;
END

ALTER TABLE omp_content.contents
    ADD CONSTRAINT CK_omp_content_contents_content_type
        CHECK(content_type IN (N'markdown', N'html', N'server_report'));
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'omp_content.contents')
      AND name = N'CK_omp_content_contents_server_report_key'
)
BEGIN
    ALTER TABLE omp_content.contents
        ADD CONSTRAINT CK_omp_content_contents_server_report_key
            CHECK(server_report_key IS NULL OR (LEN(server_report_key) BETWEEN 1 AND 128 AND server_report_key NOT LIKE N'%[^A-Za-z0-9_-]%'));
END
GO

IF OBJECT_ID(N'omp_content.content_role_access', N'U') IS NULL
BEGIN
    CREATE TABLE omp_content.content_role_access
    (
        content_id uniqueidentifier NOT NULL,
        role_id int NOT NULL,
        can_read bit NOT NULL CONSTRAINT DF_omp_content_content_role_access_can_read DEFAULT(0),
        can_write bit NOT NULL CONSTRAINT DF_omp_content_content_role_access_can_write DEFAULT(0),

        CONSTRAINT PK_omp_content_content_role_access PRIMARY KEY(content_id, role_id),
        CONSTRAINT FK_omp_content_content_role_access_content FOREIGN KEY(content_id)
            REFERENCES omp_content.contents(content_id),
        CONSTRAINT FK_omp_content_content_role_access_role FOREIGN KEY(role_id)
            REFERENCES omp.Roles(RoleId)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_omp_content_contents_app_instance_slug'
      AND object_id = OBJECT_ID(N'omp_content.contents')
)
BEGIN
    CREATE UNIQUE INDEX UX_omp_content_contents_app_instance_slug
        ON omp_content.contents(app_instance_id, slug);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_content_contents_app_instance_enabled'
      AND object_id = OBJECT_ID(N'omp_content.contents')
)
BEGIN
    CREATE INDEX IX_omp_content_contents_app_instance_enabled
        ON omp_content.contents(app_instance_id, is_enabled, sort_order, slug)
        INCLUDE(title, content_type, updated_at, updated_by);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_content_content_role_access_role'
      AND object_id = OBJECT_ID(N'omp_content.content_role_access')
)
BEGIN
    CREATE INDEX IX_omp_content_content_role_access_role
        ON omp_content.content_role_access(role_id, can_read, can_write)
        INCLUDE(content_id);
END
GO

IF OBJECT_ID(N'omp_content.Pages', N'U') IS NOT NULL
BEGIN
    INSERT INTO omp_content.contents(
        content_id,
        app_instance_id,
        slug,
        title,
        content_type,
        body,
        is_enabled,
        sort_order,
        created_at,
        created_by,
        updated_at,
        updated_by)
    SELECT p.PageId,
           p.AppInstanceId,
           p.Slug,
           p.Title,
           p.ContentFormat,
           p.Content,
           p.IsPublished,
           p.SortOrder,
           p.CreatedAtUtc,
           p.CreatedBy,
           p.UpdatedAtUtc,
           p.UpdatedBy
    FROM omp_content.Pages p
    WHERE p.IsDeleted = 0
      AND NOT EXISTS
      (
          SELECT 1
          FROM omp_content.contents c
          WHERE c.content_id = p.PageId
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM omp_content.contents c
          WHERE c.app_instance_id = p.AppInstanceId
            AND c.slug = p.Slug
      );
END
GO
