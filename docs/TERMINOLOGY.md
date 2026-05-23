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
