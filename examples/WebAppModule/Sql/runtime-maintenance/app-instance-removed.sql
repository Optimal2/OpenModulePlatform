-- Event app-instance-removed: the platform binds the AppInstanceId parameter (uniqueidentifier).
-- Runs only after every app-instance-blocking-count step reported zero.
IF OBJECT_ID(N'omp_example_webapp.RuntimeLeases', N'U') IS NOT NULL
    DELETE FROM omp_example_webapp.RuntimeLeases
    WHERE AppInstanceId = @AppInstanceId;

-- Worker leases are keyed by WorkerInstanceId; the app instance's worker rows are
-- found through the allow-listed read of omp.WorkerInstances.
IF OBJECT_ID(N'omp_example_webapp.RuntimeWorkerLeases', N'U') IS NOT NULL
    DELETE FROM omp_example_webapp.RuntimeWorkerLeases
    WHERE WorkerInstanceId IN (SELECT WorkerInstanceId FROM omp.WorkerInstances WHERE AppInstanceId = @AppInstanceId);
