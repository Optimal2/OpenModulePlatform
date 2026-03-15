<!-- File: docs/TERMINOLOGY.md -->
# OpenModulePlatform terminology

## Umbrella term

**OpenModulePlatform (OMP)** is the umbrella name for the platform, its architecture and its codebase.

## Highest concrete level

**OMP Instance** is the highest concrete level in both the structural and operational models.

## Structural model

- **OMP Instance**
- **OMP Module**
- **OMP App**
- **OMP Artifact**

## Operational model

- **OMP Instance**
- **OMP InstanceTemplate**
- **OMP Host**
- **OMP HostTemplate**
- **OMP HostDeploymentAssignment**
- **OMP HostDeployment**
- **OMP HostInstallation**

## Special terms

- **OMP Portal** - the primary portal concept for the main web user interface.
- **OMP HostAgent** - an optional tool that can run on an OMP host and automate deployment, installation, update, monitoring and reporting.

## Practical interpretation

- An **OMP Instance** is a concrete installation of OMP.
- An **OMP Module** is a functional extension within an instance.
- An **OMP App** is an application that belongs to a module.
- An **OMP Artifact** is a versioned build output for an app.
- An **OMP Host** is an execution and deployment environment within an instance.
- An **OMP HostTemplate** describes the desired state of a host.
- An **OMP HostDeployment** is the application of a desired state on a host.
- An **OMP HostInstallation** is the actual installation state observed on a host.
