# OpenModulePlatform Deployment Scripts

The supported deployment flow is the HostAgent-first installer:

- `package-hostagent-first.ps1` builds the installer package.
- `update-installer-runner-only.ps1` updates only the root
  `OpenModulePlatform.Bootstrapper.exe` in an existing package. Use this for
  private developer installer packages when installer code changed but the
  package object library should stay generated/ignored.
- `refresh-existing-hostagent-first-package.ps1` refreshes an existing package
  through a temporary runner so package-local legacy `configs` and host data are
  not deleted by a fresh one-profile package build.
- `package-hostagent-first-public.cmd` builds a neutral public sample package.
- `new-omp-artifact-package.ps1` wraps a deployable payload and optional
  runtime configuration files as a standard OMP artifact package object.
- `hostagent-first.config.sample.psd1` is the neutral package-build config.
  Copy it to `hostagent-first.local.psd1` for local source-package experiments
  that should not be committed.

The current package layout keeps shared portable objects in:

```text
data/global/module-definitions
data/global/artifacts
data/global/host-configs
data/global/config-overlays
```

Generated host-specific bootstrap helper files belong under the matching host
profile:

```text
data/hosts/<config-file-name-without-extension>/sql
data/hosts/<config-file-name-without-extension>/artifacts
data/hosts/<config-file-name-without-extension>/files
```

Private universal installer repositories should keep canonical host input in
`hosts/<profile>/bootstrap.json` beside the generated package, with optional
`package.psd1`, `sql`, `host-configs`, and `config-overlays` under the same
profile folder. Older packages may still keep bootstrap JSON files in
package-local `configs`.

The selected bootstrap JSON decides which global artifact packages should be
installed immediately and which host-specific SQL files should run for the
selected machine. There is no separate `initial`/`available` folder split in
the package; everything in `data/global` is the package library. Host-specific
runtime differences should be expressed as host configuration and config overlay
objects, not as duplicated module or artifact packages.

When a bootstrap config references `artifacts/<package>.zip`, the bootstrapper
checks the selected host folder first and then falls back to `data/global`. Use
host-local artifacts only for bootstrap repair scenarios; normal runtime
configuration belongs in config overlays.

Older `*-omp-suite*` scripts have been moved out of this public repository to
the private DEV archive under
`OpenModulePlatform/OLD/2026-05-24-public-repo-cleanup/tracked-legacy`. Do not
use them for new local, test, production, or customer installs.

Do not point `package-hostagent-first.ps1 -OutputRoot` at an existing universal
package that contains multiple host configs. Build into a separate output folder
for a new package, or use `refresh-existing-hostagent-first-package.ps1` when
the intention is to update a package in place.

Private developer installer repositories can keep the package intentionally
minimal in Git: the package root `OpenModulePlatform.Bootstrapper.exe` plus
host profiles outside generated package content. The package object library
folders (`data`, `payload`, `sql`, `tools`) can be ignored and regenerated from
developer source roots by the installer's package sync action before
install/upgrade. For that model, commit runner-only updates with
`update-installer-runner-only.ps1`, then let each developer machine regenerate
the ignored package contents locally. The sync action updates the running
installer's in-memory artifact targets for the current install/upgrade action,
but it does not rewrite tracked host config files.

See `docs/HOST_AGENT_FIRST_INSTALL.md` and
`docs/PORTABLE_DEPLOYMENT_OBJECTS.md` for the full model.
