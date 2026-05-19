# OpenModulePlatform

OpenModulePlatform (OMP) is a modular platform for defining, running, observing,
and administering OMP instances, modules, app definitions, app instances,
artifacts, hosts, and topology.

The public repository contains neutral platform code, first-party OMP modules,
and neutral example modules. It does not contain customer-specific integrations,
credentials, or domain-specific business components from previous internal
projects.

## Runtime baseline

The repository is now aligned on **.NET 10** for active development, package versioning, and publish profiles.

For Windows/IIS hosting prerequisites and official Microsoft links, see [Hosting OMP on Windows and IIS](docs/HOSTING_WINDOWS_IIS.md).

## Release status

The repository is being prepared for the first public beta release: **0.1.0**.
Version `0.1.0` should be treated as a stable public baseline for evaluation,
documentation, and iterative hardening, not as a feature-complete production platform.

## Repository contents

- `OpenModulePlatform.Portal` - Portal for navigation and manual administration
- `OpenModulePlatform.Auth` - shared OMP authentication app for AD and local password sign-in
- `OpenModulePlatform.Web.Shared` - shared web infrastructure for the Portal and web modules
- `OpenModulePlatform.Web.ContentWebAppModule` - first-party content module for simple OMP-managed information pages
- `OpenModulePlatform.Web.iFrameWebAppModule` - first-party iframe module for exposing external or separately hosted web apps inside OMP
- `examples/WebAppModule` - simple web module; app code lives in `WebApp`, SQL lives in `sql`
- `examples/WebAppBlazorModule` - Blazor-based web module; app code lives in `WebApp`, SQL lives in `sql`
- `examples/ServiceAppModule/WebApp` - web interface for the service-backed example module
- `examples/ServiceAppModule/ServiceApp` - worker/service reference example
- `OpenModulePlatform.WorkerManager.WindowsService` - generic Windows worker manager for manager-driven worker apps
- `OpenModulePlatform.WorkerProcessHost` - generic child worker process host for plugin-based workers
- `OpenModulePlatform.Worker.Abstractions` - shared contracts for worker plugins
- `examples/WorkerAppModule/WebApp` - web interface for the manager-driven worker example module
- `examples/WorkerAppModule/WorkerApp` - plugin-based worker reference example
- `sql/1-setup-openmoduleplatform.sql` and `sql/2-initialize-openmoduleplatform.sql` - neutral core schema, RBAC, default instance, host, and bootstrap data
- `OpenModulePlatform.Portal/sql/1-setup-omp-portal.sql` and `OpenModulePlatform.Portal/sql/2-initialize-omp-portal.sql` - Portal-owned schema and Portal registration data
- `OpenModulePlatform.Web.ContentWebAppModule/Sql/1-setup-content-webapp.sql` and `OpenModulePlatform.Web.ContentWebAppModule/Sql/2-initialize-content-webapp.sql` - content module schema and registration data, without default content pages
- `OpenModulePlatform.Web.iFrameWebAppModule/Sql/1-setup-iframe-webapp.sql` and `OpenModulePlatform.Web.iFrameWebAppModule/Sql/2-initialize-iframe-webapp.sql` - iframe module schema and registration data
- `examples/**/sql/1-setup-*.sql` and `examples/**/sql/2-initialize-*.sql` - optional example-module setup and initialization scripts
- `docs/` - architecture, terminology, release notes, and practical guides
- `docs/CODEX_DEVELOPMENT.md` - compact development guide for Codex/VS Code workflows

## Current architecture model

The current model explicitly separates:

- **definitions** - `Modules`, `Apps`, `Artifacts`
- **concrete instances** - `ModuleInstances`, `AppInstances`
- **manual operations** - `Instances`, `Hosts`, artifacts, app instances, and RBAC
- **future automation/topology** - `InstanceTemplates`, `HostTemplates`, template topology tables, and deployment tables

`omp.AppInstances` is the central runtime table in the current model. It currently stores:

- host placement
- artifact selection
- configuration reference
- route, path, or public URL data
- desired state
- observed state, heartbeat data, and verification data

## What works today

- The Portal can be used for manual administration of the core model
- RBAC can be administered from the Portal
- The Portal builds the app catalog from `AppInstances`, not from `Apps`
- The first-party content and iframe modules are usable OMP modules, not templates
- The example modules demonstrate pure web, classic service-backed, and manager-driven worker scenarios
- The service example reads runtime state from `AppInstances` and updates heartbeat and observed identity
- The SQL scripts use a two-step setup/initialization layout per module
- The additive Windows worker runtime is implemented with manager, child host, OMP discovery, and observed runtime reporting
- The public examples now cover web-only, classic service-backed, and manager-driven worker patterns

