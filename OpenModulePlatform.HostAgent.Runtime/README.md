# OpenModulePlatform.HostAgent.Runtime

Shared runtime for the OMP HostAgent Windows service
(`OpenModulePlatform.HostAgent.WindowsService`). The service project hosts the
engine; this project contains the engine itself and everything it drives.

The authoritative feature description, configuration reference and operator
runbook live in `docs/HOST_AGENT.md`. In short, the runtime provides:

- host heartbeat and per-host runtime state publishing
- desired artifact discovery from OMP SQL, an immutable local artifact cache,
  SHA-256 verification and provisioning state in `omp.HostArtifactStates`
- artifact package import from the import folder, including universal module
  packages and package extraction (`ArtifactZipImportService`)
- IIS web app and Windows service app deployment
  (`WebAppDeploymentService`, `ServiceAppDeploymentService`), with
  configuration continuity between artifact versions
- the deploy-set consistency check (`DeploySetConsistencyService`, ADR 0002)
- the database-backed job queue (`HostAgentJobProcessor`) for artifact
  retention and cache cleanup, maintenance scans and cleanups, and Portal
  health operations
- runtime file mirrors (`HostAgentFileMirrorService`)
- HostAgent self-upgrade (`HostAgentSelfUpgradeService`), which reads the
  service account password from the host-local credential store
- the Windows DPAPI-backed credential store
  (`HostAgentCredentialStoreService`) used for service, app-pool and
  self-upgrade identities
- Portal health monitoring (`WebAppHealthMonitor`) and host resource
  telemetry (`HostResourceCollector`)

Still not implemented: remote (HTTP/blob) artifact download sources, artifact
signing/certificate verification, and a remote management API beyond the local
named-pipe RPC. See the "Not implemented yet" section in `docs/HOST_AGENT.md`.
