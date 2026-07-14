# ADR 0004: Channel-type reconcile generalization

## Status

Proposed (2026-07-14)

## Context

OMP modules can declare *channel types* that are bound to a worker process at
runtime. A channel type may have multiple versions, and each version can point
to a specific artifact (`ArtifactId`) that HostAgent must provision on the host
before the worker can start. The canonical example is `IbsPackager`, which
routes files through configurable channels.

HostAgent discovers required artifacts from three sources today:

- `omp.AppInstances` assigned to the host (`RequirementKey` prefix
  `appinstance:`)
- `omp.WorkerInstances` assigned to the host (`RequirementKey` prefix
  `workerinstance:`)
- explicit `omp.HostArtifactRequirements` rows (`RequirementKey` prefix chosen
  by the creator)

`omp.HostArtifactRequirements` is the generic seam that module-specific code can
use to request additional artifacts for a host. HostAgent polls it like any
other desired-artifact source (`OpenModulePlatform.HostAgent.Runtime/Services/OmpHostArtifactRepository.cs:2981-2999`).

## Problem

Campaign AL solved channel-type artifact auto-provisioning only for
`IbsPackager`. The same exposure exists for every module that maps a
channel-type-version to an `ArtifactId`: when the version mapping, default
flag, pinned version, channel enabled flag, or desired state changes, the
module must keep `omp.HostArtifactRequirements` in sync or HostAgent will
provision the wrong artifact (or none at all).

In addition, the current reconcile SP is host-bound: it derives `HostId` from
`omp.AppInstances`/`omp.WorkerInstances`. A *host-neutral* or *worker-pool*
channel has no specific host to pull from, so there is no row in
`omp.HostArtifactRequirements` to drive provisioning. Such channels will need a
different negotiation mechanism, likely a HostAgent RPC call that asks HostAgent
to provision an artifact without pre-registering a host-specific requirement.

## Current state

### IbsPackager reconcile stored procedure

`IbsPackager/sql/1-setup-ibspackager.sql:215-293` defines
`omp_ibs_packager.usp_ReconcileChannelTypeArtifactRequirements`.

The procedure is idempotent and works in three phases:

1. **Compute effective artifact/host for every channel** (`sql:224-243` and
   `sql:261-280`). `ArtifactId` is resolved from
   `omp_ibs_packager.ChannelTypeVersions` using the rule:
   - pinned version wins if it is enabled;
   - otherwise the enabled default version wins;
   - fallback ordering is `IsDefault DESC, ChannelTypeVersionId DESC`.
   `HostId` is resolved as `COALESCE(ai.HostId, wi.HostId)` by joining
   `omp.AppInstances` and `omp.WorkerInstances`.
2. **Disable stale requirements** (`sql:245-258`). Any existing
   `omp.HostArtifactRequirements` row whose `RequirementKey` starts with
   `ibs_packager.channeltype:` is disabled when the channel no longer exists, is
   disabled, is stopped (`DesiredState <> 1`), has no host, has no effective
   artifact, or whose host/artifact no longer matches.
3. **Upsert active requirements** (`sql:281-292`). For every enabled, running,
   host-bound channel with a resolved artifact, `MERGE` inserts or re-enables a
   row in `omp.HostArtifactRequirements` keyed by `(HostId, RequirementKey)`.

The `RequirementKey` format is hard-coded to
`ibs_packager.channeltype:{ChannelId}` (`sql:239,274`).

### C# call sites

`IbsPackager.Runtime/Services/IbsPackagerRepository.cs` exposes
`ReconcileChannelTypeArtifactRequirementsAsync` (`cs:1800-1808`), which simply
executes the stored procedure. It is called after every mutating channel or
channel-type operation:

- `SaveChannelAsync` (`cs:1661`)
- `SetChannelEnabledAsync` (`cs:1732`)
- `SetChannelDesiredStateAsync` (`cs:1797`)
- `SaveChannelTypeVersionAsync` (`cs:1992`)
- `DeleteChannelTypeVersionAsync` (`cs:2053`)

The inline SQL in `SaveChannelAsync` (`cs:1614-1631`) also performs an
immediate reconcile for the saved channel before the global stored procedure
runs, so the pattern is duplicated between C# and the SP.

### Portal admin action

`IbsPackager.Web/Pages/Channels/Index.cshtml.cs:113-146` exposes an
`OnPostReconcileAsync` handler that administrators can trigger manually. After
calling `ReconcileChannelTypeArtifactRequirementsAsync`, it counts how many
`omp.HostArtifactRequirements` rows with the `ibs_packager.channeltype:` prefix
were updated in the last five seconds and reports that number back to the UI.

### Integration tests

`IbsPackager.Tests/ReconcileChannelTypeArtifactRequirementsTests.cs:10-497`
covers the four core reconcile outcomes:

- upsert/requirement for an enabled, running channel (`Tests:325-337`);
- disable requirement for a disabled channel (`Tests:339-351`);
- disable requirement for a stopped channel (`Tests:353-365`);
- update requirement artifact when the pinned version changes
  (`Tests:367-380`).

The tests deploy the procedure by parsing it directly from
`sql/1-setup-ibspackager.sql` (`Tests:63-107`).

### Other modules

A read-only survey of the OMP+ODV repositories found **no other module** with a
`ChannelTypeVersions`-style table or a reconcile procedure:

- `OpenDocViewer`: no `ChannelTypeVersions`, `usp_Reconcile`, or
  `HostArtifactRequirements` references.
- `LogSearch`: no matches.
- `EArkivChecker`: no matches.
- `Dokumentbibliotek`: no matches.
- `VajSkrivare`: no matches.
- `ODVGateway`: no matches.
- `iKrock2`: uses `omp.HostArtifactRequirements` directly in its init script
  (`iKrock2/sql/2-initialize-ikrock2.sql:243-284`) for its own backend service
  artifact, but does not have channel-type versions or a reconcile SP.

