# OpenModulePlatform Sample Installer

This folder is the public repository counterpart to the private universal
installer layout:

```text
installer/
  OpenModulePlatform.Bootstrapper.exe   # generated locally, ignored by Git
  data/
    global/
      module-definitions/                # generated locally, ignored by Git
      artifacts/                         # generated locally, ignored by Git
      host-configs/                      # generated locally, ignored by Git
      config-overlays/                   # generated locally, ignored by Git
      widgets/                           # generated locally, ignored by Git
  hosts/
    sample/
      bootstrap.json
      sql/
      host-configs/
      config-overlays/
      widgets/
```

The committed `hosts/sample/bootstrap.json` is a neutral demo profile. Before
starting the GUI, copy it to a machine-specific folder or change
`profile.machineNames` so it matches the local computer name.

To create the local installer runner:

```powershell
.\scripts\deployment\update-installer-runner-only.ps1 -PackageRoot .\installer
```

Then start:

```powershell
.\installer\OpenModulePlatform.Bootstrapper.exe
```

Keep `Refresh installer package from source first` enabled for a sample
installation. The installer will build the ignored `data`, `payload`, `sql`,
and tool folders from this repository before the install action starts.

Use `Refresh object archive` when you only want to update the installer's local
object archive from source repositories. Use `Create universal package` to build
an `omp-universal-package.json` based zip from selected objects in the archive.
The package builder can target any available host profile, not only the computer
currently running the GUI, and can combine global objects with host-specific
`host-configs`, `config-overlays`, and `widgets`. A package built without a
target host is global and host-agnostic: it never includes `host-configs` or
`config-overlays` (those objects always carry a host key and are per-host by
definition).

Runtime configuration files such as `appsettings*.json` and
`odv.site.config.js` must stay outside artifact payloads. They are represented
as artifact configuration-file rows or config overlays so configuration changes
do not change artifact hashes.
