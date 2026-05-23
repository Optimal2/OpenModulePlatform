# Project status

## Summary

OMP is beyond proof-of-concept but is not yet a fully completed platform.
The current repository is best described as a public platform skeleton with working reference examples and a clear direction toward a robust instance model.

For release planning purposes, `0.1.0` is the first public beta baseline.

## Runtime and tooling baseline

The repository is now aligned on `.NET 10`, a pinned .NET 10 SDK line in `global.json`, and centralized NuGet package versioning through `Directory.Packages.props`.

## Strong areas today

- neutral open-source direction without customer-specific content
- consistent separation between definitions and instances across the core model
- a Portal for manual administration
- RBAC administration in the Portal
- neutral reference modules for web-only, classic service-backed, and manager-driven worker patterns
- a service example that uses `AppInstance` as the runtime center
- a manager-driven worker example built on the same `AppInstance` model
- SQL scripts for both core and example setup
- an implemented Windows worker runtime track with manager, child host, plugin contracts, and runtime observation
- a HostAgent-first installation topology where one default installation
  profile is materialized into runtime hosts, module instances, and app
  instances

## Areas that are still incomplete

- deeper desired topology versus actual runtime reconciliation views
- explicit origin linkage between installation topology rows and materialized objects
- more complete operational use of the deployment history tables
- a stronger core-level model for configuration concepts
- multi-profile administration when one database must contain several OMP
  installations

## Model maturity

### Relatively stable now

- `Modules` as definitions
- `Apps` as definitions
- `Artifacts` as build outputs
- `ModuleInstances` as concrete module instances
- `AppInstances` as runtime-level app instances
- Portal-based manual administration and RBAC management
- one default installation profile as the desired topology surface
- HostAgent as the host-local artifact and runtime deployment executor

### Still under design

- explicit origin tracking between installation topology and runtime rows
- multi-profile deployment and assignment flows
- richer lifecycle and health semantics for the generic worker runtime

## Recommended priorities

1. keep System > Installation as the primary desired-topology workflow
2. clarify desired state versus observed state
3. improve HostAgent drift, rollout, rollback, and status views
4. continue improving Portal workflows, documentation, and operational guidance
