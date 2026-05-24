# Scripts

This folder contains only reusable development and packaging helpers that are
still part of the current OMP object/installer model.

## Current Scripts

- `bump-component-version.ps1` updates versions in `omp-components.json`.
- `protect-bootstrap-config-secrets.ps1` encrypts portable bootstrap JSON
  secrets before they are stored in private installer profiles.
- `deployment/` contains HostAgent-first packaging and installer-runner tools.
- `dev/` contains developer-only helpers for module-definition SQL embedding
  and local Content smoke-test data.
- `omp/` contains repository-object builders that turn `omp-components.json`
  entries into portable module, artifact, host config, and config overlay
  objects.

## Removed Legacy Scripts

The old script-first installer scripts and direct local runtime installer
scripts were moved to the private DEV repository under:

```text
OpenModulePlatform/OLD/2026-05-24-public-repo-cleanup/tracked-legacy/scripts
```

Use the public `installer/` sample or a private universal installer profile for
new installs and upgrades.
