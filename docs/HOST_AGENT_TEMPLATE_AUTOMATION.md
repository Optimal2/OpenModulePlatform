# HostAgent Template Automation

This document tracks the path from the current metadata model to full
HostAgent-driven deployment automation.

## Target Flow

1. SQL setup and module installers register modules, apps, artifacts, instance
   templates, host templates, and template app instances.
2. An OMP instance is assigned an instance template.
3. Hosts are registered in the instance and assigned host templates.
4. HostAgent materializes the matching template topology into concrete
   `omp.ModuleInstances` and `omp.AppInstances` rows for its host.
5. HostAgent provisions the desired artifacts into the local artifact cache.
6. Later automation stages use the cached artifacts to install or update IIS
   applications, Windows services, and worker plugins.

## Implemented Baseline

HostAgent now has a `HostAgent:MaterializeTemplates` setting, enabled by
default. At the start of each cycle it calls
`omp.MaterializeInstanceTemplate` for its configured `HostKey`.

The stored procedure is intentionally conservative:

- It only materializes enabled instances, templates, modules, apps, hosts, and
  active host-template assignments.
- It upserts concrete module and app instances without deleting rows that no
  longer appear in a template.
- It updates only metadata fields owned by templates and leaves runtime
  observation fields intact.
- It maps template hosts to concrete hosts by matching `HostKey` inside the
  same OMP instance and by requiring an active `HostDeploymentAssignments` row
  for the host template.

This closes the first automation gap: HostAgent can now create the concrete
`AppInstances` that its existing artifact provisioning query already consumes.

## Remaining Steps

1. Add a deployment queue contract around `omp.HostDeployments` so Portal can
   request a host/template rollout and HostAgent can claim, run, and complete it
   with clear status and errors.
2. Add package-type handlers in HostAgent for web apps, service apps, and worker
   plugins. Artifact provisioning should remain the cache step; installation
   handlers should own IIS/service updates.
3. Add drift detection for IIS apps, Windows services, artifact versions, and
   runtime paths so operators can see whether a host matches the desired
   template.
4. Add rollback and rollout policy metadata: desired artifact version, canary or
   per-host sequencing, maintenance windows, and restart behavior.
5. Extend Portal admin pages with preview/apply actions for instance templates,
   host template assignments, and host deployments.

## Design Boundary

Templates describe desired metadata and placement. HostAgent should be the
host-local executor. It should not contain customer-specific module rules; module
installers must keep registering neutral artifacts and template rows that
HostAgent can consume generically.
