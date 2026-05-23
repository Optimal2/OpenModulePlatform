# Architecture

## Overview

OpenModulePlatform is built around a small but explicit core model:

1. **Definitions** describe what can exist.
2. **Instances** describe what actually exists in an OMP installation.
3. **Runtime** describes what is running, where it is running, and how it is observed.
4. **Automation and topology** describe future desired state and how a HostAgent could later materialize it.

The most important architectural choice in the current codebase is that `AppInstance` is treated as the runtime center instead of pushing runtime responsibility onto `App` or `Module` definitions.

## Solution components

### OpenModulePlatform.Portal

The Portal is both the landing surface and the administrative UI.

It performs three primary jobs:

- displays available web applications through an instance-based app catalog
- provides manual administration for the core data model
- provides RBAC administration

The Portal catalog is built from `omp.AppInstances`, not from `omp.Apps`. That matters because route, host placement, and artifact choice are instance-level concerns.

### OpenModulePlatform.Web.Shared

The shared web project contains:

- hosting defaults
- authentication and forwarded-header defaults
- RBAC resolution
- shared base classes for Razor Pages
- SQL connection plumbing for web applications

This keeps the Portal and the module web front ends aligned around the same baseline behaviour.

### ExampleWebAppModule

This is the simplest reference example:

- one module definition
- one web app definition
- module-owned configuration
- a web UI that displays and edits module data

It shows how a module can use OMP without any worker or service component.

### ExampleServiceAppModule

This is the classic service-backed reference example:

- a web app for administration and observability
- a dedicated service executable
- module-owned configuration
- job tables and job processing
- runtime coupling to `omp.AppInstances`

It shows how a module can contain both a web surface and a classic service process while still using the same platform model.

### ExampleWorkerAppModule

This is the manager-driven worker reference example:

- a web app for administration and observability
- a worker plugin loaded by `OpenModulePlatform.WorkerProcessHost`
- module-owned configuration
- job tables and job processing
- runtime coupling to `omp.AppInstances`, `omp.AppWorkerDefinitions`, and `omp.AppInstanceRuntimeStates`

It shows how a module can use the same platform model while moving background processing into the additive worker manager runtime.

## Data model today

### Definitions

- `omp.Modules`
- `omp.Apps`
- `omp.Artifacts`
- `omp.ModuleDefinitionDocuments`
- `omp.ModuleDefinitionArtifactCompatibility`

Definitions should not hold runtime-specific values such as route, install path, or host placement.
Module definition documents describe the schema and metadata contract that must
exist before compatible artifact versions are selected for deployment.

### Instances

- `omp.Instances`
- `omp.ModuleInstances`
- `omp.AppInstances`
- `omp.Hosts`

These tables represent the concrete environment. `ModuleInstances` and `AppInstances` are especially important because they make it possible to run multiple instances of the same definition.

### Runtime

`omp.Hosts` can hold an optional `BaseUrl` used by the Portal when it needs to generate a link for an `AppInstance` that uses a relative `RoutePath`. When `BaseUrl` is empty, Portal assumes the app is reachable through the same public base URL as Portal.

`omp.AppInstances` currently contains, among other things:

- `HostId`
- `ArtifactId`
- `ConfigId`
- `RoutePath`
- `PublicUrl`
- `InstallPath`
- `InstallationName`
- `DesiredState`
- verification policy (`ExpectedLogin`, `ExpectedClientHostName`, `ExpectedClientIp`)
- observed state (`LastSeenUtc`, `LastLogin`, `LastClientHostName`, `LastClientIp`, `VerificationStatus`)

This makes `AppInstance` the natural runtime unit for both web apps and service apps.

### Security

For the full authentication, user, role-principal, and RBAC model, see
[`AUTHENTICATION_AND_RBAC.md`](AUTHENTICATION_AND_RBAC.md).

Authentication is split from authorization:

- `OpenModulePlatform.Auth` owns sign-in and issues the shared OMP cookie.
- OMP web apps use anonymous IIS access and validate the shared cookie in application code.
- `/auth/ad` is the built-in AD/Windows provider endpoint. It uses IIS Windows Authentication only inside the auth app and converts the Windows principal into OMP role principals.
- `/auth/login` also supports the built-in `lpwd` provider backed by `omp.auth_provider_lpwd`.

The auth tables are:

