# Changelog

All notable changes to this repository should be documented in this file.

The format is inspired by Keep a Changelog and the project follows semantic versioning at the repository level.

## [Unreleased]

### Added

- **X.509 certificate key-ring protection for ASP.NET Core Data Protection**, as
  a second encryption-at-rest mode alongside the AD-backed DPAPI-NG descriptor,
  for web farms without Active Directory. Configured with
  `OmpAuth:DataProtectionCertificateThumbprint` (looked up in
  `LocalMachine\My`). Precedence is descriptor > certificate > off > legacy
  DPAPI, and configuring both descriptor and certificate is a loud startup error
  rather than a guessed precedence. A missing certificate, a missing private
  key, a non-RSA private key, a malformed thumbprint (not 40 hex characters), or
  an expired/not-yet-valid certificate each fail startup with the thumbprint
  named -- there is never a silent fallback. Rotation is supported through
  `OmpAuth:DataProtectionRetiredCertificateThumbprints`, which feeds
  `UnprotectKeysWithAnyCertificate` so key files encrypted to an outgoing
  certificate stay readable; an expired retired certificate is accepted by
  design, but a retired entry that cannot be resolved fails startup. The
  protection-choice matrix, the post-deploy verification steps and the
  which-apps-share-a-ring analysis are in `docs/HOST_AGENT.md`.

- **Bulk AD-to-OMP role principal migration** at
  `/admin/security/ad-principal-migration` (a friendly route added in
  `OpenModulePlatform.Portal/Program.cs` for the page
  `Pages/Admin/Rbac/AdPrincipalMigration.cshtml`, which also stays reachable at
  its conventional path). It lists all `ADUser`/`User` role
  rows, previews the resolution per row (including rows that cannot be linked,
  with the reason, and a risk note when the target user has no non-AD sign-in
  link), and executes only after explicit confirmation -- in a serializable
  transaction, idempotently via `WHERE NOT EXISTS`, retried exactly once on a
  deadlock (SQL error 1205). Source AD rows are retained by design and `ADGroup`
  rows are never touched.

- **Opt-in OIDC sign-in diagnostics** (`OmpAuth:Oidc:Diagnostics`, default off;
  claim values only with `IncludeClaimValues`), plus a warn-once report for
  configured AD-mapping claim types that were absent from a validated sign-in.

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

- **The OIDC/ADFS sign-in path no longer depends on a single claim mapping for
  the `DOMAIN\name` principal form.** The claim resolver reads `unique_name` and
  `windowsaccountname` (short names and WS URIs) as user-principal candidates
  and, when `TranslateSidClaimsToAccountNames` is enabled (the default),
  translates SID claims to account names on the domain-joined auth server,
  mirroring the Windows path. Group claims are enriched in both directions, so a
  role row matches whether the provider sends a SID or a `DOMAIN\Group` name.
  Translation is fail-safe and cached per sign-in.

- **Ambiguous AD-user links now fail closed instead of guessing.** The RBAC role
  admin page resolved an AD user principal with `SELECT TOP (1) ... ORDER BY
  user_id`. Uniqueness on `omp.user_auth` is `(provider_id,
  provider_user_hash)`, a SHA-256 over the raw key and therefore case-sensitive,
  so two active AD links differing only in letter case can point at different
  OMP users -- and the page silently rewrote the principal to the lowest user
  id. The lookup now counts distinct active linked users and abstains when more
  than one resolves, reporting the ambiguity with the same wording as the bulk
  move. No role is assigned on a guess. The page also reports every
  `ADUser -> OmpUser` rewrite with its reason and offers a preserve-literal
  checkbox for storing the exact `DOMAIN\name` principal when that is intended.

- **Module-definition SQL is deferred when an artifact in the same import
  fails.** A failed artifact item (a version conflict, for example) previously
  still let the definition SQL run, recording a `Succeeded` execution over the
  pre-failure artifact state; the version gate would then never re-run it after
  the artifact was repaired and re-imported. With the SQL deferred, no execution
  is recorded and the next clean import runs the scripts. Portal defers only
  when `ExecuteSqlRepairs` is on and the module is not platform core.

- **A definition-SQL failure in Portal no longer causes a double artifact
  import.** The reordered SQL phase threw out of `ImportAsync` after the
  artifacts had imported, losing their results; the standalone artifact
  fall-through then imported the same artifacts a second time, rewriting
  configuration rows and reporting the fresh import as an identical skip.
  `ImportAsync` now reports the failure in the result (`DefinitionSqlError`)
  when the universal loop asks it to, and the loop marks the module-definition
  item failed while keeping the artifact results. Legacy single-module import
  paths keep the throwing contract.

- **`bump-version.ps1` now carries the module definition's own
  `definitionVersion` with a component bump.** A component bump rewrites
  `compatibleArtifacts.maxVersion` in the module definition, which changes the
  definition -- and HostAgent rejects a re-imported definition carrying the same
  `definitionVersion` with different content. The bump left that version
  untouched, so `local-ci` and the pre-push gate refused the result with a
  message that never named the second command the operator then had to find. A
  definition touched by a component bump is now added to the normal selection
  and goes through exactly the same path as an explicit `-ModuleKey`.

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
