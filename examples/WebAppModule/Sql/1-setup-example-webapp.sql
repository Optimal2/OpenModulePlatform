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

-- Runtime rows this module keeps about platform objects. They illustrate the
-- runtimeMaintenance contract (docs/MODULE_DEFINITIONS.md): the platform never
-- touches these tables itself; the module's declared steps under
-- Sql/runtime-maintenance/ release them when a host, an artifact or an app
-- instance is removed.
IF OBJECT_ID(N'omp_example_webapp.RuntimeBindings', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_webapp.RuntimeBindings
    (
        RuntimeBindingId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_example_webapp_RuntimeBindings PRIMARY KEY,
        BindingKey nvarchar(100) NOT NULL,
        -- NOT NULL: a binding cannot outlive its app instance, so it blocks the delete instead.
        AppInstanceId uniqueidentifier NOT NULL,
        -- Nullable: a removed artifact only unpins the binding.
        ArtifactId int NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_example_webapp_RuntimeBindings_CreatedUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID(N'omp_example_webapp.RuntimeLeases', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_webapp.RuntimeLeases
    (
        RuntimeLeaseId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_example_webapp_RuntimeLeases PRIMARY KEY,
        AppInstanceId uniqueidentifier NOT NULL,
        HostId uniqueidentifier NOT NULL,
        ExpiresUtc datetime2(3) NOT NULL
    );
END
GO
