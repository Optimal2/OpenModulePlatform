-- File: OpenModulePlatform.Web.ExampleServiceAppModule/Sql/01_create_example_serviceapp_module.sql
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_serviceapp_module')
    EXEC('CREATE SCHEMA [omp_example_serviceapp_module]');
GO

IF OBJECT_ID(N'omp_example_serviceapp_module.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_serviceapp_module.Configurations
    (
        ConfigId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VersionNo int NOT NULL CONSTRAINT DF_ExampleService_Config_VersionNo DEFAULT(0),
        ConfigJson nvarchar(max) NOT NULL,
        Comment nvarchar(400) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleService_Config_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NULL
    );
END
GO

IF OBJECT_ID(N'omp_example_serviceapp_module.Jobs', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_serviceapp_module.Jobs
    (
        JobId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        RequestType nvarchar(100) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        Status tinyint NOT NULL,
        Attempts int NOT NULL CONSTRAINT DF_ExampleService_Jobs_Attempts DEFAULT(0),
        RequestedUtc datetime2(3) NOT NULL,
        RequestedBy nvarchar(256) NULL,
        ClaimedByHostInstallationId uniqueidentifier NULL,
        ClaimedUtc datetime2(3) NULL,
        CompletedUtc datetime2(3) NULL,
        ResultJson nvarchar(max) NULL,
        LastError nvarchar(max) NULL,
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleService_Jobs_UpdatedUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID(N'omp_example_serviceapp_module.JobExecutions', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_serviceapp_module.JobExecutions
    (
        JobExecutionId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        JobId bigint NOT NULL,
        HostInstallationId uniqueidentifier NOT NULL,
        StartedUtc datetime2(3) NOT NULL,
        FinishedUtc datetime2(3) NULL,
        Outcome nvarchar(50) NOT NULL,
        ResultJson nvarchar(max) NULL,
        ErrorMessage nvarchar(max) NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM omp_example_serviceapp_module.Configurations WHERE VersionNo = 0)
BEGIN
    INSERT INTO omp_example_serviceapp_module.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"scanBatchSize": 1, "sampleMode": true}', N'Initial example service configuration', SUSER_SNAME());
END
GO
