# OpenModulePlatform.HostAgent.WindowsService

HostAgent v1 is the host-local OMP service responsible for provisioning immutable artifact versions to a local cache.

Implemented in v1:

- runs as a Windows Service
- reads desired artifacts from OMP SQL
- provisions app-instance artifacts and explicit host artifact requirements
- verifies SHA-256 when `omp.Artifacts.Sha256` is populated
- writes provisioning state to `omp.HostArtifactStates`

Expected first use:

1. Publish an artifact to a central artifact root.
2. Register the artifact in `omp.Artifacts` with `RelativePath` pointing to the artifact directory or file.
3. Assign the artifact to an app instance, or create an explicit `omp.HostArtifactRequirements` row.
4. HostAgent copies the artifact to `HostAgent:LocalArtifactCacheRoot`.
5. Worker Manager can be configured to resolve app-instance artifacts from `omp.HostArtifactStates`.

Not implemented in v1:

- service API for synchronous artifact provisioning
- remote HTTP download
- package extraction
- retention/cleanup policy
