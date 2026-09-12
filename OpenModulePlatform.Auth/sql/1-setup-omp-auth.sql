-- File: OpenModulePlatform.Auth/sql/1-setup-omp-auth.sql
/*
OMP Auth module schema setup.

The Auth app's own data (users, providers, sessions) lives in the core omp
schema, so this schema holds only what the host expects every module to own:
the user log the Portal's user log page reads. Idempotent; safe to run on
every import.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF SCHEMA_ID(N'omp') IS NULL OR OBJECT_ID(N'omp.Modules', N'U') IS NULL
    THROW 51000, 'OMP core schema is missing. Run the OMP core setup/init scripts first.', 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_auth')
    EXEC('CREATE SCHEMA [omp_auth]');
GO

-- User log: one row per human-attributable event (who did what), stored as the
-- versioned JSON envelope defined in OpenModulePlatform.Web.Shared.ActivityLog.
-- The Portal's user log reads every module's ActivityLog table with one reader;
-- keep the shape identical across modules (ActivityLogSql.CreateTableStatement
-- is the canonical text).
IF OBJECT_ID(N'omp_auth.ActivityLog', N'U') IS NULL
BEGIN
    CREATE TABLE omp_auth.ActivityLog
    (
        ActivityLogId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_auth_ActivityLog PRIMARY KEY CLUSTERED,
        LoggedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_auth_ActivityLog_LoggedUtc DEFAULT (SYSUTCDATETIME()),
        OmpUserId int NULL,
        Entry nvarchar(max) NOT NULL CONSTRAINT CK_omp_auth_ActivityLog_Entry CHECK (ISJSON(Entry) = 1)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_omp_auth_ActivityLog_LoggedUtc' AND object_id = OBJECT_ID(N'omp_auth.ActivityLog'))
BEGIN
    CREATE INDEX IX_omp_auth_ActivityLog_LoggedUtc
    ON omp_auth.ActivityLog(LoggedUtc DESC, ActivityLogId DESC);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_omp_auth_ActivityLog_OmpUserId' AND object_id = OBJECT_ID(N'omp_auth.ActivityLog'))
BEGIN
    CREATE INDEX IX_omp_auth_ActivityLog_OmpUserId
    ON omp_auth.ActivityLog(OmpUserId, LoggedUtc DESC, ActivityLogId DESC);
END
GO
