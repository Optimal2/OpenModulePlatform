# ADR 0002: HostAgent deploy-set consistency check

## Status

Proposed (2026-07-13)

## Context

OMP deployments are made of several independently versioned artifacts that must
run as a consistent set:

- web apps (`packageType: web-app`)
- service apps (`packageType: service-app`)
- worker hosts / worker plugins
- HostAgent itself (`packageType: host-agent`)
- non-runtime compatibility artifacts such as `channel-type` packages that are
  consumed as runtime binding metadata or plugin dependencies

Each artifact is stored in `omp.Artifacts` with its own version, content hash,
and compatibility slot. HostAgent provisions and deploys each artifact
independently.

## Problem

On a local install a FileDrop channel (`local_file_drop_test`) threw a
`MissingMethodException`. The deployed set was mixed: the web app and worker
had been bumped (to `0.3.30` / `0.3.18`) but the deployed
`IbsPackager.ChannelTypes.FileDrop` runtime remained an older binary built
against a previous `IbsPackager.Runtime`. The channel assembly loaded, but the
method signature it expected no longer existed in the newer shared runtime.

The immediate fix is an **operator redeploy** (rebuild and deploy a mutually
consistent set). That operational repair is outside the scope of this ADR; this
ADR records the gap that allowed a mixed set to be deployed in the first place.

## Current state

### Source-level lockstep is already gated at build time

`scripts/omp/validate-component-versions.ps1` "Check 10" validates that every
component version listed in `omp-components.json` falls inside the
`compatibleArtifacts` min/max range declared by its module definition
(`validate-component-versions.ps1:457-499`). It also performs a transitive
`ProjectReference` lockstep bump check ("Check 9",
`validate-component-versions.ps1:830-965`).

`omp-components.json` carries a single `repositoryVersion` for the whole
repository (`omp-components.json:4`). That value is written into the
HostAgent-first installer manifest (`scripts/deployment/package-hostagent-first.ps1:2015-2018`)
but it is **not** propagated into individual artifact packages or into the OMP
database.

### HostAgent verifies each artifact individually

The HostAgent runtime has rich per-artifact metadata and hashing, but no notion
of a "set":

- `ArtifactDescriptor` carries `ArtifactId`, `Version`, `PackageType`,
  `TargetName`, `RelativePath`, and `Sha256`
  (`OpenModulePlatform.HostAgent.Runtime/Models/ArtifactDescriptor.cs:3-22`).
- `ArtifactHash.ComputeSha256Async` computes a deterministic SHA-256 over a file
  or directory (`OpenModulePlatform.HostAgent.Runtime/Services/ArtifactHash.cs:8-48`).
- `ArtifactProvisioner.EnsureAsync` checks the local cache hash against the
  expected `Sha256`, moves corrupt copies aside, and re-provisions from the
  central artifact store
  (`OpenModulePlatform.HostAgent.Runtime/Services/ArtifactProvisioner.cs:20-102`).
- `ArtifactZipImportService.ImportArtifactPackageAsync` registers one artifact
  at a time, validates the extracted payload hash, rejects duplicate identity
  with different content, and applies the artifact to matching `AppInstances`,
  `WorkerInstances`, and `InstanceTemplateAppInstances`
  (`OpenModulePlatform.HostAgent.Runtime/Services/ArtifactZipImportService.cs:600-870`).
- `HostAgentEngine.RunOnceAsync` resolves desired artifacts via
  `OmpHostArtifactRepository.GetDesiredArtifactsAsync`, provisions each via
  `EnsureAndPublishAsync`, then calls `WebAppDeploymentService` and
  `ServiceAppDeploymentService` independently
  (`OpenModulePlatform.HostAgent.Runtime/Services/HostAgentEngine.cs:162-186`).
- `WebAppDeploymentDescriptor` and `ServiceAppDeploymentDescriptor` carry only
  per-artifact version/hash fields; `IsAlreadyApplied` compares the currently
  deployed artifact with the desired one for that single app instance
  (`OpenModulePlatform.HostAgent.Runtime/Services/WebAppDeploymentService.cs:154-164,359-370`;
  `OpenModulePlatform.HostAgent.Runtime/Services/ServiceAppDeploymentService.cs:130-159,379-393`).

### Module definitions define per-slot compatibility, not per-build sets

