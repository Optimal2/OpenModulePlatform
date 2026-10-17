# ADR 0007: Config overlay installation-wide (AllHosts) scope

## Status

Decided (2026-10-17), implemented in Phase A of the overlay administration
campaign. Portal CRUD/effective-source UI follows in Phase B.

## Context

Config overlays were keyed to exactly one `hostKey`. Load-balanced or otherwise
equivalent hosts in the same installation therefore needed duplicated overlays
with identical content, and keeping them in sync was manual.

## Decision

**An explicit `hostScope` selector chooses the scope.** Portable JSON:

```json
{
  "formatVersion": 1,
  "objectType": "config-overlay",
  "overlayKey": "site-config",
  "overlayVersion": "1.0.0",
  "hostScope": "AllHosts",
  "moduleKey": "opendocviewer",
  "configurationFiles": [ ]
}
```

- `hostScope` absent, empty, or `"Host"` (case-insensitive) keeps the
  historical exact-host semantics; `hostKey` is then required.
- `hostScope: "AllHosts"` (also accepted: `all-hosts`, `all_hosts`) applies to
  every host in the **same connected OMP installation**, optionally narrowed
  by `moduleKey`/`appKey`/`packageType`/`targetName`/`artifactVersion`
  (minimum, ADR 0006). It never crosses installations: resolution runs against
  the connected database only, and no credentials or defaults are copied.
- `hostKey` for an AllHosts overlay is omitted or `"*"`; any other value is
  rejected. Internally AllHosts rows store `HostKey = '*'` (sentinel) and
  `HostScope = 'AllHosts'` in `omp.ConfigOverlayDocuments.HostScope`
  (`nvarchar(20) NOT NULL DEFAULT 'Host'`, check `IN ('Host','AllHosts')`).
  Because the sentinel is a distinct stored key, the existing unique indexes
  `(OverlayKey, HostKey, OverlayVersion)` and the filtered enabled index on
  `(OverlayKey, HostKey)` keep working unchanged: one enabled AllHosts version
  per overlay key, independently of host-specific rows.
- **Fail closed:** an unrecognized `hostScope` throws; a wildcard `hostKey`
  (`*`/`?`) without explicit AllHosts throws; a missing `hostKey` for Host
  scope throws. Legacy rows have `HostScope = 'Host'` by default, so a legacy
  `'*'` host key matches no real host and never broadcasts. Databases without
  the column resolve exact-host rows only.

## Precedence (per relative path)

Artifact base (priority 0) < AllHosts overlay (1) < host-specific overlay (2).
A host-specific overlay therefore beats a matching AllHosts overlay **even if
its `overlayVersion` is numerically older**. Within one scope the existing
rules hold: highest `overlayVersion` (`ArtifactVersionComparer`), then
`UpdatedUtc`, then row id. Merge/replace is layered in that order — a merge
row merges onto the current content (base, then AllHosts, then host), a
replace row discards it. Competing versions inside one scope are not layered;
only the scope winner applies.

## Persistence and round-trip

`ConfigOverlayPackageReader` normalizes `hostScope`/`hostKey` into the stored
document JSON, so the canonical document, the normalized
`ConfigOverlayConfigurationFiles` rows, and `OverlaySha256` stay in sync and
export/import round-trips the scope. Save paths (Portal and HostAgent) write
document and file rows in one transaction and only reference the `HostScope`
column when it exists. Legacy metadata-only documents are exported with
`configurationFiles` reconstructed from the normalized rows
(`ConfigOverlayExportJson`); stored file entries are never replaced and no
files are invented.

## Consequences

- Equivalent hosts share one AllHosts overlay; a host exception is one
  host-specific overlay that wins per file.
- Existing host-only overlays, imports, and universal packages are unchanged.
- Authoring AllHosts overlays in Portal (list/create/edit/effective view) is
  Phase B; Phase A ships the contract, resolution, persistence, DTOs
  (`ConfigOverlayAdminDtos`), and `GetConfigOverlaysApplicableToHostAsync`.
