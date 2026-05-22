SET NOCOUNT ON;

DECLARE @Missing int = 0;

;WITH RequiredTables(SchemaName, TableName) AS
(
    SELECT v.SchemaName, v.TableName
    FROM (VALUES
        (N'omp_iframe', N'urls'),
        (N'omp_iframe', N'url_sets'),
        (N'omp_iframe', N'url_set_urls')
    ) AS v(SchemaName, TableName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredTables required
WHERE OBJECT_ID(required.SchemaName + N'.' + required.TableName, N'U') IS NULL;

;WITH RequiredColumns(SchemaName, TableName, ColumnName) AS
(
    SELECT v.SchemaName, v.TableName, v.ColumnName
    FROM (VALUES
        (N'omp_iframe', N'urls', N'id'),
        (N'omp_iframe', N'urls', N'url'),
        (N'omp_iframe', N'urls', N'displayname'),
        (N'omp_iframe', N'urls', N'allowed_roles'),
        (N'omp_iframe', N'urls', N'enabled'),
        (N'omp_iframe', N'url_sets', N'id'),
        (N'omp_iframe', N'url_sets', N'set_key'),
        (N'omp_iframe', N'url_sets', N'displayname'),
        (N'omp_iframe', N'url_sets', N'enabled'),
        (N'omp_iframe', N'url_set_urls', N'url_set_id'),
        (N'omp_iframe', N'url_set_urls', N'url_id'),
        (N'omp_iframe', N'url_set_urls', N'sort_order')
    ) AS v(SchemaName, TableName, ColumnName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredColumns required
WHERE COL_LENGTH(required.SchemaName + N'.' + required.TableName, required.ColumnName) IS NULL;

;WITH RequiredIndexes(ObjectName, IndexName) AS
(
    SELECT v.ObjectName, v.IndexName
    FROM (VALUES
        (N'omp_iframe.url_sets', N'UQ_omp_iframe_url_sets_set_key'),
        (N'omp_iframe.url_set_urls', N'PK_omp_iframe_url_set_urls')
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

IF OBJECT_ID(N'omp_iframe.urls', N'U') IS NOT NULL
BEGIN
    SELECT @Missing = @Missing + CASE
        WHEN EXISTS (SELECT 1 FROM omp_iframe.urls WHERE id = 1)
         AND EXISTS (SELECT 1 FROM omp_iframe.urls WHERE id = 2)
         AND EXISTS (SELECT 1 FROM omp_iframe.urls WHERE id = 3)
        THEN 0 ELSE 1 END;
END;

IF OBJECT_ID(N'omp_iframe.url_sets', N'U') IS NOT NULL
BEGIN
    SELECT @Missing = @Missing + CASE
        WHEN EXISTS (SELECT 1 FROM omp_iframe.url_sets WHERE set_key = N'default')
         AND EXISTS (SELECT 1 FROM omp_iframe.url_sets WHERE set_key = N'portal')
         AND EXISTS (SELECT 1 FROM omp_iframe.url_sets WHERE set_key = N'examples')
        THEN 0 ELSE 1 END;
END;

SELECT
    CAST(CASE WHEN @Missing = 0 THEN 1 ELSE 0 END AS bit) AS IsHealthy,
    CASE
        WHEN @Missing = 0 THEN N'iFrame module storage is healthy.'
        ELSE CONCAT(N'iFrame module storage is missing ', @Missing, N' required object(s).')
    END AS Message;
