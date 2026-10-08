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
  Sample profiles are refused — a `*.sample.json` anywhere in the folder, or
  the sample profile folder itself (a folder named `sample`): this package
  always targets a real, prepared profile. Set `profile.displayName` to a
  human-readable name (for example the server role); without it the installer
  labels the profile after the file name, i.e. "bootstrap".
  Clear-text passwords are refused: `hostAgent.serviceAccountPassword`,
  `hostAgent.iisAppPoolPassword`, `hostAgent.serviceAppPassword` and
  `sql.password` in `bootstrap.json`, plus every `*Password` field in an
  optional `package.psd1` next to it, must be empty or hold an
  `enc:aesgcm:v1:` value (`scripts/protect-bootstrap-config-secrets.ps1`
  produces them). A non-empty `security.portableEncryptionKey` is allowed but
  prints a warning: the package then carries a secret and must be guarded
  like a password (and never committed).
- `-PayloadSourceRoot` — the gathered artifact/payload source, i.e. what
  `package-hostagent-first.ps1` produces: a folder with
  `data\global\{artifacts,module-definitions,sql,...}` and `payload\`.
  Only what the profile references (enabled artifact sources, the HostAgent
  package) is copied, plus the shared `data\global` trees the install chain
  reads. Every reference must be a relative path that stays inside the
  payload source root — absolute paths and `..` segments are refused, so the
  builder never reads outside the payload source or writes outside the
  package folder. Artifact versions therefore match the profile exactly; if
  the package ever carries several versions of one artifact, the installer's
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
  SHA-512 before packaging. The download URL from the metadata is restricted
  to Microsoft's official hosts (`builds.dotnet.microsoft.com`,
  `download.visualstudio.microsoft.com`, `dotnetcli.azureedge.net`) over
  https; anything else is refused. The bundle is large and is **never
  committed** — it exists only in the cache and the output folder.
- `-RuntimeMajor` (optional) — the ASP.NET Core runtime major the artifacts
  need. Default (0): parsed from the `TargetFramework` in
  `OpenModulePlatform.Installer\OpenModulePlatform.Installer.csproj`
  (`netX.0-windows`; `Directory.Build.props` carries no TargetFramework).
  Detection fails loudly when the csproj is missing or has no TargetFramework
  — a silent fallback could package the wrong hosting bundle. The hosting
  bundle must match this major exactly; .NET does not roll forward across
  majors.
- `-OutputRoot` (default `artifacts\fresh-install`), `-PackageName` (default
  `OpenModulePlatformFreshInstall-<profile folder name>`; must be a plain
  folder name directly under `-OutputRoot` — `..`, rooted paths, path
  separators and invalid folder-name characters are refused, so the builder
  can never delete or write outside the package folder it creates),
  `-SkipZip`, `-CodeSigningConfigPath` (Azure Trusted Signing for the
  installer exe; a no-op when unconfigured, like
  `package-hostagent-first.ps1`).

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

`--dry-run` exit codes (the full table is in
[FIRST_INSTALLER.md](FIRST_INSTALLER.md)):

| Code | Meaning |
| --- | --- |
| 0 | Dry run completed and nothing blocks the install |
| 1 | An error |
| 2 | No or several matching profiles |
| 3 | The install would be blocked by a check the installer cannot fix itself |
| 4 | An installation already exists on this computer (information line; the checks still ran in full) |

The installer never creates the database; it must exist already. The installer
never upgrades or repairs; it refuses a machine where the HostAgent already
exists.
