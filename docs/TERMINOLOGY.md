# Terminology

## OpenModulePlatform (OMP)

The umbrella term for the entire platform.

## OMP instance

A concrete installation of OMP for a specific environment, organization, or deployment surface.

## Module

A module definition. It describes a reusable module that can be installed multiple times.

## Module instance

A concrete module instance inside a specific OMP instance.

## App

An app definition that belongs to a module definition. An app definition can represent a web app, Portal app, worker app, or service app.

## App instance

The concrete runtime instance of an app inside a specific module instance.
An app instance can have its own artifact, configuration, host placement, route, path, public URL, and runtime policy.

## Artifact

A deployable build product for an app definition, such as a published folder, zip archive, or other packaged output.

## Host

A concrete runtime target inside an OMP instance. A host can carry zero or more app instances.

## Installation profile

The desired topology for an OMP installation.

The database table is still named `omp.InstanceTemplates`, and the schema can
store more than one profile. The current Portal workflow intentionally exposes
one default installation profile and treats one OMP database/runtime set as one
OMP installation.

## Host role

A reusable classification for a host inside the installation profile, such as a
single local host or a web node. The database table is still named
`omp.HostTemplates`, but Portal presents it as a host role rather than a second
visible template layer.

Desired app rows can target a host role. HostAgent and WorkerManager then apply
that desired app only on concrete hosts that have the matching active host role
assignment.

## Installation topology

A collective term for the tables that describe the desired structure in the
installation profile:

- `InstanceTemplateHosts`
- `InstanceTemplateModuleInstances`
- `InstanceTemplateAppInstances`

## Host deployment assignment

A compatibility link between a concrete host and a host role. It is retained for
automation history and future multi-profile scenarios, but the normal
administration flow is System > Installation.

## Host deployment

A representation of a deployment attempt or deployment state for a host.

## OMP Portal

The shared web UI for navigation and administration in OMP.

## OMP HostAgent

The host-local automation component that reads installation topology, provisions
artifacts, writes runtime configuration, and creates or updates IIS apps,
Windows services, and worker runtimes.

## Desired state

The database-backed target state that tells HostAgent what should exist on a
host. Desired state covers selected artifacts, host placement, runtime paths,
and whether apps, services, or workers should run.

## Artifact store

The central storage root for registered artifact payloads. HostAgent reads from
the artifact store and copies deployable content into a host-local artifact
cache before deploying it.

## Artifact cache

The host-local extracted copy of artifact content. Deployments use this cache as
their source so the central artifact store can remain shared and immutable.

## Package library

The installer-local library of portable objects that can be imported during a
bootstrap, upgrade, or package refresh. In the universal installer layout this
normally lives under `installer/data/global/`.

## Available library

The runtime library of available portable objects that have been staged for
Portal or HostAgent import but are not necessarily selected as desired state.

## Import folder

A watched folder where universal packages, artifact packages, module
definitions, widgets, host configs, or config overlays can be dropped for
HostAgent to import.

## Universal package

A zip container that can carry any mix of portable OMP objects, such as module
definitions, artifacts, host configs, config overlays, and widgets. See
`UNIVERSAL_MODULE_PACKAGES.md`.

## Config overlay

Host-specific configuration that is stored separately from the artifact binary
payload. This keeps environment-specific config out of artifact hashes and lets
the same artifact content be used by multiple hosts.

## Artifact configuration file

A runtime configuration file associated with an artifact and written by
HostAgent during deployment. These files live outside the binary artifact hash
and are stored in OMP metadata.

## Registered module definition

A module definition that exists in the OMP database or available library. A
registered definition may still need to be applied before it affects module,
app, SQL, or desired-state metadata.

## Applied module definition

A module definition whose SQL and metadata have been applied to the OMP
database. Applying a definition is idempotent and may run repair scripts.

## HostAgent-first install

The installation model where the bootstrapper creates the minimum database
state, installs HostAgent, imports portable objects, and then lets HostAgent
materialize the rest of the runtime.

## HostAgent self-upgrade

The process where a newer HostAgent artifact is installed beside the current
service, started, and allowed to take over before the old service is quiesced
and removed.

## Host lease

A database lease used by HostAgent instances to coordinate which running
HostAgent owns host-local automation at a given moment.

## Takeover mode

A temporary HostAgent runtime mode used during self-upgrade when a newer
HostAgent starts, proves it can run, and takes responsibility for cleaning up
the older HostAgent service.

## Bootstrap configuration

The host-specific configuration used by the bootstrapper to connect to SQL,
locate package data, configure paths, install HostAgent, and seed initial OMP
state.

## Sync package objects

The installer action that refreshes package-library module definitions,
artifacts, SQL, and related portable objects from configured source roots before
an install or upgrade.

## Minimal runner

The committed installer shape that keeps only the bootstrapper executable and
source-of-truth host configuration in version control. Generated package data is
rebuilt locally or staged separately.
