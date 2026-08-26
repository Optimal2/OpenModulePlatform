# Changelog

All notable changes to this repository should be documented in this file.

The format is inspired by Keep a Changelog and the project follows semantic versioning at the repository level.

## [Unreleased]

### Added

- WorkerManager now publishes a `Draining` (6) observed state while a worker is
  finishing its in-flight job ahead of a configuration- or host-driven restart,
  instead of continuing to claim `Running`. The Portal worker pages and the
  example worker app label the new state, the app-instance summary ranks it
  deliberately (between Stopping and Starting), and the staleness downgrade
  covers it so a manager that dies mid-drain no longer pins `Draining` forever.
  HostAgent resource telemetry samples the live process of a draining worker too
  (observed states 1, 2, 3 and 6), since the process and its in-flight job's
  resource usage exist until the restart.

### Fixed

- WorkerManager robustness (review findings R7-F4–F7): the runtime-observation
  upsert now guards both foreign keys of `omp.WorkerInstanceRuntimeStates` (and
  the `omp.AppInstanceRuntimeStates` fallback write) instead of only the one the
  MERGE matches on, so an observation arriving after its app instance was
  deleted is dropped instead of faulting the publish; the HostAgent RPC caller
  identity WMI lookup disposes its result collection and every enumerated
  `ManagementObject`; and a broken OMP database worker catalog row (duplicate
  id, incompatible package type, unresolvable plugin path, unreadable value) is
  skipped per row instead of failing reconciliation for every worker on the
  host. The drain lifecycle (begin/cancel/timeout) is now covered by unit tests
  against the three historical drain defects (R5-F1, R6-F6/W6, R7-F1).

- Operator-edited artifact configuration files (`omp.ArtifactConfigurationFiles`)
  are no longer lost silently when a new artifact version is imported with
  packaged configuration files. Each package-registered row now stores the
  pristine packaged content in the new `PackageFileContent` baseline column, and
  HostAgent import, Portal upload, Portal universal import, and the Bootstrapper
  run a shared three-way carry-forward: when the packaged file is unchanged
  against the previous version's baseline, the operator-edited content and
  enabled state follow the new version automatically. When the packaged file
  changed over an operator edit, or the row predates the baseline column, the
  package file wins and the import result warns about the affected files instead
  of dropping the edits silently. Re-registering the same artifact version also
  preserves operator edits while the packaged file is unchanged.

- HostAgent now redeploys web apps and service apps when the artifact content
  SHA-256 changes behind an unchanged artifact id and version. The already-applied
  check compares the desired `omp.HostArtifactStates.ContentSha256` with the
  deployed `omp.HostAppDeploymentStates.ContentSha256`, so replaced artifact
  content no longer requires a version bump to reach the runtime.

> **Note:** This changelog was not maintained per-release after `0.1.0`. The repository has since advanced to the `0.3.x` release line. The authoritative current version is the `repositoryVersion` in `omp-components.json` (and the central metadata in `Directory.Build.props`) — not the newest entry below. Treat the `0.1.0` section as the initial-baseline record, not the current state; future notable changes should be logged here per the Keep a Changelog format.

## [0.1.0] - 2026-04-13

### Added

- initial public beta release line for OpenModulePlatform
- central version metadata in `Directory.Build.props`
- repository hygiene files: `.editorconfig`, `.gitattributes`, improved `.gitignore`
- GitHub Actions CI workflow for restore and release builds
- Dependabot configuration for NuGet packages and GitHub Actions
- worker runtime scaffold projects:
  - `OpenModulePlatform.WorkerManager.WindowsService`
  - `OpenModulePlatform.WorkerProcessHost`
  - `OpenModulePlatform.Worker.Abstractions`
- public contribution guidance and release-oriented documentation

### Changed

- standardized documentation in English for public release preparation
- strengthened public repository guidance and security documentation
- improved portal top bar JavaScript to coalesce resize-driven layout work into a single frame
- tightened several shared web implementation details during release preparation

### Notes

`0.1.0` is the first public beta baseline. The repository is intentionally useful and buildable, but some architectural areas remain under active design, especially template materialization, HostAgent, and the future worker runtime.
