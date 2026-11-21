# Scripts

This folder contains only reusable development and packaging helpers that are
still part of the current OMP object/installer model.

## Current Scripts

- `bump-component-version.ps1` updates component versions in `omp-components.json`.
  **Deprecated** — kept only for backward compatibility; it throws unless
  `-AllowDeprecated` is passed, and leaves `repositoryVersion`, module-definition versions, and
  `compatibleArtifacts.maxVersion` behind. Use the canonical
  `scripts/omp/bump-version.ps1` instead (see `docs/OMP_COMPONENT_MANIFEST.md`).
- `local-ci.ps1` is the local validation and pre-push gate;
  `local-ci-telemetry.ps1` supplies its best-effort, non-gate-breaking counters.
- `setup-hooks.ps1` activates the tracked Git hooks for the local gate.
- `bootstrap-secret-fields.ps1` defines the portable bootstrap secret fields;
  `protect-bootstrap-config-secrets.ps1` encrypts those values before they are
  stored in private installer profiles.
- `test-bump-version-github-findings.ps1` and
  `test-github-findings-source.ps1` are focused regression/source-check runners.
- `deployment/` contains HostAgent-first packaging and installer-runner tools.
- `dev/` contains developer-only helpers for module-definition SQL embedding
  and local Content smoke-test data. `seed-content-webapp-test-pages.ps1`
  prepares the four supported Content page types, while
  `test-content-webapp-local-install.ps1` verifies live HostAgent mirroring,
  stale-file deletion, installed Content file loading, and server-report SQL
  rendering. The latter uses the focused `ContentWebAppRuntimeProbe` project.
- `diagnostics/` contains read-only operational diagnostics, including
  HostAgent service checks and OMP web-app checks for logging, filesystem, IIS,
  HTTP, event logs, and OMP database access.
- `omp/` contains repository-object builders that turn `omp-components.json`
  entries into portable module, artifact, host config, config overlay, widget,
  and universal-package objects.
  OMP-compatible module repositories should expose a root
  `build-omp-objects.ps1` wrapper plus the standard `scripts/omp/` wrappers:
  `bump-version.ps1`, `bump-version.cmd`, `build-repository-objects.ps1`,
  `build-universal-package.ps1`, `build-universal-package.cmd`,
  `export-universal-package.ps1`, and `export-universal-package.cmd`.
  See `docs/OMP_COMPONENT_MANIFEST.md` for the conformance checklist and
  `scripts/omp/README.md` for the canonical object-builder behavior.
- `omp/push-with-rebump.ps1` pushes a version bump without losing a race to
  another machine: it fetches, rebases when `origin/main` moved, re-runs
  `bump-version.ps1` for the component set read from the commit's own diff,
  amends, and retries a bounded number of times. Prefer it over a hand-run
  fetch/rebase/bump/push loop in this repository.
- `omp/validate-component-versions.ps1` also fails the build when a component
  version moved against the base ref while `repositoryVersion` stayed put, since
  that value is the universal package's identity. See
  `docs/VERSIONING_AND_IDENTITIES.md`.

## Removed Legacy Scripts

The old script-first installer scripts and direct local runtime installer
scripts are retained outside this public repository. Do not use them for new
installs. Use the public `installer/` sample or a private universal installer
profile for new installs and upgrades.
