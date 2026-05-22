SET NOCOUNT ON;

DECLARE @Missing int = 0;

;WITH RequiredTables(SchemaName, TableName) AS
(
    SELECT v.SchemaName, v.TableName
    FROM (VALUES
        (N'omp_content', N'contents'),
        (N'omp_content', N'content_role_access')
    ) AS v(SchemaName, TableName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredTables required
WHERE OBJECT_ID(required.SchemaName + N'.' + required.TableName, N'U') IS NULL;

;WITH RequiredColumns(SchemaName, TableName, ColumnName) AS
(
    SELECT v.SchemaName, v.TableName, v.ColumnName
    FROM (VALUES
        (N'omp_content', N'contents', N'content_id'),
        (N'omp_content', N'contents', N'app_instance_id'),
        (N'omp_content', N'contents', N'slug'),
        (N'omp_content', N'contents', N'title'),
        (N'omp_content', N'contents', N'content_type'),
        (N'omp_content', N'contents', N'body'),
        (N'omp_content', N'contents', N'server_report_key'),
        (N'omp_content', N'contents', N'is_enabled'),
        (N'omp_content', N'contents', N'sort_order'),
        (N'omp_content', N'contents', N'created_at'),
        (N'omp_content', N'contents', N'created_by'),
        (N'omp_content', N'contents', N'updated_at'),
        (N'omp_content', N'contents', N'updated_by'),
        (N'omp_content', N'content_role_access', N'content_id'),
        (N'omp_content', N'content_role_access', N'role_id'),
        (N'omp_content', N'content_role_access', N'can_read'),
        (N'omp_content', N'content_role_access', N'can_write')
    ) AS v(SchemaName, TableName, ColumnName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredColumns required
WHERE COL_LENGTH(required.SchemaName + N'.' + required.TableName, required.ColumnName) IS NULL;

;WITH RequiredIndexes(ObjectName, IndexName) AS
(
    SELECT v.ObjectName, v.IndexName
    FROM (VALUES
        (N'omp_content.contents', N'UX_omp_content_contents_app_instance_slug'),
        (N'omp_content.contents', N'IX_omp_content_contents_app_instance_enabled'),
        (N'omp_content.content_role_access', N'IX_omp_content_content_role_access_role')
    ) AS v(ObjectName, IndexName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredIndexes required
WHERE NOT EXISTS
(
    SELECT 1
    FROM sys.indexes idx
    WHERE idx.object_id = OBJECT_ID(required.ObjectName)
      AND idx.name = required.IndexName
);

;WITH RequiredChecks(ObjectName, CheckName) AS
(
    SELECT v.ObjectName, v.CheckName
    FROM (VALUES
        (N'omp_content.contents', N'CK_omp_content_contents_content_type'),
        (N'omp_content.contents', N'CK_omp_content_contents_server_report_key')
    ) AS v(ObjectName, CheckName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredChecks required
WHERE NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints chk
    WHERE chk.parent_object_id = OBJECT_ID(required.ObjectName)
      AND chk.name = required.CheckName
);

SELECT
    CAST(CASE WHEN @Missing = 0 THEN 1 ELSE 0 END AS bit) AS IsHealthy,
    CASE
        WHEN @Missing = 0 THEN N'Content module storage is healthy.'
        ELSE CONCAT(N'Content module storage is missing ', @Missing, N' required object(s).')
    END AS Message;
