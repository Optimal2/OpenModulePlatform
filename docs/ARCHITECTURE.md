<!-- File: docs/ARCHITECTURE.md -->
# OpenModulePlatform architecture

## Purpose

OpenModulePlatform is designed as a modular platform rather than a single-purpose application. The repository focuses on the platform baseline: portal infrastructure, shared services, RBAC, host-management concepts and representative example modules.

## Structural model

The structural model describes what an OMP instance consists of.

```text
OMP Instance
└─ OMP Module
   └─ OMP App
      └─ OMP Artifact
```

### Interpretation

- **OMP Instance** - a concrete installation of the platform.
- **OMP Module** - a functional extension within an instance.
- **OMP App** - an application that belongs to a module.
- **OMP Artifact** - a deployable build output for an app.

## Operational model

The operational model describes how an OMP instance is shaped, deployed and observed.

```text
OMP Instance
├─ OMP InstanceTemplate
└─ OMP Host
   ├─ OMP HostTemplate
   ├─ OMP HostDeploymentAssignment
   ├─ OMP HostDeployment
   └─ OMP HostInstallation
```

### Interpretation

- **OMP InstanceTemplate** - describes the intended topology of an instance.
- **OMP Host** - an execution and deployment environment within an instance.
- **OMP HostTemplate** - describes the desired state of a host.
- **OMP HostDeploymentAssignment** - links a host to the template it should follow.
- **OMP HostDeployment** - a concrete deployment execution on a host.
- **OMP HostInstallation** - the observed installation state of an app on a host.

## Current implementation in this repository

### Portal and web applications

- ASP.NET Core Razor Pages.
- Shared hosting conventions in `OpenModulePlatform.Web.Shared`.
- SQL-backed RBAC via `omp.*` tables.
- Central portal navigation driven by `omp.Modules`, `omp.Apps` and `omp.AppPermissions`.

### Service applications

- .NET worker-service hosting model.
- Heartbeat and verification against `omp.HostInstallations`.
- Module-local configuration stored in the module schema.
- Queue-based job processing pattern for background execution.

### SQL model

The `sql` folder contains:

- a core install script for the platform schema and portal baseline.
- an example install script that sets up the sample modules and their data.

## What is intentionally not included

This repository does not include customer-specific modules, domain-specific pipelines or private deployment conventions. The intent is to keep the baseline reusable for open source adoption and downstream extension.