## What is still in progress

- template materialization is not yet fully implemented
- HostAgent and worker runtime hardening is still ongoing
- the deployment tables are more preparatory than fully operationalized
- the configuration model is still module-owned and not yet fully formalized at the core level
- artifact distribution and installation are still outside the current worker manager scope

## Quick start

### 1. Create the database

Create the `OpenModulePlatform` database in SQL Server.

### 2. Install core

Run the root OMP core scripts in order:

```sql
sql/1-setup-openmoduleplatform.sql
sql/2-initialize-openmoduleplatform.sql
```

Set `@BootstrapPortalAdminPrincipal` in `2-initialize-openmoduleplatform.sql` to the local Windows user or group that should receive the initial Portal administrator role. The local installer patches this safely and can also seed multiple administrator principals by passing multiple values to `-BootstrapPortalAdminPrincipal`, for example both a Windows account name and its display-name form.

### 3. Install the Portal module

Run the Portal-owned setup and initialization scripts in order:

```sql
OpenModulePlatform.Portal/sql/1-setup-omp-portal.sql
OpenModulePlatform.Portal/sql/2-initialize-omp-portal.sql
```

Set the same `@BootstrapPortalAdminPrincipal` value in `2-initialize-omp-portal.sql`, or use `scripts/manage-local-install.ps1` so the local installer handles it.

### 4. Install first-party OMP modules and optional examples

First-party modules in the repository root are usable OMP functionality. Install
the modules you want by running their module-owned SQL scripts in order:

```text
OpenModulePlatform.Web.ContentWebAppModule/Sql/1-setup-content-webapp.sql
OpenModulePlatform.Web.ContentWebAppModule/Sql/2-initialize-content-webapp.sql
OpenModulePlatform.Web.iFrameWebAppModule/Sql/1-setup-iframe-webapp.sql
OpenModulePlatform.Web.iFrameWebAppModule/Sql/2-initialize-iframe-webapp.sql
```

The local installer `scripts/manage-local-install.ps1` installs the Portal plus
the first-party Content and iFrame modules, including their template metadata
and web artifacts. Content starts without sample pages; run
`scripts/dev/seed-content-webapp-test-pages.ps1` only when you want explicit
local Content smoke-test pages. Use `scripts/install-local-examples.ps1` only
when you also want the optional example modules.

Each example module owns its own SQL folder and follows the same two-file pattern:

```text
examples/<module>/sql/1-setup-*.sql
examples/<module>/sql/2-initialize-*.sql
```

Run only the example modules you explicitly want in the local environment.

### 5. Configure the Portal

Set `ConnectionStrings:OmpDb` for the Portal and start `OpenModulePlatform.Portal`.

The Portal is the primary path for manual administration. Normal operation should not require direct SQL editing after the initial bootstrap.

## Manual administration versus automation

In its current state, the Portal is divided into two tracks:

- **Core and manual administration** - Instances, Hosts, Modules, ModuleInstances, Apps, Artifacts, AppInstances, and RBAC
- **Advanced automation** - templates, deployment assignments, and deployments

A normal manual installation should be possible without requiring the future HostAgent or template materialization model.

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Authentication and RBAC](docs/AUTHENTICATION_AND_RBAC.md)
- [Terminology](docs/TERMINOLOGY.md)
- [Manual administration](docs/ADMIN_CONFIGURATION.md)
- [Project status](docs/PROJECT_STATUS.md)
- [Worker runtime](docs/WORKER_RUNTIME.md)
- [Logging](docs/LOGGING.md)
- [Hosting OMP on Windows and IIS](docs/HOSTING_WINDOWS_IIS.md)
- [Release notes](CHANGELOG.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)

## Public repository hygiene

The repository includes:

- a GitHub Actions build workflow
- Dependabot configuration for NuGet packages and GitHub Actions
- repository hygiene checks for common local IDE files and generated artifacts
- central version metadata for the `0.1.0` release line

## License

The project is published under the MIT License.

## HostAgent v1

This package includes a first HostAgent implementation:

- `OpenModulePlatform.HostAgent.WindowsService`
- `OpenModulePlatform.HostAgent.Runtime`

HostAgent v1 provisions immutable artifact versions from a central artifact root to a local host cache and writes status to `omp.HostArtifactStates`. See `docs/HOST_AGENT.md`.
