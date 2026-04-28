# OpenModulePlatform.HostAgent.Runtime

Shared runtime for the OMP HostAgent Windows service.

Implemented in v1:

- host heartbeat publishing
- desired artifact discovery from OMP SQL
- immutable local artifact cache
- source-to-cache copy from a central artifact root
- file and directory SHA-256 verification
- provisioning status publishing to `omp.HostArtifactStates`

Not implemented in v1:

- HTTP/gRPC/named-pipe API for synchronous `EnsureArtifact` calls
- cleanup/retention of unused artifact versions
- package extraction for zip/nupkg artifacts
- signing/certificate validation
