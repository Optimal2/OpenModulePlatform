<!-- File: docs/TERMINOLOGY.md -->
# OpenModulePlatform terminology

## Paraplybegrepp

**OpenModulePlatform (OMP)** is the umbrella name for the platform, the architecture and the codebase.

## Högsta nivå i båda modellerna

**OMP Instance** is the highest concrete level in both the structural and the operational model.

## Strukturell modell

- **OMP Instance**
- **OMP Module**
- **OMP App**
- **OMP Artifact**

## Operativ modell

- **OMP Instance**
- **OMP InstanceTemplate**
- **OMP Host**
- **OMP HostTemplate**
- **OMP HostDeploymentAssignment**
- **OMP HostDeployment**
- **OMP HostInstallation**

## Särskilda begrepp

- **OMP Portal** = the portal concept for the main web UI
- **OMP HostAgent** = an optional tool that can run on an OMP Host and automate deployment, installation, update, monitoring and reporting

## Practical interpretation

- An **OMP Instance** is a concrete installation of OMP.
- An **OMP Module** is a functional extension within an instance.
- An **OMP App** is a deployable application within a module.
- An **OMP Artifact** is a versioned build product of an app.
- An **OMP Host** is a runtime and deployment environment within an instance.
- An **OMP HostTemplate** describes the desired state of a host.
- An **OMP HostDeployment** is the execution of a desired state on a host.
- An **OMP HostInstallation** is the actual installed state of an app on a host.
