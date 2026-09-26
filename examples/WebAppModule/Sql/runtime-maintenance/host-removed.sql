-- Event host-removed: the platform binds the HostId parameter (uniqueidentifier).
-- A lease held on a removed host can never be renewed; drop it.
IF OBJECT_ID(N'omp_example_webapp.RuntimeLeases', N'U') IS NOT NULL
    DELETE FROM omp_example_webapp.RuntimeLeases
    WHERE HostId = @HostId;

-- Worker leases are keyed by WorkerInstanceId; the host's worker rows still exist
-- when the step runs, so the allow-listed read of omp.WorkerInstances finds them.
IF OBJECT_ID(N'omp_example_webapp.RuntimeWorkerLeases', N'U') IS NOT NULL
    DELETE FROM omp_example_webapp.RuntimeWorkerLeases
    WHERE WorkerInstanceId IN (SELECT WorkerInstanceId FROM omp.WorkerInstances WHERE HostId = @HostId);
