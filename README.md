# OpenModulePlatform

OpenModulePlatform (OMP) is a modular platform for defining, running, observing,
and administering OMP instances, modules, app definitions, app instances,
artifacts, hosts, and topology.

The public repository contains neutral platform code and neutral example modules.
It does not contain customer-specific integrations, credentials, or domain-specific
business components from previous internal projects.

## Release status

The repository is being prepared for the first public beta release: **0.1.0**.
Version `0.1.0` should be treated as a stable public baseline for evaluation,
documentation, and iterative hardening, not as a feature-complete production platform.

## Repository contents

- `OpenModulePlatform.Portal` - Portal for navigation and manual administration
- `OpenModulePlatform.Web.Shared` - shared web infrastructure for the Portal and web modules
- `OpenModulePlatform.Web.ExampleWebAppModule` - simple web module used as a reference example
- `OpenModulePlatform.Web.ExampleWebAppBlazorModule` - Blazor-based web module reference example
- `OpenModulePlatform.Web.ExampleServiceAppModule` - web interface for the service-backed example module
- `OpenModulePlatform.Service.ExampleServiceAppModule` - worker/service reference example
- `OpenModulePlatform.WorkerManager.WindowsService` - scaffold for a future generic Windows worker manager
- `OpenModulePlatform.WorkerProcessHost` - scaffold for a future generic child worker process host
- `OpenModulePlatform.Worker.Abstractions` - contracts for the future worker runtime model
- `sql/SQL_Install_OpenModulePlatform.sql` - core schema, RBAC, Portal, and bootstrap data
- `sql/SQL_Install_OpenModulePlatform_Examples.sql` - example modules, example instances, template topology, and sample jobs
- `docs/` - architecture, terminology, release notes, and practical guides

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
- The example modules demonstrate both a pure web scenario and a service-backed scenario
- The service example reads runtime state from `AppInstances` and updates heartbeat and observed identity
- The SQL scripts bootstrap both core and example data
- The worker runtime scaffold is in place for the next phase of runtime work

## What is still in progress

- template materialization is not yet fully implemented
- HostAgent does not yet exist
- the deployment tables are more preparatory than fully operationalized
- the configuration model is still module-owned and not yet fully formalized at the core level
- the worker runtime scaffold does not yet implement dynamic plugin loading or process supervision

## Quick start

### 1. Create the database

Create the `OpenModulePlatform` database in SQL Server.

### 2. Install core

Run:

```sql
sql/SQL_Install_OpenModulePlatform.sql
```

After running it, review and replace all `REPLACE_ME` values before the Portal or any service app is used outside a local example environment.

### 3. Install examples

Run:

```sql
sql/SQL_Install_OpenModulePlatform_Examples.sql
```

This adds neutral example modules, module instances, app instances, template topology, and sample jobs.

### 4. Configure the Portal

Set `ConnectionStrings:OmpDb` for the Portal and start `OpenModulePlatform.Portal`.

The Portal is the primary path for manual administration. Normal operation should not require direct SQL editing after the initial bootstrap.

## Manual administration versus automation

In its current state, the Portal is divided into two tracks:

- **Core and manual administration** - Instances, Hosts, Modules, ModuleInstances, Apps, Artifacts, AppInstances, and RBAC
- **Advanced automation** - templates, deployment assignments, and deployments

A normal manual installation should be possible without requiring the future HostAgent or template materialization model.

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Terminology](docs/TERMINOLOGY.md)
- [Manual administration](docs/ADMIN_CONFIGURATION.md)
- [Project status](docs/PROJECT_STATUS.md)
- [Worker runtime scaffold](docs/WORKER_RUNTIME.md)
- [Logging](docs/LOGGING.md)
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
