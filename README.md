# OpenModulePlatform

OpenModulePlatform (OMP) is a general modular platform for defining, running,
and administering OMP instances, modules, app definitions, app instances, artifacts,
hosts, and topology.

The repository contains only neutral platform code and neutral example modules.
It does not contain domain-specific business components from previous internal projects.

## Contents

- `OpenModulePlatform.Portal` - Portal for navigation and manual administration
- `OpenModulePlatform.Web.Shared` - shared web infrastructure for the Portal and web modules
- `OpenModulePlatform.Web.ExampleWebAppModule` - simple web module used as a reference example
- `OpenModulePlatform.Web.ExampleServiceAppModule` - web interface for the service-backed example module
- `OpenModulePlatform.Service.ExampleServiceAppModule` - worker/service example
- `sql/SQL_Install_OpenModulePlatform.sql` - core schema, RBAC, Portal, and bootstrap data
- `sql/SQL_Install_OpenModulePlatform_Examples.sql` - example modules, example instances, template topology, and sample jobs
- `docs/` - architecture, terminology, and practical guides

## Current model

The current model explicitly separates:

- **definitions** - `Modules`, `Apps`, `Artifacts`
- **concrete instances** - `ModuleInstances`, `AppInstances`
- **manual operations** - `Instances`, `Hosts`, artifacts, app instances, and RBAC
- **future automation/topology** - `InstanceTemplates`, `HostTemplates`, template topology tables, and deployment tables

`omp.AppInstances` is the central runtime table in the current model. This is where OMP currently stores:

- host placement
- artifact selection
- config reference
- route/path/url
- desired state
- observed state / heartbeat / verification data

## What works today

- The Portal can be used for manual administration of the core model
- RBAC can be administered from the Portal
- The Portal builds the app catalog from `AppInstances`, not from `Apps`
- The example modules demonstrate both a pure web scenario and a service-backed scenario
- The service example reads runtime state from `AppInstances` and updates heartbeat/observed identity
- The SQL scripts bootstrap both core and example data

## What is still in progress

- template materialization is not yet fully implemented
- HostAgent does not yet exist
- the deployment tables are more preparatory than fully operationalized
- the config model is still module-owned and not yet fully formalized at the core level

## Quick start

### 1. Create the database

Create the `OpenModulePlatform` database in SQL Server.

### 2. Install core

Run:

```sql
sql/SQL_Install_OpenModulePlatform.sql
```

After running it, all `REPLACE_ME` values must be reviewed and replaced before the Portal or service apps can be used for real.

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

- **Core/manual administration** - Instances, Hosts, Modules, ModuleInstances, Apps, Artifacts, AppInstances, and RBAC
- **Advanced automation** - templates, deployment assignments, and deployments

The idea is that a normal manual installation should be possible without requiring the user to work with the future HostAgent/template model.

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Terminology](docs/TERMINOLOGY.md)
- [Manual admin configuration](docs/ADMIN_CONFIGURATION.md)
- [Current project status](docs/PROJECT_STATUS.md)

## License

The project is published under the MIT License.
