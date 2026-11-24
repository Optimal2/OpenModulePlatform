# Windows worker runtime in OMP

This document summarizes the Windows-specific process model. The complete
contract, including worker provisioning and placement rules, is in
[`WORKER_RUNTIME.md`](WORKER_RUNTIME.md). The runtime targets `.NET 10`; the
classic standalone Windows-service model remains supported in parallel.

## Process and identity model

`OpenModulePlatform.WorkerManager.WindowsService` is the host-local Windows
service. It discovers desired workers, starts one
`OpenModulePlatform.WorkerProcessHost` process per `WorkerInstanceId`, supervises
restarts, and requests graceful shutdown through a named OS event.

`AppInstanceId` remains the module-facing runtime identity in `omp.AppInstances`.
A `omp.WorkerInstances` row gives each supervised process its own
`WorkerInstanceId`, `WorkerInstanceKey`, optional host and artifact binding, and
runtime configuration. For backward compatibility, an app instance with no
explicit worker rows receives one implicit worker whose ID equals the app
instance ID. Creating explicit worker rows disables that fallback, including
when the explicit rows are disabled.

The manager resolves the worker's plugin from the deployed install root and the
`PluginRelativePath` in `omp.AppWorkerDefinitions`. The app-level metadata row
also supplies `RuntimeKind` (`windows-worker-plugin`) and `WorkerTypeKey`, which
the child host uses to resolve an `IWorkerModuleFactory` from the plugin.

## Discovery and placement

Database discovery (`OmpDatabaseWorkerInstanceCatalog`) starts from explicit
`omp.WorkerInstances` rows for the configured host key, with one implicit row per
`omp.AppInstances` row where no explicit worker rows exist. A row is selected only
when the app/worker rows are enabled and allowed with `DesiredState` equal to the
configured `WorkerManager:OmpDatabase:RunningDesiredState`, the host is pinned or
the role placement resolves to exactly one host, the bound artifact is enabled with
a runtime-compatible package type, an install path and a non-empty
`PluginRelativePath` resolve inside it, and an enabled app-worker definition exists
for the configured runtime kind.

An explicit worker `HostId` takes precedence over the app instance's `HostId`.
Without a concrete host pin, the selected host role must resolve to exactly one
active, enabled assigned host. An ambiguous role placement is rejected and an
already managed worker is stopped on reconciliation. Apps without an
`omp.AppWorkerDefinitions` row are not selected, which keeps classic service
apps outside this runtime.

## Observed state and heartbeats

`omp.WorkerInstanceRuntimeStates` stores one observed row per worker instance.
`omp.AppInstanceRuntimeStates` aggregates sibling workers to one app-level row,
with the worst sibling state winning. Observations include lifecycle state,
process ID, start and heartbeat times, exit information, and a status message.
`WorkerObservedStates` defines the lifecycle values `0` Unknown, `1` Starting,
`2` Running, `3` Stopping, `4` Stopped, `5` Failed and `6` Draining.

While a live process exists, the runtime rows also record the worker artifact
and WorkerProcessHost artifact from which it was started. Those version-witness
fields are cleared when the observation becomes stale; a null value means that
no currently running version can be stated.

In `OmpDatabase` catalog mode, the manager also updates host heartbeat in
`omp.Hosts.LastSeenUtc` and app heartbeat in `omp.AppInstances.LastSeenUtc` while
a managed worker is starting or running.

## Plugin compatibility and administration

A worker plugin can require a minimum WorkerProcessHost artifact version through
`minWorkerHostVersion` in `omp-components.json`. Packaging writes the generated
`omp-worker-plugin.json`; the child host checks that requirement before loading
the plugin assembly.

Portal administration is at `/admin/workers`. It combines app-worker metadata
with live worker-runtime observations. The older `/admin/appworkers` and
`/admin/workerruntime` routes redirect there.

The neutral implementation example is under `examples/WorkerAppModule/`:

- `WebApp/OpenModulePlatform.Web.ExampleWorkerAppModule.csproj`
- `WorkerApp/OpenModulePlatform.Worker.ExampleWorkerAppModule.csproj`
