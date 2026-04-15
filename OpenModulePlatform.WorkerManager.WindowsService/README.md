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

Current limitations:

- does not yet publish observed runtime state or heartbeat back to OMP
- does not yet perform artifact download or installation
- does not yet include a portal UI for worker metadata administration

Important compatibility rule:

- classic service apps remain untouched unless they are explicitly registered in `omp.AppWorkerDefinitions`
- this keeps the legacy service-exe model intact while the manager-based runtime matures
