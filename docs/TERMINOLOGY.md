# Terminology

## OMP Instance
A concrete installation of OpenModulePlatform for one environment or organisation.

## Module
A reusable module definition.

## Module Instance
A concrete module instance inside an OMP instance.

## App
A reusable app definition that belongs to a module definition.

## App Instance
A concrete runtime or web instance of an app inside a module instance. An app instance may have its own route, artifact, configuration, host placement and verification rules.

## Artifact
A deployable build output for an app definition.

## Host
A runtime target within an OMP instance. Hosts can carry one or more app instances.

## Instance Template
A template for the topology of an OMP instance.

## Host Template
A template for the desired shape of a host role within an OMP instance.

## OMP Portal
The shared portal web application for navigating and administering an OMP instance.

## OMP HostAgent
A future optional automation component that can reconcile host state against the desired topology and deployment model.
