# OpenModulePlatform Deployment Scripts

The supported deployment flow is the HostAgent-first installer:

- `package-hostagent-first.ps1` builds the installer package.
- `package-hostagent-first-public.cmd` builds a neutral public sample package.
- `new-omp-artifact-package.ps1` wraps a deployable payload and optional
  runtime configuration files as a standard OMP artifact package object.

The current package layout keeps shared portable objects in:

```text
data/global/module-definitions
data/global/artifacts
data/global/sql
```

Host-specific additions belong under the matching bootstrap profile:

```text
data/profiles/<config-file-name>/sql
data/profiles/<config-file-name>/artifacts
data/profiles/<config-file-name>/files
```

The bootstrap JSON in `configs` decides which global artifact packages should
be installed immediately and which host-specific SQL files should run for the
selected machine. There is no separate `initial`/`available` folder split in
the package; everything in `data/global` is the package library.

When a bootstrap config references `artifacts/<package>.zip`, the bootstrapper
checks the selected profile folder first and then falls back to `data/global`.
Use profile artifacts only for environment-specific outer packages, such as
different runtime configuration files around the same binary payload.

Older `*-omp-suite*` scripts are retained only as migration references for
pre-HostAgent-first packages. Do not use them for new local, test, production,
or customer installs.

See `docs/HOST_AGENT_FIRST_INSTALL.md` and
`docs/PORTABLE_DEPLOYMENT_OBJECTS.md` for the full model.
