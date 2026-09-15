-- File: examples/WorkerAppModule/Sql/1-setup-example-workerapp.sql
/*
Creates the example Worker App module schema and tables.

Prerequisite:
- Run ../../sql/1-setup-openmoduleplatform.sql first.
*/
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_workerapp')
    EXEC('CREATE SCHEMA [omp_example_workerapp]');
GO

IF OBJECT_ID(N'omp_example_workerapp.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_workerapp.Configurations
    (
        ConfigId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VersionNo int NOT NULL,
        ConfigJson nvarchar(max) NOT NULL,
        Comment nvarchar(400) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleWorker_Config_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NULL
    );
END
GO

IF OBJECT_ID(N'omp_example_workerapp.Jobs', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_workerapp.Jobs
    (
        JobId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        RequestType nvarchar(100) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        Status tinyint NOT NULL,
        Attempts int NOT NULL CONSTRAINT DF_ExampleWorker_Jobs_Attempts DEFAULT(0),
        RequestedUtc datetime2(3) NOT NULL,
        RequestedBy nvarchar(256) NULL,
        ClaimedByAppInstanceId uniqueidentifier NULL,
        ClaimedUtc datetime2(3) NULL,
        CompletedUtc datetime2(3) NULL,
        ResultJson nvarchar(max) NULL,
        LastError nvarchar(max) NULL,
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleWorker_Jobs_UpdatedUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID(N'omp_example_workerapp.JobExecutions', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_workerapp.JobExecutions
    (
        JobExecutionId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        JobId bigint NOT NULL,
        AppInstanceId uniqueidentifier NOT NULL,
        StartedUtc datetime2(3) NOT NULL,
        FinishedUtc datetime2(3) NULL,
        Outcome nvarchar(50) NOT NULL,
        ResultJson nvarchar(max) NULL,
        ErrorMessage nvarchar(max) NULL
    );
END
GO

-- User log. Written by the web app through the shared writer in
-- OpenModulePlatform.Web.Shared.ActivityLog. The Portal's user log reads every
-- module's ActivityLog table with one reader; keep the shape identical across
-- modules (ActivityLogSql.CreateTableStatement is the canonical text).
IF OBJECT_ID(N'omp_example_workerapp.ActivityLog', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_workerapp.ActivityLog
    (
        ActivityLogId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_example_workerapp_ActivityLog PRIMARY KEY CLUSTERED,
        LoggedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_example_workerapp_ActivityLog_LoggedUtc DEFAULT (SYSUTCDATETIME()),
        OmpUserId int NULL,
        Entry nvarchar(max) NOT NULL CONSTRAINT CK_omp_example_workerapp_ActivityLog_Entry CHECK (ISJSON(Entry) = 1)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_omp_example_workerapp_ActivityLog_LoggedUtc' AND object_id = OBJECT_ID(N'omp_example_workerapp.ActivityLog'))
BEGIN
    CREATE INDEX IX_omp_example_workerapp_ActivityLog_LoggedUtc
    ON omp_example_workerapp.ActivityLog(LoggedUtc DESC, ActivityLogId DESC);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_omp_example_workerapp_ActivityLog_OmpUserId' AND object_id = OBJECT_ID(N'omp_example_workerapp.ActivityLog'))
BEGIN
    CREATE INDEX IX_omp_example_workerapp_ActivityLog_OmpUserId
    ON omp_example_workerapp.ActivityLog(OmpUserId, LoggedUtc DESC, ActivityLogId DESC);
END
GO
