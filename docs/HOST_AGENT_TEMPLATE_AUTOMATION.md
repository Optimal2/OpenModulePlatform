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
6. Package-type handlers use the cached artifacts to install or update IIS
   applications, Windows services, and worker plugins.

## Implemented Baseline

HostAgent now has `HostAgent:MaterializeTemplates` and
`HostAgent:ProcessHostDeployments` settings, both enabled by default. At the
start of each cycle it can claim one pending `omp.HostDeployments` row for its
configured `HostKey`, materialize that requested host/template combination, and
mark the deployment as succeeded or failed. It then calls
`omp.MaterializeInstanceTemplate` for its configured `HostKey` so normal
template drift is also corrected before artifact provisioning.

Portal or scripts can enqueue a deployment by calling
`omp.RequestHostDeployment`.

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

HostAgent also has package-type handlers for IIS web apps and Windows service
apps. When `HostAgent:DeployWebApps` is enabled, provisioned `web-app`
artifacts are mirrored to the configured IIS runtime folders. When
`HostAgent:DeployServiceApps` is enabled, provisioned `service-app` artifacts
are mirrored to service runtime folders and the Windows services are created or
updated through `sc.exe`. Both handlers record outcomes in
`omp.HostAppDeploymentStates`. Local runtime configuration, logs, and
application data are excluded from the mirror by default.

## Remaining Steps

1. Add Portal actions for previewing and enqueuing host deployments from the
   template/admin pages.
2. Add any missing package-type handlers for worker-plugin installation paths.
   Worker plugins are already provisioned and consumed through WorkerManager,
   but drift/status views should make that explicit.
3. Add drift detection for IIS apps, Windows services, artifact versions, and
   runtime paths so operators can see whether a host matches the desired
   template.
4. Add rollback and rollout policy metadata: desired artifact version, canary or
   per-host sequencing, maintenance windows, and restart behavior.
5. Add a bootstrap/update story for HostAgent itself. A running HostAgent can
   safely update normal web apps and service apps, but replacing the HostAgent
   service requires an external bootstrapper, paired updater service, or
   controlled maintenance script.
6. Extend Portal admin pages with preview/apply actions for instance templates,
   host template assignments, and host deployments.

## Script Boundary

Once a host is bootstrapped, regular artifact version changes should flow
through OMP metadata and HostAgent instead of repeated package installer runs.
The PowerShell packages still have a role for first install, database schema
changes, HostAgent installation or repair, IIS site creation, service account
configuration, ACLs, and disaster recovery.

## Design Boundary

Templates describe desired metadata and placement. HostAgent should be the
host-local executor. It should not contain customer-specific module rules; module
installers must keep registering neutral artifacts and template rows that
HostAgent can consume generically.
