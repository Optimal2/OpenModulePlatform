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

This is the more complete reference example:

- a web app for administration and observability
- a service or worker component
- module-owned configuration
- job tables and job processing
- runtime coupling to `omp.AppInstances`

It shows how a module can contain both a web surface and a service process while still using the same platform model.

## Data model today

### Definitions

- `omp.Modules`
- `omp.Apps`
- `omp.Artifacts`

Definitions should not hold runtime-specific values such as route, install path, or host placement.

### Instances

- `omp.Instances`
- `omp.ModuleInstances`
- `omp.AppInstances`
- `omp.Hosts`

These tables represent the concrete environment. `ModuleInstances` and `AppInstances` are especially important because they make it possible to run multiple instances of the same definition.

### Runtime

`omp.Hosts` can hold an optional `BaseUrl` used by the Portal when it needs to generate a link for an `AppInstance` that uses a relative `RoutePath` on a different host than the Portal.

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

RBAC is built on four core tables:

- `omp.Permissions`
- `omp.Roles`
- `omp.RolePermissions`
- `omp.RolePrincipals`

The Portal and module UIs read effective permissions through `RbacService`, which maps users and Windows groups to permissions stored in the database.

### Templates and deployment

The following tables already exist:

- `omp.InstanceTemplates`
- `omp.HostTemplates`
- `omp.InstanceTemplateHosts`
- `omp.InstanceTemplateModuleInstances`
- `omp.InstanceTemplateAppInstances`
- `omp.HostDeploymentAssignments`
- `omp.HostDeployments`

These show the intended direction, but the full materialization model is not yet complete.

## Request flow in web applications

1. The application starts with shared hosting defaults from `OpenModulePlatform.Web.Shared`.
2. Windows-integrated authentication is used when anonymous access is not allowed.
3. `RbacService` reads the user's effective permissions from the database.
4. Razor Pages build views from permissions and repository data.
5. The Portal home page filters the app catalog based on permissions.

## Request flow in the service example

1. The worker starts with a configured `AppInstanceId`.
2. `AppInstanceRepository` reads runtime state from `omp.AppInstances`.
3. Heartbeat updates observed state on both `AppInstance` and, when possible, `Host`.
4. Configuration is loaded from a module-owned table using `ConfigId` as the bridge.
5. The job processor works only when the app instance is active, allowed, and verified against the expected runtime identity.

## Strengths of the current architecture

- explicit separation between definitions and instances
- `AppInstance` already works as a shared runtime model for both web and service scenarios
- the Portal genuinely uses the instance level in its app catalog
- RBAC is compact and understandable
- the SQL scripts are explicit and use deliberate placeholders instead of guessing environment-specific values
- the example projects demonstrate both a simple pattern and a more complete pattern

## Known limitations

- the template model exists in the schema but is not yet fully operationalized
- origin tracking between template rows and materialized runtime rows is not yet explicit
- `ConfigId` is functional but still semantically thin at the core-model level
- the Portal administrative workflows are better than before but still table-centric
- there is not yet a real reconcile or materialization engine between desired topology and actual runtime state

## Recommended direction

1. Keep `AppInstance` as the central runtime unit.
2. Complete the materialization model from templates to real `Hosts`, `ModuleInstances`, and `AppInstances`.
3. Make origin tracking explicit in the database.
4. Clarify desired state versus observed state further.
5. Build HostAgent only after those pieces are stable.
