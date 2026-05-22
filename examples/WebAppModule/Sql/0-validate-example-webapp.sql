SET NOCOUNT ON;

DECLARE @Missing int = 0;

IF OBJECT_ID(N'omp_example_webapp.Configurations', N'U') IS NULL
    SELECT @Missing = @Missing + 1;

;WITH RequiredColumns(SchemaName, TableName, ColumnName) AS
(
    SELECT v.SchemaName, v.TableName, v.ColumnName
    FROM (VALUES
        (N'omp_example_webapp', N'Configurations', N'ConfigId'),
        (N'omp_example_webapp', N'Configurations', N'VersionNo'),
        (N'omp_example_webapp', N'Configurations', N'ConfigJson'),
        (N'omp_example_webapp', N'Configurations', N'Comment'),
        (N'omp_example_webapp', N'Configurations', N'CreatedUtc'),
        (N'omp_example_webapp', N'Configurations', N'CreatedBy')
    ) AS v(SchemaName, TableName, ColumnName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredColumns required
WHERE COL_LENGTH(required.SchemaName + N'.' + required.TableName, required.ColumnName) IS NULL;

IF OBJECT_ID(N'omp_example_webapp.Configurations', N'U') IS NOT NULL
BEGIN
    SELECT @Missing = @Missing + CASE
        WHEN EXISTS (SELECT 1 FROM omp_example_webapp.Configurations WHERE VersionNo = 0)
        THEN 0 ELSE 1 END;
END;

SELECT
    CAST(CASE WHEN @Missing = 0 THEN 1 ELSE 0 END AS bit) AS IsHealthy,
    CASE
        WHEN @Missing = 0 THEN N'Example WebApp module storage is healthy.'
        ELSE CONCAT(N'Example WebApp module storage is missing ', @Missing, N' required object(s).')
    END AS Message;