This means `IbsPackager` is both the first implementation and the template for
any future module with the same need.

## Proposals

### (a) Where does the reconcile seam live?

#### Option A1: Generalize to OMP-core

Move the reconcile logic into the OMP core schema and HostAgent runtime. The
core would own a generic notion of "channel type versions" and automatically
reconcile `omp.HostArtifactRequirements` for any module that registers such
metadata.

Pros:

- DRY: one implementation, consistent behavior across modules.
- Centralized idempotency, disable logic, and test coverage.
- Portal could expose a single "Reconcile channel requirements" action.

Cons:

- Core must know about module-specific `ChannelTypeVersions` tables or a new
  shared table shape, creating schema coupling.
- Module authors lose flexibility in how they resolve effective versions or
  hosts.
- A core change affects every module; regression blast radius is large.

#### Option A2: Keep per-module, provide a contract/template

Leave the SP and C# code in each module (as in `IbsPackager`). OMP documents the
required table shape, `RequirementKey` prefix convention, and the set of call
sites where reconcile must run. Each module implements its own SP in its own
schema.

Pros:

- Module independence: each module owns its own schema and version-resolution
  rules.
- No core schema coupling.
- `IbsPackager` already proves the pattern works end-to-end.

Cons:

- Duplicated pattern across modules.
- Drift risk: a future module may forget a call site or implement the disable
  logic differently.
- Tests must be duplicated or abstracted per module.

#### Option A3: Hybrid — core generic reconcile SP with module config

OMP-core provides a generic stored procedure (for example
`omp.usp_ReconcileChannelTypeArtifactRequirements`) that accepts module
configuration such as schema name, channel table name, version table name, and
requirement-key prefix. Each module registers its metadata in a core table.

Pros:

- DRY reconcile implementation without hard-coding module schemas in core.
- Core can enforce idempotency and disable semantics consistently.

Cons:

- Dynamic SQL inside the core SP increases complexity and complicates testing.
- Schema/table names must be validated to prevent SQL injection.
- Module authors still need to register config and call the generic SP at the
  right places.

**Recommendation:** Start with **Option A2** for the next module that needs the
pattern, then evaluate **Option A3** once two or three modules have duplicated
nearly identical SPs. Avoid **Option A1** until the core schema can own the
channel-type-version abstraction without pulling module-specific concepts into
OMP core.

> **ÄGARBESLUT (a):** Decide whether the next module with channel-type artifact
> mapping should implement its own reconcile SP (Option A2), or whether OMP
> core should provide a generic/hybrid seam (Option A3) before that module is
> built.

### (b) Host-neutral / worker-pool channels

#### Option B1: Build the HostAgent RPC now

Extend the existing named-pipe RPC (`docs/HOST_AGENT.md:37-41`) with a new
operation (for example `ensureChannelArtifact`) that a worker manager or module
runtime can call to ask HostAgent to provision a channel-type artifact without
a pre-existing `omp.HostArtifactRequirements` row.

Pros:

- Future-proof for host-neutral channels.
- Keeps HostAgent as the single provisioner of artifacts.
- Can reuse the existing `ensureArtifact` RPC shape.

Cons:

- Speculative: no host-neutral channel exists today.
- Requires new RPC contract, ACL review, and caller identity logging.
- Requires HostAgent to resolve the artifact from channel metadata (possibly
  via a new database lookup or RPC payload).

#### Option B2: Defer until needed

Document the gap but do not build the RPC. If a host-neutral channel is
introduced later, the requirement can be designed with concrete use cases.

Pros:

- YAGNI: avoids speculative complexity.
- Keeps HostAgent RPC surface minimal.

Cons:

- Design debt if the need becomes urgent.
- A future module may invent an ad-hoc workaround (for example copying the
  artifact into every worker package) instead of using HostAgent.

#### Option B3: Worker-pool provisioning table

Introduce a new table such as `omp.WorkerPoolArtifactRequirements` that
represents artifact needs for a pool of workers rather than a single host.
HostAgent would resolve a pool requirement to the local host when a worker in
that pool starts.

Pros:

- Database-first model fits the existing HostAgent polling pattern.
- No runtime RPC needed.

Cons:

- Adds a new core table and HostAgent query path.
- Does not solve host-neutral channels that have no pool mapping either.
- Over-engineered until worker-pool semantics are well defined.

**Recommendation:** Choose **Option B2** now and record the decision. Add an
RPC-based approach (B1) or a pool table (B3) only when a concrete host-neutral
channel design is proposed. The existing `ensureArtifact` RPC already provides
an emergency path for worker processes that need an artifact before starting
(`docs/HOST_AGENT.md:141-155`).

> **ÄGARBESLUT (b):** Decide whether to build a HostAgent RPC for host-neutral
> channel provisioning now, or document the gap and defer until a concrete
> host-neutral channel requirement exists.

## Migration considerations

- If Option A3 is chosen later, the existing
  `omp_ibs_packager.usp_ReconcileChannelTypeArtifactRequirements` can remain as
  a module-specific wrapper or be migrated to the generic core SP.
- Any generalization must preserve the `RequirementKey` prefix convention
  (`<module>.channeltype:<channel-id>`) so HostAgent and retention logic can
  identify channel-type requirements.
- Host-neutral channels (when implemented) must not break the existing
  host-bound reconcile flow; they are an additive seam.
- Tests should be abstracted so the same scenarios can run against both a
  module-specific SP and a generic core SP.

## Decision

TBD pending owner decisions on **(a)** and **(b)** above.
