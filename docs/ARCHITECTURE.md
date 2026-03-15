# Architecture

OpenModulePlatform is organised around four concerns:

## 1. Instance structure
- `omp.Instances`
- `omp.Modules`
- `omp.ModuleInstances`
- `omp.Apps`
- `omp.AppInstances`
- `omp.Artifacts`

## 2. Security
- `omp.Permissions`
- `omp.Roles`
- `omp.RolePermissions`
- `omp.RolePrincipals`

## 3. Deployment and topology
- `omp.InstanceTemplates`
- `omp.HostTemplates`
- `omp.InstanceTemplateHosts`
- `omp.InstanceTemplateModuleInstances`
- `omp.InstanceTemplateAppInstances`
- `omp.Hosts`
- `omp.HostDeploymentAssignments`
- `omp.HostDeployments`

## 4. Module-owned data
Each module owns its own schema for configuration and workload data.

## First-round direction
This repository revision moves runtime state away from app definitions and into `omp.AppInstances`.
That change makes it possible to:

- run multiple instances of the same app definition
- attach different artifacts and configuration to each instance
- place app instances on different hosts
- represent both web and service apps using the same runtime concept

The template model is present in the schema, but full materialisation and HostAgent orchestration are still future work.
