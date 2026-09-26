-- Event app-instance-removed: @AppInstanceId uniqueidentifier is bound by the platform.
-- Runs only after every app-instance-blocking-count step reported zero.
IF OBJECT_ID(N'omp_example_webapp.RuntimeLeases', N'U') IS NOT NULL
    DELETE FROM omp_example_webapp.RuntimeLeases
    WHERE AppInstanceId = @AppInstanceId;
