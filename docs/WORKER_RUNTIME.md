# Worker runtime status

This document summarizes the current state of the additive worker runtime track in the public repository.

The worker-runtime projects are now aligned on `.NET 10` together with the rest of the repository.

## Implemented projects

### OpenModulePlatform.WorkerManager.WindowsService

Implemented as a Windows Service manager that:

- discovers desired worker app instances from configuration or from OMP
- supervises one child process per `AppInstanceId`
- applies a basic restart policy
- requests graceful shutdown through named OS events
- publishes host heartbeat and observed runtime state back to OMP in database-backed mode

### OpenModulePlatform.WorkerProcessHost

Implemented as a general child host that:

- starts from `AppInstanceId`, `WorkerTypeKey`, `PluginAssemblyPath` and an optional shutdown event name
- loads the requested worker plugin assembly
- resolves a matching `IWorkerModuleFactory`
- creates a minimal execution context
- runs the worker module and returns a process exit code

### OpenModulePlatform.Worker.Abstractions

Implemented as the minimal shared contract layer for worker plugins.

### Public reference example

The repository now also includes a public reference module for the manager-driven runtime:

- `OpenModulePlatform.Web.ExampleWorkerAppModule`
- `OpenModulePlatform.Worker.ExampleWorkerAppModule`

This example shows how a neutral OMP module can expose a web administration surface while running its background processing through the worker manager and child host.

## OMP integration status

The current OMP-backed Windows implementation uses:

- `omp.AppInstances` as runtime identity and desired-state anchor
- `omp.AppWorkerDefinitions` as minimal plugin-runtime metadata
- `omp.AppInstanceRuntimeStates` as manager-owned observed runtime state
- `omp.Hosts.LastSeenUtc` and `omp.AppInstances.LastSeenUtc` for heartbeat compatibility

## Portal administration status

The public Portal now includes:

- `App workers` for app-level worker-runtime metadata in `omp.AppWorkerDefinitions`
- `Worker runtime` for operational visibility into managed app instances and their latest observed runtime state

These pages are schema-aware and degrade to empty lists until the database has been updated with the current SQL install script.

## Still out of scope

The public repository still does not implement:

- artifact download or installation by the worker manager
- a non-Windows worker manager
