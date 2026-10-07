# OpenModulePlatform.Installer

First-install-only WinForms installer for new servers. See
[docs/FIRST_INSTALLER.md](../docs/FIRST_INSTALLER.md) for the operator-facing
behaviour, package layout and dry-run usage.

- One window: profile confirmation card, prerequisite panel, install progress.
- Drives the shared `OpenModulePlatform.Installation` engine; never creates the
  database; no upgrade/repair/uninstall.
- Published self-contained single-file `win-x64` with compression; the app
  manifest requires administrator rights.
- `--dry-run` (with optional `--machine-name`) runs every check without
  mutating anything.
- Tests live in `OpenModulePlatform.Installer.Tests`.
