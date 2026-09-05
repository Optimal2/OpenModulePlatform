# ADR 0006: Config overlay artifactVersion is a minimum version

## Status

Decided (2026-09-05), implemented in the same change.

## Context

A config overlay can narrow itself to an artifact build through the optional
`artifactVersion` selector (`docs/CONFIG_OVERLAYS.md`). Until this change the
selector was an **exact match**: the resolution query in
`OmpHostArtifactRepository.GetArtifactConfigurationFilesAsync`
(`OpenModulePlatform.HostAgent.Runtime/Services/OmpHostArtifactRepository.cs`)
compared `overlay.ArtifactVersion = @Version` as a string in SQL.

The measured consequence (customer test host, August 2026, documented in
`docs/CONFIG_OVERLAYS.md`): an Auth overlay pinned to one artifact version
silently stopped applying at the next artifact upgrade, and the following
deployment fell back to the artifact's own configuration, losing the OIDC
section. The failure mode is silent because nothing in the deployment result
said the overlay had stopped matching.

## Problem

Two gaps, one root cause:

1. Exact-match pinning ties an environment description to one build. The
   overlay's content (server names, paths, URLs) does not change when the
   artifact is rebuilt, so the match should survive upgrades by default.
2. When a versioned overlay does **not** match, the deployment carries no
   signal. An operator only notices when the application misbehaves.

## Decision

**`artifactVersion` is a minimum version.** An overlay with
`artifactVersion: "0.3.183"` applies to artifact `0.3.183` and every later
version, and does not apply to `0.3.100`. An overlay without
`artifactVersion` is unconstrained, exactly as before.

Comparison uses `ArtifactVersionComparer`
(`OpenModulePlatform.Artifacts/ArtifactVersionComparer.cs`) in C#, never
string comparison in SQL: the query returns candidate rows and the pin, and
the C# resolution loop filters them. SQL Server string ordering of
`nvarchar` versions (`"0.3.100" > "0.3.99"` lexically) cannot express
numeric component ordering, and pushing the comparison into SQL would
duplicate a comparer that already exists and is tested.

When an enabled overlay matches every selector **except** the version floor,
the deployment result carries a diagnostic warning naming the overlay key,
the overlay version, the pinned minimum, and the artifact version being
deployed. This is the normal rollback case (the overlay was written for a
newer build), so it must be visible without failing the deployment.

## Alternatives considered

- **Keep exact match.** Rejected: it is the measured failure mode. Pinning
  remains available implicitly — an overlay that must not outlive a build
  can be disabled or superseded by a new `overlayVersion` — but the default
  reading of a versioned overlay is now "from this build onwards".
- **Interval (`minArtifactVersion` + `maxArtifactVersion`).** Rejected for
  now: no caller has asked for an upper bound, two fields invite
  half-configured ranges, and an upper bound can be added later as a second
  optional field without changing the meaning of the existing one.
- **Compare in SQL.** Rejected: see above; lexical ordering is wrong and the
  comparer belongs in one place.

## Consequences

- Overlays already pinned to the currently deployed version keep applying
  after upgrades — this is the fix, and it changes behavior for databases
  that relied on exact-match expiry. Those overlays were already documented
  as fragile; the documentation now describes the minimum semantics.
- Rolling back to an artifact older than an overlay's minimum no longer
  applies that overlay, and the deployment result says so.
- The resolution query no longer filters on version in SQL, so it can
  return a few more candidate rows per deployment; the C# filter discards
  them before winner selection.
