-- File: OpenModulePlatform.Web.ContentWebAppModule/Sql/3-add-server-report-support.sql
/*
Adds ServerReport support to the Content Web App module.

Prerequisite:
- Run 1-setup-content-webapp.sql first.
*/
USE [OpenModulePlatform];
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
