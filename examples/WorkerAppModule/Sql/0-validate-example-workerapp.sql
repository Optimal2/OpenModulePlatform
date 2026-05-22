SET NOCOUNT ON;

DECLARE @Missing int = 0;

;WITH RequiredTables(SchemaName, TableName) AS
(
    SELECT v.SchemaName, v.TableName
    FROM (VALUES
        (N'omp_example_workerapp', N'Configurations'),
        (N'omp_example_workerapp', N'Jobs'),
        (N'omp_example_workerapp', N'JobExecutions')
    ) AS v(SchemaName, TableName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredTables required
WHERE OBJECT_ID(required.SchemaName + N'.' + required.TableName, N'U') IS NULL;

;WITH RequiredColumns(SchemaName, TableName, ColumnName) AS
(
    SELECT v.SchemaName, v.TableName, v.ColumnName
    FROM (VALUES
        (N'omp_example_workerapp', N'Configurations', N'VersionNo'),
        (N'omp_example_workerapp', N'Configurations', N'ConfigJson'),
        (N'omp_example_workerapp', N'Jobs', N'RequestType'),
        (N'omp_example_workerapp', N'Jobs', N'PayloadJson'),
        (N'omp_example_workerapp', N'Jobs', N'Status'),
        (N'omp_example_workerapp', N'Jobs', N'Attempts'),
        (N'omp_example_workerapp', N'Jobs', N'RequestedUtc'),
        (N'omp_example_workerapp', N'Jobs', N'ClaimedByAppInstanceId'),
        (N'omp_example_workerapp', N'Jobs', N'UpdatedUtc'),
        (N'omp_example_workerapp', N'JobExecutions', N'JobId'),
        (N'omp_example_workerapp', N'JobExecutions', N'AppInstanceId'),
        (N'omp_example_workerapp', N'JobExecutions', N'Outcome')
    ) AS v(SchemaName, TableName, ColumnName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredColumns required
WHERE COL_LENGTH(required.SchemaName + N'.' + required.TableName, required.ColumnName) IS NULL;

IF OBJECT_ID(N'omp_example_workerapp.Configurations', N'U') IS NOT NULL
BEGIN
    SELECT @Missing = @Missing + CASE
        WHEN EXISTS (SELECT 1 FROM omp_example_workerapp.Configurations WHERE VersionNo = 0)
        THEN 0 ELSE 1 END;
END;

SELECT
    CAST(CASE WHEN @Missing = 0 THEN 1 ELSE 0 END AS bit) AS IsHealthy,
    CASE
        WHEN @Missing = 0 THEN N'Example WorkerApp module storage is healthy.'
        ELSE CONCAT(N'Example WorkerApp module storage is missing ', @Missing, N' required object(s).')
    END AS Message;
