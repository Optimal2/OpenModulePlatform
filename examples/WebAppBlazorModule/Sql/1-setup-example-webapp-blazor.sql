-- File: examples/WebAppBlazorModule/sql/1-setup-example-webapp-blazor.sql
/*
Creates the example Web App Blazor module schema and tables.

Prerequisite:
- Run ../../sql/1-setup-openmoduleplatform.sql first.
*/
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_webapp_blazor')
    EXEC('CREATE SCHEMA [omp_example_webapp_blazor]');
GO

IF OBJECT_ID(N'omp_example_webapp_blazor.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_webapp_blazor.Configurations
    (
        ConfigId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VersionNo int NOT NULL,
        ConfigJson nvarchar(max) NOT NULL,
        Comment nvarchar(400) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleWebBlazor_Config_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NULL
    );
END
GO

-- User log. Written by the module through the shared writer in
-- OpenModulePlatform.Web.Shared.ActivityLog. The Portal's user log reads every
-- module's ActivityLog table with one reader; keep the shape identical across
-- modules (ActivityLogSql.CreateTableStatement is the canonical text).
IF OBJECT_ID(N'omp_example_webapp_blazor.ActivityLog', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_webapp_blazor.ActivityLog
    (
        ActivityLogId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_example_webapp_blazor_ActivityLog PRIMARY KEY CLUSTERED,
        LoggedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_example_webapp_blazor_ActivityLog_LoggedUtc DEFAULT (SYSUTCDATETIME()),
        OmpUserId int NULL,
        Entry nvarchar(max) NOT NULL CONSTRAINT CK_omp_example_webapp_blazor_ActivityLog_Entry CHECK (ISJSON(Entry) = 1)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_omp_example_webapp_blazor_ActivityLog_LoggedUtc' AND object_id = OBJECT_ID(N'omp_example_webapp_blazor.ActivityLog'))
BEGIN
    CREATE INDEX IX_omp_example_webapp_blazor_ActivityLog_LoggedUtc
    ON omp_example_webapp_blazor.ActivityLog(LoggedUtc DESC, ActivityLogId DESC);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_omp_example_webapp_blazor_ActivityLog_OmpUserId' AND object_id = OBJECT_ID(N'omp_example_webapp_blazor.ActivityLog'))
BEGIN
    CREATE INDEX IX_omp_example_webapp_blazor_ActivityLog_OmpUserId
    ON omp_example_webapp_blazor.ActivityLog(OmpUserId, LoggedUtc DESC, ActivityLogId DESC);
END
GO