- `omp.users`
- `omp.auth_providers`
- `omp.user_auth`
- `omp.auth_provider_lpwd`

An OMP user row is required for local password sign-in and for provider identities that should be attached to a first-class OMP user. It is not required for large AD groups.

RBAC is built on four core tables:

- `omp.Permissions`
- `omp.Roles`
- `omp.RolePermissions`
- `omp.RolePrincipals`

The Portal and module UIs read effective permissions through `RbacService`. `RolePrincipals` can target first-class OMP users with `PrincipalType = 'OmpUser'`, direct AD users with `PrincipalType = 'ADUser'`, large AD groups with `PrincipalType = 'ADGroup'`, or other provider-specific principal types such as `LocalUser`. This lets an administrator grant access to an AD group with many users without creating one `omp.users` row per member.

### Installation topology and deployment

The current automation model uses one default installation profile as the
admin-facing desired state for the OMP installation. The database still uses the
historical table names and can store more than one profile, but the Portal
workflow is intentionally scoped to one active installation profile for now:

- `omp.InstanceTemplates`
- `omp.HostTemplates`
- `omp.InstanceTemplateHosts`
- `omp.InstanceTemplateModuleInstances`
- `omp.InstanceTemplateAppInstances`
- `omp.HostDeploymentAssignments`
- `omp.HostDeployments`

`InstanceTemplates` is the installation profile. `HostTemplates` is treated as a
host-role lookup inside that profile, not as a separate visible template layer.
The rows in `InstanceTemplateHosts`, `InstanceTemplateModuleInstances`, and
`InstanceTemplateAppInstances` define desired hosts, module instances, desired
artifact versions, placement, routes, paths, and runtime policy. HostAgent reads
those rows and materializes concrete `Hosts`, `ModuleInstances`, and
`AppInstances`, then provisions matching artifacts on each host.

## Request flow in web applications

1. The application starts with shared hosting defaults from `OpenModulePlatform.Web.Shared`.
2. The shared OMP cookie authenticates the request. Unauthenticated users are redirected to `/auth/login`.
3. `RbacService` expands OMP auth claims into role-principal keys and reads effective permissions from the database.
4. Razor Pages build views from permissions and repository data.
5. The Portal home page filters the app catalog based on permissions.

## Request flow in the classic service example

1. The worker starts with a configured `AppInstanceId`.
2. `AppInstanceRepository` reads runtime state from `omp.AppInstances`.
3. Heartbeat updates observed state on both `AppInstance` and, when possible, `Host`.
4. Configuration is loaded from a module-owned table using `ConfigId` as the bridge.
5. The job processor works only when the app instance is active, allowed, and verified against the expected runtime identity.

## Request flow in the manager-driven worker example

1. `OpenModulePlatform.WorkerManager.WindowsService` discovers eligible app instances for the current host.
2. The manager starts one `OpenModulePlatform.WorkerProcessHost` child process per eligible `AppInstanceId`.
3. The child host loads `OpenModulePlatform.Worker.ExampleWorkerAppModule.dll` and resolves `ExampleWorkerAppModuleFactory` through `IWorkerModuleFactory`.
4. The plugin reads runtime state from `omp.AppInstances` and module configuration from its own schema.
5. The manager publishes observed runtime state back to OMP while the worker plugin processes jobs.

## Strengths of the current architecture

- explicit separation between definitions and instances
- `AppInstance` already works as a shared runtime model for both web and service scenarios
- the Portal genuinely uses the instance level in its app catalog
- RBAC is compact and understandable
- the SQL scripts are explicit and require operator-provided bootstrap values instead of guessing environment-specific values
- the example projects demonstrate web-only, classic service-backed, and manager-driven worker patterns

## Known limitations

- the schema can represent several installation profiles, but Portal currently
  exposes one default installation profile
- origin tracking between template rows and materialized runtime rows is not yet explicit
- `ConfigId` is functional but still semantically thin at the core-model level
- the Portal administrative workflows are better than before but still table-centric
- deeper drift reporting between desired topology and actual runtime state is
  still evolving

## Recommended direction

1. Keep `AppInstance` as the central runtime unit.
2. Keep System > Installation as the main desired-topology surface for normal
   administration.
3. Make origin tracking explicit in the database.
4. Clarify desired state versus observed state further.
5. Add multi-profile support only when there is a concrete need for several OMP
   installations in one database/runtime set.
