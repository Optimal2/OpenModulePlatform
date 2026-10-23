# Worker runtime status

This document summarizes the current state of the additive worker runtime track in the public repository.

The worker-runtime projects are now aligned on `.NET 10` together with the rest of the repository.

Workers with channel types must follow the [channel-type reconciliation contract](CHANNEL_TYPE_RECONCILE.md), including its startup, version-recovery and artifact-requirement call sites.

## Implemented projects

### OpenModulePlatform.WorkerManager.WindowsService

Implemented as a Windows Service manager that:

- discovers desired worker app instances from configuration or from OMP
- supervises one child process per `WorkerInstanceId`
- is deployed as the `omp_core` `service-app` artifact `omp_workermanager`
- applies a basic restart policy
- requests graceful shutdown through named OS events
- cleans orphaned `WorkerProcessHost` processes at startup; by default it kills
  the full orphan process tree, configurable with
  `WorkerManager:CleanupOrphansKillProcessTree`
- asynchronously captures a bounded tail of each child process's standard error
  and includes it in the manager exit log and failed runtime observation, so a
  startup rejection is not reduced to an unexplained exit code
- publishes host heartbeat and observed runtime state back to OMP in database-backed mode

### OpenModulePlatform.WorkerProcessHost

Implemented as a general child host that:

- starts from `AppInstanceId`, `WorkerTypeKey`, `PluginAssemblyPath` and an optional shutdown event name
- loads the requested worker plugin assembly
- resolves a matching `IWorkerModuleFactory`
- creates a minimal execution context
- runs the worker module and returns a process exit code
- is provisioned as the `omp_core` `worker-host` artifact `omp_workerprocesshost`

### OpenModulePlatform.Worker.Abstractions

Implemented as the minimal shared contract layer for worker plugins.

### Public reference example

The repository now also includes a public reference module for the manager-driven runtime.
Both projects live under `examples/WorkerAppModule/`, not at the repository root - searching
for the project names alone will not find them:

- `examples/WorkerAppModule/WebApp/OpenModulePlatform.Web.ExampleWorkerAppModule.csproj`
  (manifest component `example-workerapp-web`, `web-app`)
- `examples/WorkerAppModule/WorkerApp/OpenModulePlatform.Worker.ExampleWorkerAppModule.csproj`
  (manifest component `example-workerapp-worker`, `worker`)

The worker component is also the repository's only live example of the host-contract floor:
it declares `minWorkerHostVersion` in `omp-components.json`, which Check 4b of
`scripts/omp/validate-component-versions.ps1:579` validates and
`OpenModulePlatform.WorkerProcessHost` enforces at load time.

This example shows how a neutral OMP module can expose a web administration surface while running its background processing through the worker manager and child host.

## OMP integration status

The current OMP-backed Windows implementation uses:

- `omp.AppInstances` as runtime identity and desired-state anchor
- `omp.AppWorkerDefinitions` as minimal plugin-runtime metadata
- `omp.AppInstanceRuntimeStates` as manager-owned observed runtime state
- `omp.Hosts.LastSeenUtc` and `omp.AppInstances.LastSeenUtc` for heartbeat compatibility
- HostAgent artifact provisioning for WorkerManager, WorkerProcessHost, and
  worker plugin packages

## Portal administration status

### Placement and implicit workers

The database catalog requires both the app instance and the worker instance to be
enabled, allowed, and in the running desired state. An explicit worker `HostId`
takes precedence over the app instance's `HostId`. Without either pin, a host role
must have exactly one active, enabled assigned host. If multiple hosts share the
role, WorkerManager rejects the unpinned worker and logs a placement error with the
app and worker IDs. Assign each worker to a host before enabling a second host for
the role. Already managed workers that become ambiguous are stopped on the next
reconciliation. This rule also covers the implicit default worker.

For compatibility, an app instance with no `omp.WorkerInstances` rows still gets
one implicit worker whose ID equals the app instance ID. Discovery logs a warning;
every runtime observation starts with `Implicit default worker (no WorkerInstances
row).` The app-level runtime summary retains the process/exit diagnostics, including
crash-loop failures, for the Portal. A count of WorkerInstances is therefore not a
count of running processes. Creating explicit worker rows replaces the fallback;
even disabled explicit rows suppress it.

The public Portal now includes:

- `App workers` for app-level worker-runtime metadata in `omp.AppWorkerDefinitions`
- `Worker runtime` for operational visibility into managed app instances and their latest observed runtime state

These pages are schema-aware and degrade to empty lists until the database has been updated with the current SQL install script.

## Provisioning worker app instances from a module

A module admin page can create worker app instances without writing SQL against
platform tables. `WorkerAppInstanceProvisioningService`
(`OpenModulePlatform.Web.Shared/Provisioning`) is registered by
`AddOmpWebDefaults` in every OMP web app.

```csharp
Task<WorkerAppInstanceProvisioningResult> ProvisionWorkerAppInstanceAsync(
    ClaimsPrincipal caller,
    WorkerAppInstanceProvisioningRequest request,
    CancellationToken ct);
```

One call creates one logical app instance (`HostId` and `TargetHostTemplateId`
NULL) and one `omp.WorkerInstances` row per requested host. That covers both
topologies:

- **Shared:** one app instance with several workers that many module objects
  use. The service creates it once; the module then points its objects at it.
