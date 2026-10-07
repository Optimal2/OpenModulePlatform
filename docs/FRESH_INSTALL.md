# Fresh-install package (build-fresh-install-package.ps1)

`scripts/deployment/build-fresh-install-package.ps1` builds the deliverable for
a **new** server: one folder and one zip that the server operator copies to the
machine and runs, with no other tooling. The installer inside is the
first-install-only `OpenModulePlatform.Installer.exe` — see
[FIRST_INSTALLER.md](FIRST_INSTALLER.md) for what it checks and does. For the
developer-side HostAgent-first package, see
[HOST_AGENT_FIRST_INSTALL.md](HOST_AGENT_FIRST_INSTALL.md).

## Inputs

- `-ProfilePath` — a folder anywhere on disk holding the prepared host
  profile's `bootstrap.json`. The profile must name the target server
  (`profile.machineNames`, `hostAgent.hostName` or `hostAgent.hostKey`).
  Sample profiles (`*.sample.json`) are refused: this package always targets a
  real, prepared profile. Set `profile.displayName` to a human-readable name
  (for example the server role); without it the installer labels the profile
  after the file name, i.e. "bootstrap".
- `-PayloadSourceRoot` — the gathered artifact/payload source, i.e. what
  `package-hostagent-first.ps1` produces: a folder with
  `data\global\{artifacts,module-definitions,sql,...}` and `payload\`.
  Only what the profile references (enabled artifact sources, the HostAgent
  package) is copied, plus the shared `data\global` trees the install chain
  reads. Artifact versions therefore match the profile exactly; if the package
  ever carries several versions of one artifact, the installer's
  latest-available selection (the same `SelectLatestAvailableArtifactPackages`
  call the Bootstrapper makes) picks the newest at install time.
- `-InstallerExePath` (optional) — a prebuilt installer exe. Without it the
  installer is published from this repository, self-contained single-file
  win-x64, so it starts on a server with no .NET installed.
- `-HostingBundlePath` (optional) — a pre-downloaded
  `dotnet-hosting-<version>-win.exe`. Without it the bundle is downloaded from
  Microsoft's official release metadata
  (`builds.dotnet.microsoft.com/dotnet/release-metadata/<major>.0/releases.json`)
  into a local cache (`%LOCALAPPDATA%\OpenModulePlatform\installer-prereqs`,
  overridable with `-CacheRoot`) and verified against Microsoft's published
  SHA-512 before packaging. The bundle is large and is **never committed** —
  it exists only in the cache and the output folder.
- `-RuntimeMajor` (optional) — the ASP.NET Core runtime major the artifacts
  need. Default: parsed from this repository's `Directory.Build.props`
  (`netX.0`), falling back to 10. The hosting bundle must match this major
  exactly; .NET does not roll forward across majors.
- `-OutputRoot` (default `artifacts\fresh-install`), `-PackageName` (default
  `OpenModulePlatformFreshInstall-<profile folder name>`), `-SkipZip`,
  `-CodeSigningConfigPath` (Azure Trusted Signing for the installer exe;
  a no-op when unconfigured, like `package-hostagent-first.ps1`).

## Package layout

```
OpenModulePlatformFreshInstall-<profile>\
  OpenModulePlatform.Installer.exe    self-contained single-file win-x64, requires admin
  README.txt                          operator instructions (Swedish + English)
  hosts\<profile>\bootstrap.json      the one prepared profile
  prereqs\dotnet-hosting-<ver>-win.exe
  payload\...                         HostAgent package the profile references
  data\global\artifacts\*.zip         the artifact packages the profile references
  data\global\module-definitions\     module definitions (+ embedded/referenced SQL)
  data\global\sql\                    global SQL scripts the profile references
  data\hosts\bootstrap\sql\           profile-local SQL (when the profile folder has sql\)
  data\global\host-configs\, config-overlays\   when present in the payload source
```

The builder ends with a summary: profile, machine names, payload file count,
hosting bundle version and SHA-512, and the package sizes.

## Operator flow (also in the package README.txt)

1. Copy the folder to the server.
2. Run `OpenModulePlatform.Installer.exe --dry-run` as administrator. Fix any
   red lines the installer cannot fix itself and repeat until green.
   `--dry-run --machine-name <name>` validates the package from another
   machine before the server visit.
3. Run `OpenModulePlatform.Installer.exe` as administrator and follow the
   dialog.

The installer never creates the database; it must exist already. The installer
never upgrades or repairs; it refuses a machine where the HostAgent already
exists.
