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

## Instance template

A template that describes how an OMP instance is expected to look structurally.

## Host template

A template for a host role within an instance template.

## Template topology

A collective term for the tables that describe desired structure in a template:

- `InstanceTemplateHosts`
- `InstanceTemplateModuleInstances`
- `InstanceTemplateAppInstances`

## Host deployment assignment

A link between a concrete host and a host template.
This is an automation-related part of the model, not a required part of manual installation.

## Host deployment

A representation of a deployment attempt or deployment state for a host.

## OMP Portal

The shared web UI for navigation and administration in OMP.

## OMP HostAgent

A future optional automation component that can read desired topology and deployment state and execute or verify actions on hosts.