- **Dedicated:** one app instance per module object, with one worker on a
  standalone server or one worker per host behind a load balancer. Any number of
  hosts is accepted.

| Value | Source |
|---|---|
| Module key, app key, app instance key, display name, hosts | Supplied by the caller |
| Configuration path for the worker's RPC settings, for example `MyModule:HostAgentRpc` | Supplied by the caller |
| Module instance | The template instance, else `ModuleInstanceId`, else the module's only enabled module instance |
| Installation name | The template instance, else the one name all existing instances of the app share (none: NULL; several: refused) |
| Artifact | The template's artifact, else the newest enabled artifact of the app whose package type fits and that the applied module definition accepts |
| Worker `ConfigurationJson` | The host's own HostAgent RPC settings, see below |
| IDs | New for every created row |

Each new worker gets `{ "<path>": { "Enabled": true, "PipeName": ..., "TimeoutSeconds": ... } }`
built from its own host:

1. The host's active host configuration is the `omp.HostConfigurationDocuments`
   row for its host key with `IsActive = 1` and the highest `ConfigurationVersion`.
   If it has `values.hostAgentRpc`, `enabled: false` refuses the call, and its
   `pipeName` and `timeoutSeconds` apply.
2. Otherwise, or when that object has no `pipeName`, the pipe is the
   `RpcPipeName` an active HostAgent registered for the host: an
   `omp.HostAgentRuntimeStates` row with `IsActive = 1` and a `LastSeenUtc`
   within its freshness window. The window is ten minutes, or three of the
   agent's own refresh intervals when that is longer: the row's `RefreshSeconds`
   is the `HostAgent:RefreshSeconds` the agent publishes, so a healthy agent with
   a slow cycle (for example 900 seconds, window 45 minutes) never looks stale. A
   row without `RefreshSeconds` (an agent older than that column) gets ten
   minutes. The timeout is `values.hostAgentRpc.timeoutSeconds` when set, else
   the request's `DefaultHostAgentRpcTimeoutSeconds` (30 by default).
3. Otherwise the whole call is refused with `HostAgentRpcUnavailable`, and nothing
   is created. The message names the host and, when an active HostAgent row
   exists, its service name and version and why it was not trusted (no pipe, or
   last seen outside its window).

An active host configuration that cannot be read (not valid JSON, not a JSON
object, or duplicate property names) refuses the call with
`HostConfigurationInvalid` naming the host and the parse problem. It is not
treated as "no configuration", because falling back to the registration would
hide a broken pin or an RPC opt-out.

The pipe is never derived from the host key: a HostAgent may serve a custom
`RpcPipeName`, and a runtime-state row also survives a disabled or uninstalled
agent. A HostAgent older than the `RpcPipeName` registration never registers a
pipe, so its host must have `values.hostAgentRpc.pipeName` set explicitly.

After a downgrade to a HostAgent older than the registration (0.3.333), the
`RpcPipeName` the newer agent wrote stays on its runtime-state row, and the older
agent keeps that row's `LastSeenUtc` fresh, so provisioning keeps trusting that
pipe. This is accepted: the pipe name comes from `HostAgent:RpcPipeName` (or the
host key), never from the agent version, so it is still the pipe the host serves.
The resolver logs the registering agent's version with every pipe it takes from a
registration, so such a case is visible. Pin `values.hostAgentRpc.pipeName` if the
downgraded agent is configured with a different pipe.

A template supplies the binding only. Its IDs and its workers' configuration are
never copied.

The call runs in one transaction under an application lock on the module and
app instance key. A refused or failed call leaves no rows. A repeated call with
the same key reuses the matching app instance and only adds workers for hosts
that have none. Existing app and worker rows, including their configuration and
artifact, are never updated. Concurrent duplicate calls run one after the other,
and the later calls report `AppInstanceCreated = false`.

A reused app instance without an artifact is refused with `NoCompatibleArtifact`:
WorkerManager never starts workers bound to no artifact.

The caller is authorized when it is authenticated and RBAC grants it
`OMP.Portal.Admin`, or when it holds `ModuleAdministrationPermission` and an
enabled app of the same module declares that permission in `omp.AppPermissions`.
`WebApp:AllowAnonymous` does not bypass this check. A permission held only through
an ambient role (Everyone, AuthenticatedUsers) is an installation policy decision
that the service does not override. Refusals raise `WorkerAppInstanceProvisioningException`, whose `Error` is
one of: `Unauthorized`, `InvalidRequest`, `AppNotFound`, `NotAWorkerApp`,
`ModuleInstanceUnresolved`, `TemplateMismatch`, `InstallationNameUnresolved`,
`NoCompatibleArtifact`, `IncompatibleArtifact`, `HostNotFound`, `HostDisabled`,
`HostOutsideInstallation` (the host belongs to another `omp.Instances` row),
`HostAgentRpcUnavailable`, `KeyCollision` (the key is used by a host-bound
instance or another app), `Busy` (the lock timed out behind another call), and
`LockFailed` (SQL Server refused the lock as a deadlock victim, a cancellation or
an error; retrying is safe).

The new rows are enabled, allowed and in the running desired state, so the
platform may start the workers once the transaction commits. Disabling or
removing instances stays in Portal administration.

## Still out of scope

The public repository still does not implement:

- a non-Windows worker manager
