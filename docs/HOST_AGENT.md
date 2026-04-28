# HostAgent

HostAgent is host-local OMP/ODV infrastructure. It is intentionally generic and is not tied to a specific module such as IbsPackager.

## Implemented

- Windows Service project: `OpenModulePlatform.HostAgent.WindowsService`
- Shared runtime project: `OpenModulePlatform.HostAgent.Runtime`
- Host heartbeat via `omp.Hosts.LastSeenUtc`
- Desired artifact discovery from:
  - app instances assigned to the host
  - worker instances assigned to the host
  - explicit `omp.HostArtifactRequirements`
- Immutable local artifact cache under `HostAgent:LocalArtifactCacheRoot`
- Central artifact root resolution via `HostAgent:CentralArtifactRoot`
- File and directory SHA-256 verification
- Provisioning state in `omp.HostArtifactStates`
- Local named-pipe RPC for synchronous artifact provisioning:
  - operation: `ensureArtifact`
  - request fields: `artifactId`, optional `desiredLocalPath`
  - response fields: `success`, `state`, `localPath`, `contentSha256`, `errorMessage`

## SQL objects

- `omp.HostArtifactRequirements`
- `omp.HostArtifactStates`
- `omp.WorkerInstances`
- `omp.WorkerInstanceRuntimeStates`

`omp.WorkerInstances` introduces process-level desired state below an `omp.AppInstances` row. This supports modules where one app instance needs multiple isolated worker processes, for example one process per channel.

## Worker Manager integration

Worker Manager can be configured to call HostAgent before starting a worker process if the plugin artifact is missing locally.

```json
{
  "WorkerManager": {
    "HostAgentRpc": {
      "Enabled": true,
      "PipeName": "",
      "TimeoutSeconds": 60
    }
  }
}
```

An empty `PipeName` resolves to:

```text
OpenModulePlatform.HostAgent.{HostKey}
```

The manager still owns process lifecycle. HostAgent only provisions artifacts and reports host/artifact state.

## Artifact layout

Recommended central layout:

```text
\\server\omp-artifacts\worker\ibs-packager\1.0.0\...
\\server\omp-artifacts\channel-type\file-drop\0.1.0\...
\\server\omp-artifacts\channel-type\file-drop\0.2.0\...
```

Recommended local layout is managed by HostAgent:

```text
D:\OMP\Artifacts\worker\ibs-packager\1.0.0\...
D:\OMP\Artifacts\channel-type\file-drop\0.1.0\...
D:\OMP\Artifacts\channel-type\file-drop\0.2.0\...
```

## Stabilization note

The named-pipe RPC response writer uses an `async Task` method and awaits `StreamWriter.WriteLineAsync(...)` directly. This avoids SDK-specific return-type issues around `WriteLineAsync` and keeps the implementation compatible with the .NET 10 SDK used by the project.

## Not implemented yet

- package extraction from archive files
- HTTP/S3/Azure Blob download sources
- artifact retention/cleanup
- signing/certificate verification
- remote HostAgent management API
