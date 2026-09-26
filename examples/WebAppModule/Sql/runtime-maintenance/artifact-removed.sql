-- Event artifact-removed: the platform binds the ArtifactId parameter (int).
-- A binding pinned to a removed artifact falls back to the app instance's current artifact.
IF OBJECT_ID(N'omp_example_webapp.RuntimeBindings', N'U') IS NOT NULL
    UPDATE omp_example_webapp.RuntimeBindings
    SET ArtifactId = NULL
    WHERE ArtifactId = @ArtifactId;
