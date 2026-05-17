# Artifact Versioning and Stable Identities

This document defines the OMP policy for deployable artifact versions and
stable identities. It is intentionally platform-neutral: consumer modules should
follow the same rules without adding environment-specific assumptions to this
repository.

## Goals

- Every deployable application must have an `omp.Artifacts` row for the exact
  build output that HostAgent should provision.
- Version changes must be explicit metadata changes, not repeated manual
  installer runs after a host has been bootstrapped.
- Human-stable keys must be used for matching across installs and upgrades.
- Database identifiers must be stable once created, but generated automatically
  for new administrator-created objects instead of copied from placeholder test
  values.
- HostAgent must execute desired state. It must not infer product-specific
  upgrade rules from file names, folder names, or consumer module conventions.

## Concepts

`Version` is the application or package version that produced an artifact. OMP
stores it as text because different module repositories may have their own
release tooling, but new deployable artifacts should use SemVer-compatible
versions whenever practical.

`Artifact` is an immutable deployable output for one `omp.Apps` definition. A
new build of the same app creates a new artifact row. Existing artifact rows are
kept for audit, rollback, and host-state comparison until a retention policy
removes them.

`Desired artifact` is the artifact currently selected by an app instance,
template app instance, worker instance, or host artifact requirement. HostAgent
deploys that selected artifact. It does not choose the highest version on its
own.

`Stable key` is a text key such as `ModuleKey`, `AppKey`, `InstanceKey`,
`ModuleInstanceKey`, `AppInstanceKey`, `HostKey`, or `WorkerInstanceKey`.
Installers, templates, and administrators should use these keys to match
existing rows across reruns.

`Database identity` is the primary key persisted in OMP. Definition and
artifact tables use integer identities. Concrete instance rows use GUIDs because
they may be referenced by runtime state, portal entries, logs, and external
automation.

## Version Policy

Source repositories should expose one authoritative version for the build
output being installed. Installation scripts may pass that value into SQL, but
SQL initialization files should not become the long-term source of truth for
normal release versions.

When a new version is produced:

1. Publish or copy the package contents into the central artifact store.
2. Register a new `omp.Artifacts` row with the app, version, package type,
   relative path, and content hash.
3. Point the desired app instance or template app instance to the new
   `ArtifactId`.
4. Let HostAgent provision and deploy the selected artifact on each matching
   host.

Changing `omp.AppInstances.ArtifactId` or
`omp.InstanceTemplateAppInstances.DesiredArtifactId` is the version switch.
Disabling or deleting old artifacts is a cleanup decision, not the upgrade
mechanism.

Package type describes the deployment contract that HostAgent or another OMP
runtime component should execute. Use stable values such as:

- `web-app` for IIS-hosted web applications.
- `service-app` for Windows services.
- `worker-plugin` for worker plugins consumed by WorkerManager.

Archive or transport format should be modeled separately if OMP later supports
zip, NuGet, HTTP, or cloud-backed artifact sources. Package type should not be
overloaded to mean both "what this app is" and "how these bytes are packed".

## GUID and Identity Policy

Stable text keys are the preferred cross-install identity. GUID primary keys are
runtime identities and should be treated as persisted database state.

Use deterministic GUIDs only for built-in seed rows where the identity is part
of the bootstrap contract and must survive repeated setup runs. Such values must
be documented next to the seed data.

Use generated GUIDs for:

- administrator-created instances, hosts, app instances, and worker instances;
- rows materialized from templates when no matching stable key already exists;
- environment-specific rows created by local or deployment configuration.

Do not copy obvious placeholder or test GUIDs into production-oriented module
configuration. If a deployment needs a stable row across reruns, the stable text
key should be used to find the row first; only a brand-new row should receive a
new GUID.

Portal create pages and stored procedures should generate GUIDs for new rows
when the caller does not provide one. Portal may expose a "generate" action for
new or draft objects, but regeneration should not be offered for persisted rows
that may already be referenced by runtime state, portal entries, permissions,
logs, or HostAgent deployment state.

HostAgent should not invent application identities during deployment. It may
materialize template rows through OMP stored procedures, and those procedures
may generate GUIDs for new concrete rows after matching by stable keys.

## Template and HostAgent Rules

Templates describe desired metadata and placement. Concrete rows are matched by
stable keys:

- module instances by `(InstanceId, ModuleInstanceKey)`;
- app instances by `(ModuleInstanceId, AppInstanceKey)`;
- hosts by `(InstanceId, HostKey)`;
- worker instances by `(AppInstanceId, WorkerInstanceKey)`.

Host-neutral web app instances use `HostId = NULL`. Each HostAgent deploys the
same logical app locally, while host-specific deployment state remains keyed by
the concrete host.

Host-specific services and worker processes should use concrete hosts or worker
instance metadata when placement matters. Their code must still handle
multi-host execution according to the app's own runtime contract.

## Roadmap

1. Keep this document as the shared contract for OMP and consumer module
   repositories.
2. Add a small manifest convention for module repositories so installers can
   read version, app keys, package types, and stable seed identities from one
   source.
3. Add schema constraints or validation to prevent duplicate active artifacts
   for the same app/version/package/target combination when that can be done
   without breaking existing installations.
4. Improve Portal artifact administration so selecting a new artifact for an
   app instance or template app instance is the standard upgrade path.
5. Improve Portal and stored-procedure creation flows so new GUID identities are
   generated consistently and placeholder GUIDs are not needed in configuration.
6. Add drift, rollout, rollback, and retention views around HostAgent
   deployment state.
7. Keep PowerShell packages for bootstrap, schema changes, service account and
   IIS site setup, HostAgent repair, ACLs, and disaster recovery. Use HostAgent
   for regular artifact version changes after bootstrap.
