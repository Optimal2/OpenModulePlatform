SET NOCOUNT ON;

DECLARE @Missing int = 0;

IF OBJECT_ID(N'omp_example_webapp_blazor.Configurations', N'U') IS NULL
    SELECT @Missing = @Missing + 1;

;WITH RequiredColumns(SchemaName, TableName, ColumnName) AS
(
    SELECT v.SchemaName, v.TableName, v.ColumnName
    FROM (VALUES
        (N'omp_example_webapp_blazor', N'Configurations', N'ConfigId'),
        (N'omp_example_webapp_blazor', N'Configurations', N'VersionNo'),
        (N'omp_example_webapp_blazor', N'Configurations', N'ConfigJson'),
        (N'omp_example_webapp_blazor', N'Configurations', N'Comment'),
        (N'omp_example_webapp_blazor', N'Configurations', N'CreatedUtc'),
        (N'omp_example_webapp_blazor', N'Configurations', N'CreatedBy')
    ) AS v(SchemaName, TableName, ColumnName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredColumns required
WHERE COL_LENGTH(required.SchemaName + N'.' + required.TableName, required.ColumnName) IS NULL;

IF OBJECT_ID(N'omp_example_webapp_blazor.Configurations', N'U') IS NOT NULL
BEGIN
    SELECT @Missing = @Missing + CASE
        WHEN EXISTS (SELECT 1 FROM omp_example_webapp_blazor.Configurations WHERE VersionNo = 0)
        THEN 0 ELSE 1 END;
END;

-- The user log table, its columns and both indexes: an installation from
-- before it gets them on the next import.
IF OBJECT_ID(N'omp_example_webapp_blazor.ActivityLog', N'U') IS NULL
    SELECT @Missing = @Missing + 1;

;WITH RequiredLogColumns(ColumnName) AS
(
    SELECT v.ColumnName
    FROM (VALUES (N'ActivityLogId'), (N'LoggedUtc'), (N'OmpUserId'), (N'Entry')) AS v(ColumnName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredLogColumns required
WHERE COL_LENGTH(N'omp_example_webapp_blazor.ActivityLog', required.ColumnName) IS NULL;

;WITH RequiredLogIndexes(IndexName) AS
(
    SELECT v.IndexName
    FROM (VALUES (N'IX_omp_example_webapp_blazor_ActivityLog_LoggedUtc'), (N'IX_omp_example_webapp_blazor_ActivityLog_OmpUserId')) AS v(IndexName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredLogIndexes required
WHERE NOT EXISTS
(
    SELECT 1
    FROM sys.indexes idx
    WHERE idx.object_id = OBJECT_ID(N'omp_example_webapp_blazor.ActivityLog')
      AND idx.name = required.IndexName
);

SELECT
    CAST(CASE WHEN @Missing = 0 THEN 1 ELSE 0 END AS bit) AS IsHealthy,
    CASE
        WHEN @Missing = 0 THEN N'Example Blazor module storage is healthy.'
        ELSE CONCAT(N'Example Blazor module storage is missing ', @Missing, N' required object(s).')
    END AS Message;
