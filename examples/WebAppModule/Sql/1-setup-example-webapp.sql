-- File: examples/WebAppModule/sql/1-setup-example-webapp.sql
/*
Creates the example Web App module schema and tables.

Prerequisite:
- Run ../../sql/1-setup-openmoduleplatform.sql first.
*/
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

-- User log. Written by the module through the shared writer in
-- OpenModulePlatform.Web.Shared.ActivityLog. The Portal's user log reads every
-- module's ActivityLog table with one reader; keep the shape identical across
-- modules (ActivityLogSql.CreateTableStatement is the canonical text).
IF OBJECT_ID(N'omp_example_webapp.ActivityLog', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_webapp.ActivityLog
    (
        ActivityLogId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_example_webapp_ActivityLog PRIMARY KEY CLUSTERED,
        LoggedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_example_webapp_ActivityLog_LoggedUtc DEFAULT (SYSUTCDATETIME()),
        OmpUserId int NULL,
        Entry nvarchar(max) NOT NULL CONSTRAINT CK_omp_example_webapp_ActivityLog_Entry CHECK (ISJSON(Entry) = 1)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_omp_example_webapp_ActivityLog_LoggedUtc' AND object_id = OBJECT_ID(N'omp_example_webapp.ActivityLog'))
BEGIN
    CREATE INDEX IX_omp_example_webapp_ActivityLog_LoggedUtc
    ON omp_example_webapp.ActivityLog(LoggedUtc DESC, ActivityLogId DESC);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_omp_example_webapp_ActivityLog_OmpUserId' AND object_id = OBJECT_ID(N'omp_example_webapp.ActivityLog'))
BEGIN
    CREATE INDEX IX_omp_example_webapp_ActivityLog_OmpUserId
    ON omp_example_webapp.ActivityLog(OmpUserId, LoggedUtc DESC, ActivityLogId DESC);
END
GO
