# OpenModulePlatform.WorkerManager.WindowsService

This project is the Windows Service host for the OMP worker runtime manager.

Current state:

- runs as a real Windows Service host
- supervises one child process per managed `AppInstanceId`
- starts `OpenModulePlatform.WorkerProcessHost` as an external child process
- passes runtime identity and plugin settings through command-line configuration overrides
- applies a basic restart policy with restart delay and restart window limits
- requests graceful shutdown through a named OS event and kills the child only if it does not exit in time
- supports two worker catalogs:
  - `Configuration` for conservative local bootstrap and testing
  - `OmpDatabase` for real discovery from `omp.AppInstances` on the current host
- publishes manager heartbeat back to `omp.Hosts` when running in `OmpDatabase` mode
- publishes observed worker runtime state back to `omp.WorkerInstanceRuntimeStates`, aggregated into `omp.AppInstanceRuntimeStates`
- records which artifact version each worker process was actually started from, and which WorkerProcessHost build it was launched with
- updates `omp.AppInstances.LastSeenUtc` while a manager-driven worker is observed as starting or running

OMP database discovery currently resolves workers by joining:

- `omp.AppInstances`
- `omp.Hosts`
- `omp.Apps`
- `omp.Artifacts`
- `omp.AppWorkerDefinitions`

The `omp.AppWorkerDefinitions` table is the minimal metadata contract for manager-driven plugin workers. It binds an app definition to:

- a runtime kind, currently `windows-worker-plugin`
- a `WorkerTypeKey`
- a plugin assembly path relative to `omp.AppInstances.InstallPath`

`omp.WorkerInstanceRuntimeStates` holds one row per worker instance and is the per-instance truth; `omp.AppInstanceRuntimeStates` is its aggregation to one row per app instance (worst sibling wins). Both store:

- observed lifecycle state
- process id
- start time
- manager-side heartbeat
- last exit time
- last exit code
- a short status message
- the artifact the process was actually started from: `RuntimeArtifactId` / `RuntimeArtifactVersion`
- the WorkerProcessHost build it was launched with: `RuntimeHostArtifactId` / `RuntimeHostArtifactVersion`

The four artifact columns are the runtime version witness (R12-F2). They are written only while a live process exists and are cleared when a state is downgraded for staleness, so a version in them always describes a process someone is currently observing. NULL therefore means "no running version can be stated" and the deployment diagnostics report it as unverifiable rather than as agreement. Read them with `scripts/diagnostics/Get-OmpDeploymentDrift.ps1` and `scripts/diagnostics/Get-OmpAppDeploymentDetail.ps1`.

Current limitations:

- does not yet perform artifact download or installation
- does not yet define a cross-platform worker manager model

Portal administration: `/admin/workers` combines app worker definitions with the
live worker runtime (metadata editing via `/admin/appworkeredit`); the older
`/admin/appworkers` and `/admin/workerruntime` routes redirect there.

Important compatibility rule:

- classic service apps remain untouched unless they are explicitly registered in `omp.AppWorkerDefinitions`
- this keeps the legacy service-exe model intact while the manager-based runtime matures