A module definition declares `compatibleArtifacts` entries that constrain the
version range for one app/package-type/target slot
(`docs/MODULE_DEFINITIONS.md:133-142`). Those ranges are stored in
`omp.ModuleDefinitionArtifactCompatibility`
(`sql/1-setup-openmoduleplatform.sql:434-452`). They prevent importing an
artifact version that the module definition does not expect, but they do **not**
express a relationship between two different slots (for example "the web app
`0.3.30` must run with channel-type-runtime `0.3.30`").

### Artifact packages carry no build provenance

The `omp-artifact-package.json` envelope currently contains only `formatVersion`,
`payload`, optional `moduleDefinition.minVersion`, and `configurationFiles`
(`docs/ARTIFACT_PACKAGES.md:83-103`;
`OpenModulePlatform.Artifacts/ArtifactPackageExtractor.cs:111-118`;
`OpenModulePlatform.Artifacts/ArtifactPackageWriter.cs:68-90`). There is no
field for `repositoryVersion`, `buildId`, `builtAgainst`, or a set identifier.

## Gap confirmation

**What IS checked today:**

1. Each artifact's content matches its stored SHA-256 before it is accepted or
   deployed.
2. Each artifact version is inside the `compatibleArtifacts` min/max range of
   the applied module definition for its own slot.
3. Source components that share changed `ProjectReference` dependencies are
   forced to bump in lockstep before a package is built.

**What is NOT checked today:**

1. There is no database column, artifact manifest field, or HostAgent check that
   records which build / `repositoryVersion` an artifact came from.
2. `GetDesiredArtifactsAsync` returns independent rows; HostAgent never groups
   them by module instance or suite and asks "do these artifacts belong to the
   same build?"
3. `WebAppDeploymentService.DeployAsync` and
   `ServiceAppDeploymentService.DeployAsync` deploy one app instance at a time
   and never compare the version of the web/service artifact with the version of
   a related channel-type-runtime, worker plugin, or shared library.
4. `channel-type` and other non-runtime packages are explicitly skipped from
   auto-applying to `AppInstances` because they are "compatibility/channel
   metadata rather than a runtime artifact"
   (`OpenModulePlatform.Portal/Services/OmpAdminRepository.Editor.cs:3305-3307`),
   which is correct for their package type but means the only thing keeping them
   consistent with the runtime that loads them is the operator's build/package
   discipline.

The gap is real: the runtime failure was caused by a version skew **between**
artifacts, not by a bad artifact in isolation. No current HostAgent, Portal, or
module-definition check would have caught the skew before deployment.

## Proposal

Introduce a deploy-set consistency check that runs at import/provision or
deploy time and verifies that all artifacts intended to run together for a
module/suite came from the same consistent build.

### Mechanism A: Manifest-declared expected set (recommended)

Add an optional top-level section to the module-definition document that
declares one or more **consistent artifact sets** for the module. Example shape:

```json
{
  "consistentArtifactSets": [
    {
      "setKey": "default",
      "description": "Mutually consistent web, service, and FileDrop runtime.",
      "expectedArtifacts": [
        { "appKey": "ibs_packager_web", "packageType": "web-app", "targetName": "ibs-packager-web" },
        { "appKey": "ibs_packager_worker", "packageType": "worker", "targetName": "ibs-packager-worker" },
        { "appKey": "ibs_packager_filedrop", "packageType": "channel-type", "targetName": "filedrop" }
      ],
      "versionMatchRule": "exact"
    }
  ]
}
```

Rules the check would enforce:

- For every active `ModuleInstance` / `AppInstance` / `WorkerInstance` whose
  artifacts belong to a declared set, all set members currently selected must
  have the **same version** (or satisfy the declared `versionMatchRule`).
- The check runs in HostAgent after `GetDesiredArtifactsAsync` resolves the
  desired set and before deployment is attempted.
- If a set declaration is missing, HostAgent behaves exactly as today.

**Why A is preferred:** the module definition is already the authoritative
contract for what artifacts a module expects. The truth lives in one place, is
versioned, and can be reviewed in Portal's module-definition integrity matrix.

### Mechanism B: Derived from artifact-embedded metadata

Extend `omp-artifact-package.json` to include build provenance:

```json
{
  "buildProvenance": {
    "repositoryKey": "openmoduleplatform",
    "repositoryVersion": "0.3.242",
    "buildId": "20260713.1",
    "builtAgainst": {
      "IbsPackager.Runtime": "0.3.30"
    }
  }
}
```

HostAgent would read this metadata from every artifact in a module's deployed
set and warn/block when values differ.

**Trade-offs:** more automatic for the operator, but the truth is scattered
across every artifact and is only as reliable as each builder. It also requires
all artifact builders (including private/customer repositories and legacy
packages) to emit the new fields before it can be enforced.

### Comparison

| Concern | Mechanism A (manifest-declared) | Mechanism B (artifact-derived) |
|---|---|---|
| Source of truth | module-definition document | each artifact package |
| Operator visibility | explicit in Portal integrity matrix | implicit; requires inspection of each package |
| Builder impact | module author adds one JSON section | every artifact builder must inject provenance |
| Backward compatibility | optional; missing = no constraint | missing fields must be tolerated for old packages |
| Catch mixed set caused by partial rebuild | yes, if set is declared | yes, if all artifacts carry correct metadata |
| Works for channel-type / metadata packages | yes, they are listed in the set | yes, if they carry metadata |

## Decision flags for ÄGARBESLUT

(a) **WARN vs BLOCK on mixed set**

- **WARN** (recommended first step): HostAgent logs a high-severity warning and
  surfaces a diagnostic on the deployment state. Existing mixed sets continue to
  run while the operator plans a rebuild.
- **BLOCK**: HostAgent refuses to deploy any member of the inconsistent set.
  This is the long-term desired state, but it must be opt-in per host or
  environment until existing installations have been audited.

(b) **WHERE the expected consistent set is registered**

- **Option 1 (recommended)**: add the set declaration to the module-definition
  document (`consistentArtifactSets`), versioned with `definitionVersion`.
- **Option 2**: add a separate `deploy-set-manifest.json` referenced from
  `omp-components.json`. This decouples set checks from module definition
  versioning but introduces another artifact to import and keep in sync.

(c) **SCOPE**

- **Per module instance (recommended)**: the check covers all artifacts that
  belong to one module instance / suite.
- **Global**: the check covers every active artifact across all modules in the
  installation. Stricter, but likely over-broad when unrelated modules are
  versioned independently.

## Migration considerations

1. **Optional opt-in.** The new check must not break existing installations. A
   module definition without `consistentArtifactSets` is treated as having no
   set constraints.
2. **HostAgent setting.** Add a HostAgent setting such as
   `DeploySetConsistencyMode` with values `None`, `Warn`, and `Block`. Default
   should start as `Warn`.
3. **Definition version bump.** Adding or changing `consistentArtifactSets`
   requires a normal `definitionVersion` bump and re-running
   `scripts/dev/embed-module-definition-sql.ps1`, per the module-definition SQL
   change rule in `AGENTS.md`.
4. **Existing channel-type packages.** Because `channel-type` packages are not
   auto-applied to `AppInstances`, the check must look at module-owned metadata
   (for example worker plugin bindings or channel-type selections) to discover
   which channel-type artifact is part of the deployed set.
5. **Manual override.** When `Block` is enabled, Portal should allow an
   administrator to mark a specific artifact or set as approved-for-deploy so
   emergency fixes are not impossible.

## Consequences

- Once adopted, mixed sets like the FileDrop incident become visible at deploy
  time rather than as runtime `MissingMethodException` failures.
- Module authors gain an explicit, versioned place to document which artifacts
  must move together.
- HostAgent gains a new diagnostic dimension without changing the per-artifact
  provisioning hash semantics.
- The change is additive: existing definitions, artifact packages, and HostAgent
  deployments continue to work until a module opts in.

## References

- `OpenModulePlatform.HostAgent.Runtime/Models/ArtifactDescriptor.cs`
- `OpenModulePlatform.HostAgent.Runtime/Models/WebAppDeploymentDescriptor.cs`
- `OpenModulePlatform.HostAgent.Runtime/Models/ServiceAppDeploymentDescriptor.cs`
- `OpenModulePlatform.HostAgent.Runtime/Services/ArtifactHash.cs`
- `OpenModulePlatform.HostAgent.Runtime/Services/ArtifactProvisioner.cs`
- `OpenModulePlatform.HostAgent.Runtime/Services/ArtifactZipImportService.cs`
- `OpenModulePlatform.HostAgent.Runtime/Services/HostAgentEngine.cs`
- `OpenModulePlatform.HostAgent.Runtime/Services/OmpHostArtifactRepository.cs`
- `OpenModulePlatform.Artifacts/ArtifactPackageExtractor.cs`
- `OpenModulePlatform.Artifacts/ArtifactPackageWriter.cs`
- `scripts/omp/validate-component-versions.ps1`
- `docs/MODULE_DEFINITIONS.md`
- `docs/ARTIFACT_PACKAGES.md`
- `docs/UNIVERSAL_MODULE_PACKAGES.md`
- `sql/1-setup-openmoduleplatform.sql`
