# Windows worker runtime in OMP

This document describes the additive Windows worker runtime track in OpenModulePlatform.

## Goals

The Windows worker runtime introduces a general manager-driven model where:

- one Windows Service acts as a worker manager on a host
- the manager supervises one child process per `AppInstanceId`
- the child host loads worker logic from a plugin assembly
- the classic service-exe model remains valid in parallel

## Runtime identity

`AppInstanceId` is the runtime identity.

The manager discovers desired workers from `omp.AppInstances` and starts one child process for each eligible app instance.

## Minimal OMP metadata contract

The first OMP-backed implementation adds `omp.AppWorkerDefinitions`.

This table is intentionally small and generic. It answers the questions that the worker manager must know without changing the meaning of existing app instances:

- should this app be handled by the manager-driven plugin runtime?
- which worker factory key should the child process load?
- which plugin assembly should be loaded from the deployed install root?

Suggested semantics:

- `RuntimeKind`: identifies the runtime flavor, currently `windows-worker-plugin`
- `WorkerTypeKey`: logical plugin key used by `IWorkerModuleFactory`
- `PluginRelativePath`: path to the plugin assembly relative to `omp.AppInstances.InstallPath`

## Discovery rules in the current implementation

The database-backed worker catalog only returns app instances that satisfy all of the following:

- assigned to the current host through `omp.AppInstances.HostId`
- host is enabled
- app is enabled
- app instance is enabled
- app instance is allowed
- app instance desired state is the configured running state
- app instance has an artifact binding
- app instance has an install path
- artifact is enabled
- app definition has an enabled row in `omp.AppWorkerDefinitions` for the configured runtime kind

If no worker metadata row exists for an app, the manager ignores it. This is the compatibility boundary that protects classic service apps.

## Path resolution

The current manager resolves the plugin assembly path as:

`Path.Combine(AppInstances.InstallPath, AppWorkerDefinitions.PluginRelativePath)`

This keeps runtime selection app-level while still allowing the host to use the app instance's deployed install root.

## Observed runtime state

The manager-driven runtime now publishes observations to `omp.AppInstanceRuntimeStates`.

This table is manager-owned runtime telemetry, not desired state. It is updated additively beside `omp.AppInstances` and can evolve without changing the meaning of the core app-instance row.

Current columns capture:

- `ObservedState`
- `ProcessId`
- `StartedUtc`
- `LastSeenUtc`
- `LastExitUtc`
- `LastExitCode`
- `StatusMessage`

Current observed state values are:

- `0`: unknown
- `1`: starting
- `2`: running
- `3`: stopping
- `4`: stopped
- `5`: failed

## Heartbeat model

When the manager runs in `OmpDatabase` mode it also publishes:

- host heartbeat to `omp.Hosts.LastSeenUtc`
- worker heartbeat to `omp.AppInstances.LastSeenUtc` while a managed worker is observed as starting or running

This keeps compatibility with existing OMP views that already rely on `omp.AppInstances.LastSeenUtc`, while still adding a separate surface for manager-specific runtime details.


## Public example module

The public repository now includes a neutral reference module for this runtime track:

- `OpenModulePlatform.Web.ExampleWorkerAppModule`
- `OpenModulePlatform.Worker.ExampleWorkerAppModule`

The example keeps the familiar module pattern of a web administration surface plus background processing, but the background processing runs as a worker plugin loaded by `OpenModulePlatform.WorkerProcessHost` instead of as its own service executable.

## Portal administration

The Portal admin area now includes two worker-runtime pages:

- `App workers`: manage `omp.AppWorkerDefinitions` rows per app definition
- `Worker runtime`: view app instances selected into the worker-runtime track together with the latest observed runtime state

These pages are additive and do not change the classic service-app path.
