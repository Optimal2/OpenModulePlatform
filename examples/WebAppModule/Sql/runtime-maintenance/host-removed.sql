-- Event host-removed: @HostId uniqueidentifier is bound by the platform.
-- A lease held on a removed host can never be renewed; drop it.
IF OBJECT_ID(N'omp_example_webapp.RuntimeLeases', N'U') IS NOT NULL
    DELETE FROM omp_example_webapp.RuntimeLeases
    WHERE HostId = @HostId;
