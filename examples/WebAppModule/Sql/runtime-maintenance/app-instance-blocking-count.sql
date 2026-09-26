-- Event app-instance-blocking-count: @AppInstanceId uniqueidentifier is bound by the platform.
-- Read-only. Bindings are operator data that must not disappear as a side effect, so they
-- block the delete; Portal names them with the Description column.
IF OBJECT_ID(N'omp_example_webapp.RuntimeBindings', N'U') IS NOT NULL
    SELECT COUNT(*) AS BlockingCount,
           N'example runtime binding(s)' AS Description
    FROM omp_example_webapp.RuntimeBindings
    WHERE AppInstanceId = @AppInstanceId;
