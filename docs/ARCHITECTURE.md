<!-- File: docs/ARCHITECTURE.md -->
# OpenModulePlatform architecture

## Current baseline in this repository

This repository provides a neutral OMP baseline built around:

- a shared web library
- the OMP Portal
- a web-only example module
- a web + service example module
- a core SQL schema using the OMP terminology

## Structural model implemented in SQL

```text
OMP Instance
└─ OMP Module
   └─ OMP App
      └─ OMP Artifact
```

## Operational model implemented in SQL

```text
OMP Instance
├─ OMP InstanceTemplate
└─ OMP Host
   ├─ OMP HostTemplate
   ├─ OMP HostDeploymentAssignment
   ├─ OMP HostDeployment
   └─ OMP HostInstallation
```

## Current technical implementation

### Portal and web apps

- ASP.NET Core Razor Pages
- Windows authentication / Negotiate support
- SQL-backed RBAC
- shared page model and hosting conventions in `OpenModulePlatform.Web.Shared`

### Service apps

- .NET worker service style process
- SQL heartbeat against `omp.HostInstallations`
- configuration snapshot loading from module-local schema
- SQL queue claim pattern for jobs

## Intentionally excluded from this repository

- customer-specific modules
- archive-specific concepts
- domain-specific pipelines
- private environment details
