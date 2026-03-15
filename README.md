<!-- File: README.md -->
# OpenModulePlatform

OpenModulePlatform (OMP) is a neutral, modular foundation for building portal-driven solutions composed of modules, apps, artifacts and host-managed deployments.

This repository contains the open source baseline for OMP. It includes the shared web infrastructure, the main portal, two example modules and the SQL scripts required to create a working development environment.

## Repository contents

- `OpenModulePlatform.Portal` - the main OMP portal web application.
- `OpenModulePlatform.Web.Shared` - shared hosting, RBAC and page-model infrastructure for OMP web applications.
- `OpenModulePlatform.Web.ExampleWebAppModule` - example of a web-only module.
- `OpenModulePlatform.Web.ExampleServiceAppModule` - example of a module with a web application and a service application.
- `OpenModulePlatform.Service.ExampleServiceAppModule` - example .NET worker / Windows Service application used by the service module sample.
- `sql/SQL_Install_OpenModulePlatform.sql` - idempotent core install script for the OMP schema and portal baseline.
- `sql/SQL_Install_OpenModulePlatform_Examples.sql` - idempotent sample-data script that adds the example modules and their supporting schema.

## Quick start

1. Create a database named `OpenModulePlatform`.
2. Run `sql/SQL_Install_OpenModulePlatform.sql`.
3. Review the bootstrap portal administrator row inserted into `omp.RolePrincipals` and adjust it if required for your environment.
4. Run `sql/SQL_Install_OpenModulePlatform_Examples.sql`.
5. Start `OpenModulePlatform.Portal` and sign in with the bootstrap administrator account.
6. Publish and install `OpenModulePlatform.Service.ExampleServiceAppModule` if you want to exercise the service module end-to-end.

## Notes

- The web samples use ASP.NET Core Razor Pages.
- The service sample uses a .NET worker host and can be installed as a Windows Service.
- The example modules are intentionally generic. They are included to demonstrate structure, registration, RBAC and host installation patterns without carrying domain-specific logic.

## Documentation

- `docs/TERMINOLOGY.md`
- `docs/ARCHITECTURE.md`

## License

This repository is licensed under the MIT License. See `LICENSE` for details.
