# Project status

## Summary

OMP is beyond proof-of-concept but is not yet a fully completed platform.
The current repository is best described as a public platform skeleton with working reference examples and a clear direction toward a robust instance model.

For release planning purposes, `0.1.0` is the first public beta baseline.

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

## Areas that are still incomplete

- final template materialization
- desired topology versus actual runtime reconciliation
- explicit origin linkage between template objects and materialized objects
- more complete operational use of the deployment tables
- a stronger core-level model for configuration concepts
- the future HostAgent implementation

## Model maturity

### Relatively stable now

- `Modules` as definitions
- `Apps` as definitions
- `Artifacts` as build outputs
- `ModuleInstances` as concrete module instances
- `AppInstances` as runtime-level app instances
- Portal-based manual administration and RBAC management

### Still under design

- the template chain from `InstanceTemplate` to real rows
- deployment and assignment flows
- the HostAgent contract
- richer lifecycle and health semantics for the generic worker runtime

## Recommended priorities

1. complete the topology and materialization data model
2. clarify desired state versus observed state
3. implement HostAgent only after those foundations are stable
4. continue improving Portal workflows, documentation, and operational guidance
