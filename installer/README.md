# OpenModulePlatform Sample Installer

This folder is the public repository counterpart to the private universal
installer layout:

```text
installer/
  OpenModulePlatform.Bootstrapper.exe   # generated locally, ignored by Git
  hosts/
    sample/
      bootstrap.json
      sql/
      host-configs/
      config-overlays/
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

Runtime configuration files such as `appsettings*.json` and
`odv.site.config.js` must stay outside artifact payloads. They are represented
as artifact configuration-file rows or config overlays so configuration changes
do not change artifact hashes.
