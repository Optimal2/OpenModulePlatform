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

IF OBJECT_ID(N'omp_content.Pages', N'U') IS NULL
BEGIN
    CREATE TABLE omp_content.Pages
    (
        PageId uniqueidentifier NOT NULL CONSTRAINT DF_omp_content_Pages_PageId DEFAULT NEWID(),
        AppInstanceId uniqueidentifier NOT NULL,
        PageKey nvarchar(100) NULL,
        Slug nvarchar(256) NOT NULL,
        Title nvarchar(200) NOT NULL,
        Summary nvarchar(500) NULL,
        MetaTitle nvarchar(200) NULL,
        MetaDescription nvarchar(500) NULL,
        ContentFormat nvarchar(20) NOT NULL CONSTRAINT DF_omp_content_Pages_ContentFormat DEFAULT(N'markdown'),
        Content nvarchar(max) NOT NULL,
        IsPublished bit NOT NULL CONSTRAINT DF_omp_content_Pages_IsPublished DEFAULT(0),
        PublishedAtUtc datetime2(3) NULL,
        SortOrder int NOT NULL CONSTRAINT DF_omp_content_Pages_SortOrder DEFAULT(0),
        IsDeleted bit NOT NULL CONSTRAINT DF_omp_content_Pages_IsDeleted DEFAULT(0),
        LastPublishedRevisionId uniqueidentifier NULL,
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_content_Pages_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NULL,
        UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_content_Pages_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
        UpdatedBy nvarchar(256) NULL,

        CONSTRAINT PK_omp_content_Pages PRIMARY KEY(PageId),
        CONSTRAINT FK_omp_content_Pages_AppInstance FOREIGN KEY(AppInstanceId)
            REFERENCES omp.AppInstances(AppInstanceId),
        CONSTRAINT CK_omp_content_Pages_ContentFormat CHECK(ContentFormat IN (N'markdown', N'html'))
    );
END
GO

IF OBJECT_ID(N'omp_content.PageRevisions', N'U') IS NULL
BEGIN
    CREATE TABLE omp_content.PageRevisions
    (
        RevisionId uniqueidentifier NOT NULL CONSTRAINT DF_omp_content_PageRevisions_RevisionId DEFAULT NEWID(),
        PageId uniqueidentifier NOT NULL,
        RevisionNumber int NOT NULL,
        Title nvarchar(200) NOT NULL,
        Slug nvarchar(256) NOT NULL,
        ContentFormat nvarchar(20) NOT NULL,
        Content nvarchar(max) NOT NULL,
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_content_PageRevisions_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NULL,
        ChangeNote nvarchar(500) NULL,

        CONSTRAINT PK_omp_content_PageRevisions PRIMARY KEY(RevisionId),
        CONSTRAINT FK_omp_content_PageRevisions_Page FOREIGN KEY(PageId)
            REFERENCES omp_content.Pages(PageId),
        CONSTRAINT UQ_omp_content_PageRevisions_Page_Revision UNIQUE(PageId, RevisionNumber),
        CONSTRAINT CK_omp_content_PageRevisions_ContentFormat CHECK(ContentFormat IN (N'markdown', N'html'))
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_omp_content_Pages_LastPublishedRevision'
      AND parent_object_id = OBJECT_ID(N'omp_content.Pages')
)
BEGIN
    ALTER TABLE omp_content.Pages
    ADD CONSTRAINT FK_omp_content_Pages_LastPublishedRevision
        FOREIGN KEY(LastPublishedRevisionId) REFERENCES omp_content.PageRevisions(RevisionId);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_omp_content_Pages_AppInstance_Slug_Active'
      AND object_id = OBJECT_ID(N'omp_content.Pages')
)
BEGIN
    CREATE UNIQUE INDEX UX_omp_content_Pages_AppInstance_Slug_Active
        ON omp_content.Pages(AppInstanceId, Slug)
        WHERE IsDeleted = 0;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_content_Pages_AppInstance_Published'
      AND object_id = OBJECT_ID(N'omp_content.Pages')
)
BEGIN
    CREATE INDEX IX_omp_content_Pages_AppInstance_Published
        ON omp_content.Pages(AppInstanceId, IsPublished, IsDeleted, SortOrder, Slug)
        INCLUDE(Title, PublishedAtUtc, UpdatedAtUtc);
END
GO
