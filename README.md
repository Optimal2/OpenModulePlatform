# OpenModulePlatform

OpenModulePlatform (OMP) is a modular platform for managing instances, modules, app definitions, app instances, hosts and deployment topology.

This repository contains:

- the shared OMP Portal
- a shared web library
- a web-only example module
- a service-backed example module
- SQL install scripts for the OMP core schema and the example modules

## Current model

The current repository version separates:

- **definitions**: modules, apps, artifacts
- **instance topology**: module instances and app instances
- **operations**: instance templates, host templates, hosts, deployment assignments and deployments

`omp.AppInstances` is the key runtime table in the current model. It represents the deployable and observable instance of an app within an OMP instance.

## SQL setup

1. Run `sql/SQL_Install_OpenModulePlatform.sql`
2. Review and replace every `REPLACE_ME` placeholder
3. Run `sql/SQL_Install_OpenModulePlatform_Examples.sql` if you want the sample modules

The base install script intentionally inserts placeholder security and runtime values. This makes the initial setup explicit and avoids silent assumptions about users, hosts or service accounts.
