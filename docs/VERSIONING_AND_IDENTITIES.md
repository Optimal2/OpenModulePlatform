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

`Repository version` is an optional source-control or release-bundle version.
It may describe a coordinated release from a repository, but it is not the
version HostAgent deploys.

`Component version` is the version of one deployable application or package in a
repository. This is the value stored on `omp.Artifacts.Version`.

`Artifact` is an immutable deployable output for one `omp.Apps` definition. A
new build of the same app creates a new artifact row. Existing artifact rows are
kept for audit, rollback, and host-state comparison until a retention policy
removes them.

Artifact content is application code and static assets, not environment-owned
runtime configuration. Do not package `appsettings*.json`, generated OMP
identity files, database connection strings, passwords, or site-local files such
as `odv.site.config.js` into deployable artifacts. Simple OMP runtime
configuration is written by the bootstrap/deployment layer, and app-specific
deployment-owned files belong in `omp.ArtifactConfigurationFiles`.

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

Source repositories should expose one authoritative component version for each
deployable build output. Installation scripts may pass that value into SQL, but
SQL initialization files should not become the long-term source of truth for
normal release versions.

A repository may contain one deployable app or several deployable apps. In a
single-app repository, the repository version and component version can be the
same value. In a multi-app repository, each deployable component must be able to
move independently. A repository tag or release can still group the work for
human review, but OMP must register artifacts by component version.

For example, a repository containing a web app and a backend service should be
able to register a new `service-app` artifact without registering a new
`web-app` artifact. The web app instance keeps pointing to its existing
`ArtifactId`, while the service app instance or template app instance is moved
to the new service artifact.

Repositories should keep a root `omp-components.json` manifest. The recommended
shape for multi-app repositories is a component list keyed by stable OMP
identity fields:

```text
repository key
optional repository release version
components:
  - module key
  - app key
  - package type
  - target name
  - component version
  - package relative path
  - package hash
```

The combination of `module key`, `app key`, `package type`, and `target name`
identifies which deployable component the version belongs to. `component
version` identifies the specific build output for that component.

Repositories may also list bootstrap infrastructure components that are
deployable but not yet modeled as normal OMP app artifacts. Those components
should use `registrationMode = bootstrap` in the manifest so they remain visible
to release tooling without pretending that HostAgent can already update them as
ordinary app instances.

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

Source-controlled SQL initialization should therefore resolve existing rows by
stable keys before inserting new rows with `NEWID()`. Hardcoded GUID literals are
acceptable only for explicitly documented built-in seed rows where the GUID is a
contract, not a convenience.

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

Runtime identity is not the same thing as artifact identity. An artifact can be
deployed to several hosts, but the runtime should identify itself with the
concrete row that owns its state:

- Host-neutral IIS apps normally use the same `AppInstanceId` on every web host.
  Their deployment state is host-specific through
  `omp.HostAppDeploymentStates(HostId, AppInstanceId)`, while their user-facing
  content and portal entry remain one logical app.
- Services that run concurrently on several hosts normally need one
  host-specific `AppInstance` per host, because app runtime state and heartbeat
  rows are keyed by `AppInstanceId`.
- Worker plugins use `WorkerInstanceId` below `AppInstanceId` when several
  independent processes must run for the same app, especially on the same host.

Artifact configuration files may use HostAgent tokens such as
`{{Omp.AppInstanceId}}` and `{{Omp.HostId}}` so a single artifact configuration
template can write the correct runtime identity for each concrete deployment.

## Roadmap

1. Keep this document as the shared contract for OMP and consumer module
   repositories.
2. Teach package and installer scripts to consume `omp-components.json` so they
   can read component versions, app keys, package types, package paths, hashes,
   and stable seed identities from one source.
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
