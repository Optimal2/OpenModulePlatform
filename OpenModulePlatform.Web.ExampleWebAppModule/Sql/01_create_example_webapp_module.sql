-- File: OpenModulePlatform.Web.ExampleWebAppModule/Sql/01_create_example_webapp_module.sql
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_webapp_module')
    EXEC('CREATE SCHEMA [omp_example_webapp_module]');
GO

IF OBJECT_ID(N'omp_example_webapp_module.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_webapp_module.Configurations
    (
        ConfigId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VersionNo int NOT NULL CONSTRAINT DF_ExampleWeb_Config_VersionNo DEFAULT(0),
        ConfigJson nvarchar(max) NOT NULL,
        Comment nvarchar(400) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleWeb_Config_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM omp_example_webapp_module.Configurations WHERE VersionNo = 0)
BEGIN
    INSERT INTO omp_example_webapp_module.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"sampleSetting": true}', N'Initial example configuration', SUSER_SNAME());
END
GO
