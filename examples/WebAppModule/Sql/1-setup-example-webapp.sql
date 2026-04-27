-- File: examples/WebAppModule/Sql/1-setup-example-webapp.sql
/*
Creates the example Web App module schema and tables.

Prerequisite:
- Run ../../sql/1-setup-openmoduleplatform.sql first.
*/
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_webapp')
    EXEC('CREATE SCHEMA [omp_example_webapp]');
GO

IF OBJECT_ID(N'omp_example_webapp.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_webapp.Configurations
    (
        ConfigId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VersionNo int NOT NULL,
        ConfigJson nvarchar(max) NOT NULL,
        Comment nvarchar(400) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleWeb_Config_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NULL
    );
END
GO
