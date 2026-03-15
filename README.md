<!-- File: README.md -->
# OpenModulePlatform

OpenModulePlatform (OMP) is a neutral, modular platform foundation for building portal-driven solutions composed of modules, apps, artifacts and host-managed deployments.

This repository is the open source baseline for OMP. It contains:

- `OpenModulePlatform.Portal` – the OMP Portal web application
- `OpenModulePlatform.Web.Shared` – shared web hosting, RBAC and base page models
- `OpenModulePlatform.Web.ExampleWebAppModule` – example of a web-only module
- `OpenModulePlatform.Web.ExampleServiceAppModule` – example of a module with a web app and a service app
- `OpenModulePlatform.Service.ExampleServiceAppModule` – example worker / service app for the service module
- `sql/SQL_Install_OpenModulePlatform.sql` – idempotent SQL install script for the OMP core schema

## Repository goals

This repository intentionally excludes any domain-specific or customer-specific solutions. It is the clean OMP baseline intended to be reused, extended and forked.

## Terminology

See `docs/TERMINOLOGY.md`.

## Current sample structure

```text
OpenModulePlatform.Portal
OpenModulePlatform.Web.Shared
OpenModulePlatform.Web.ExampleWebAppModule
OpenModulePlatform.Web.ExampleServiceAppModule
OpenModulePlatform.Service.ExampleServiceAppModule
sql/SQL_Install_OpenModulePlatform.sql
```

## Notes

- The current web samples use Razor Pages.
- The service sample uses a .NET worker / Windows Service style host.
- The terminology and the SQL model are broader than the current technical implementation.
- The code in this artifact was refactored statically from the supplied source tree. No build verification was possible in this environment because the .NET SDK is not installed in the container.
