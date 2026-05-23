# HostAgent Installation Automation

This document describes the current desired-topology model that HostAgent
materializes into concrete runtime rows and deployed files.

Artifact versioning and stable identity rules are defined in
`docs/VERSIONING_AND_IDENTITIES.md`.

## Current Model

The current Portal workflow uses one default installation profile. The database
table is still named `omp.InstanceTemplates` and the schema can store several
profiles, but the product currently treats one OMP database/runtime set as one
OMP installation.

System > Installation is the admin-facing source of truth for:

- desired hosts
- desired module instances
- desired app rows
- desired artifact versions
- host placement, route, path, and runtime policy

`omp.HostTemplates` remains in the schema, but Portal presents those rows as
host roles. They are not a separate visible host-template workflow.

## Materialization Flow

1. SQL setup, module definition imports, or module packages register modules,
   apps, artifacts, and the installation profile rows.
2. A Portal admin changes desired hosts, module instances, or app versions from
   System > Installation.
3. HostAgent reads the desired topology for its configured host key.
4. HostAgent materializes enabled desired rows into concrete `omp.Hosts`,
   `omp.ModuleInstances`, and `omp.AppInstances`.
5. HostAgent provisions desired artifacts into the local artifact cache.
6. Package-type handlers install or update IIS applications, Windows services,
   worker hosts, and worker plugins.

## Implemented Baseline

HostAgent runs a reconciliation cycle that can:

- materialize installation topology for its configured host
- provision artifact content from the central artifact store
- deploy `web-app` artifacts to IIS runtime folders
- deploy `service-app` artifacts as Windows services
- deploy worker manager, worker process host, and worker plugin artifacts
- repair artifact-owned configuration files after deployment
- record outcomes in `omp.HostAppDeploymentStates`

Host-neutral web apps are used for apps that sit behind one load-balanced public
identity. Runtime apps and web apps without that load-balanced identity should
be host-specific so each concrete runtime gets its own `AppInstanceId`.

## Remaining Steps

1. Improve Portal drift/status views so operators can see desired versus
   observed artifact, IIS, service, and worker state in one place.
2. Add friendlier Portal controls for HostAgent self-upgrade rollout policy.
3. Make origin tracking between desired topology rows and materialized runtime
   rows explicit in the database.
4. Revisit multiple installation profiles only when there is a concrete need to
   run several independent OMP installations in one database/runtime set.

## Script Boundary

Once a host is bootstrapped, regular artifact version changes should flow
through OMP metadata and HostAgent instead of repeated package installer runs.
Installer packages still have a role for first install, database bootstrap,
HostAgent installation or repair, IIS site creation, service account
configuration, ACLs, and disaster recovery.

## Design Boundary

Installation topology describes desired metadata and placement. HostAgent is the
host-local executor. It must not contain customer-specific module rules; module
definitions and artifact packages must keep registering neutral data that
HostAgent can consume generically.
