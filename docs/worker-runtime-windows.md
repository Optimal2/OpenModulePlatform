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
