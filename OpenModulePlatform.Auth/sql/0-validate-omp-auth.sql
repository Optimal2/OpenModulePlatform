-- File: OpenModulePlatform.Auth/sql/0-validate-omp-auth.sql
/*
OMP Auth module storage validation. Reports whether the module-owned schema
objects exist; it never creates or changes anything.

It MUST return exactly one row with an IsHealthy bit and a Message, and it MUST
be read-only: the import blocks any validation script whose text matches a list
of writing keywords, comments included. When it reports unhealthy, the import
re-runs the module's idempotent setup scripts, which is how an existing
installation receives the user log table.
*/
SET NOCOUNT ON;

DECLARE @Missing int = 0;

IF SCHEMA_ID(N'omp_auth') IS NULL
    SET @Missing = @Missing + 1;

;WITH RequiredTables(SchemaName, TableName) AS
(
    SELECT v.SchemaName, v.TableName
    FROM (VALUES
        (N'omp_auth', N'ActivityLog')
    ) AS v(SchemaName, TableName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredTables required
WHERE OBJECT_ID(required.SchemaName + N'.' + required.TableName, N'U') IS NULL;

;WITH RequiredColumns(SchemaName, TableName, ColumnName) AS
(
    SELECT v.SchemaName, v.TableName, v.ColumnName
    FROM (VALUES
        (N'omp_auth', N'ActivityLog', N'ActivityLogId'),
        (N'omp_auth', N'ActivityLog', N'LoggedUtc'),
        (N'omp_auth', N'ActivityLog', N'OmpUserId'),
        (N'omp_auth', N'ActivityLog', N'Entry')
    ) AS v(SchemaName, TableName, ColumnName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredColumns required
WHERE OBJECT_ID(required.SchemaName + N'.' + required.TableName, N'U') IS NOT NULL
  AND COL_LENGTH(required.SchemaName + N'.' + required.TableName, required.ColumnName) IS NULL;

;WITH RequiredIndexes(ObjectName, IndexName) AS
(
    SELECT v.ObjectName, v.IndexName
    FROM (VALUES
        (N'omp_auth.ActivityLog', N'IX_omp_auth_ActivityLog_LoggedUtc'),
        (N'omp_auth.ActivityLog', N'IX_omp_auth_ActivityLog_OmpUserId')
    ) AS v(ObjectName, IndexName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredIndexes required
WHERE OBJECT_ID(required.ObjectName, N'U') IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1
      FROM sys.indexes i
      WHERE i.object_id = OBJECT_ID(required.ObjectName)
        AND i.name = required.IndexName
  );

SELECT
    CAST(CASE WHEN @Missing = 0 THEN 1 ELSE 0 END AS bit) AS IsHealthy,
    CASE
        WHEN @Missing = 0 THEN N'OMP Auth module storage is healthy.'
        ELSE CONCAT(N'OMP Auth module storage is missing ', @Missing, N' required object(s).')
    END AS Message;
