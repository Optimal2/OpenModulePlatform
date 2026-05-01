-- File: OpenModulePlatform.Web.iFrameWebAppModule/sql/1-setup-iframe-webapp.sql
/*
Creates the iFrame Web App module schema and tables.

Prerequisite:
- Run ../../sql/1-setup-openmoduleplatform.sql first.
*/
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_iframe')
    EXEC('CREATE SCHEMA [omp_iframe]');
GO

IF OBJECT_ID(N'omp_iframe.urls', N'U') IS NULL
BEGIN
    CREATE TABLE omp_iframe.urls
    (
        [id] int IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_iframe_urls PRIMARY KEY,
        [url] nvarchar(500) NOT NULL,
        [displayname] nvarchar(200) NOT NULL,
        [allowed_roles] nvarchar(500) NULL,
        [enabled] bit NOT NULL CONSTRAINT DF_omp_iframe_urls_enabled DEFAULT(1)
    );
END
GO

IF OBJECT_ID(N'omp_iframe.url_sets', N'U') IS NULL
BEGIN
    CREATE TABLE omp_iframe.url_sets
    (
        [id] int IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_iframe_url_sets PRIMARY KEY,
        [set_key] nvarchar(100) NOT NULL,
        [displayname] nvarchar(200) NOT NULL,
        [enabled] bit NOT NULL CONSTRAINT DF_omp_iframe_url_sets_enabled DEFAULT(1),
        CONSTRAINT UQ_omp_iframe_url_sets_set_key UNIQUE([set_key])
    );
END
GO

IF OBJECT_ID(N'omp_iframe.url_set_urls', N'U') IS NULL
BEGIN
    CREATE TABLE omp_iframe.url_set_urls
    (
        [url_set_id] int NOT NULL,
        [url_id] int NOT NULL,
        [sort_order] int NOT NULL CONSTRAINT DF_omp_iframe_url_set_urls_sort_order DEFAULT(0),
        CONSTRAINT PK_omp_iframe_url_set_urls PRIMARY KEY([url_set_id], [url_id]),
        CONSTRAINT FK_omp_iframe_url_set_urls_url_set FOREIGN KEY([url_set_id]) REFERENCES omp_iframe.url_sets([id]),
        CONSTRAINT FK_omp_iframe_url_set_urls_url FOREIGN KEY([url_id]) REFERENCES omp_iframe.urls([id])
    );
END
GO
