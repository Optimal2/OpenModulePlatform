# First-install-only installer (OpenModulePlatform.Installer)

`OpenModulePlatform.Installer.exe` is a single-purpose installer for **new**
servers. It performs the first installation of a prepared host profile and
nothing else: no upgrade, repair, uninstall, package building or source
synchronization. For those, see [HOST_AGENT_FIRST_INSTALL.md](HOST_AGENT_FIRST_INSTALL.md)
and the Bootstrapper.

It is published self-contained single-file `win-x64` with compression, so it
starts on a server with no .NET installed, and its application manifest
requests administrator rights.

## Package layout

The executable runs from an installer package folder:

```
<package>\
  OpenModulePlatform.Installer.exe
  hosts\<profile>\bootstrap.json   one or more prepared host profiles
  prereqs\dotnet-hosting-<ver>-win.exe   the hosting bundle for the artifacts' runtime major
  data\global\...                  artifact payload (as delivered)
```

On start it discovers profiles with the same search rules as the Bootstrapper
and locks onto the one profile whose `profile.machineNames`,
`hostAgent.hostName` or `hostAgent.hostKey` matches the local computer name:

- **Exactly one match**: a confirmation card (profile, computer, environment,
  SQL server and database, module and artifact counts, install root).
- **No match**: "No installation is prepared for this computer (\<name\>)" with
  a contact-your-supplier hint. Other profiles are never listed, and sample
  configs (`*.sample.json`) never count.
- **Several matches**: an error naming them; remove duplicates first.

If the matched profile's HostAgent service already exists, or the install root
already contains a HostAgent, the installer stops: this program only performs
the first installation.

## What it checks and installs

The prerequisite panel shows one green/red line per requirement:

- Windows edition (Server uses `Install-WindowsFeature`, client Windows uses
  `dism.exe /online /enable-feature`).
- IIS web server role; plus Windows Authentication when the profile deploys the
  authentication application, and WebSocket Protocol when it deploys a Blazor
  Server application.
- ASP.NET Core shared runtime for the artifacts' exact runtime major (read from
  the payload's `*.runtimeconfig.json` files; .NET never rolls forward across
  major versions, so only the required major counts) and the
  AspNetCoreModuleV2 IIS registration.
- For an `https` binding with `iisBindingCertificateThumbprint` set: the
  certificate must exist in `LocalMachine\My` **with a private key**. Red
  otherwise, with a plain explanation - the installer never installs
  certificates, so this blocks Install until fixed.
- SQL connectivity with integrated security and that the configured database
  **exists** (the probe connects to `master` first, so "server unreachable" and
  "database does not exist" are reported distinctly). This installer never
  creates a database, regardless of `sql.createDatabase`; have the database
  administrator create it first.
- Free disk space on the install drive.
- The service account: domain and local accounts show a password box that must
  be verified (LogonUser) before Install is enabled; built-in accounts need no
  password. Passwords are used exactly as typed - never trimmed.

Install then, in order:

1. Verifies the service account password and grants the account the
   **Log on as a service** right (`SeServiceLogonRight` via LSA) — `sc.exe`
   does not grant it, and without it the service cannot start. A `.\name`
   account is machine-qualified (`MACHINE\name`) before the account lookup,
   because `LookupAccountName` does not resolve the `.\` shorthand.
2. Installs missing IIS features, then runs the hosting bundle from `prereqs\`
   (`/install /quiet /norestart`, or `/repair` when only the IIS module is
   missing) **after** IIS is in place, then `iisreset`. Exit codes 3010 and
   1641 (restart required) are reported plainly, not as failures, and the
   final summary notes that a restart is required. On Server,
   `Install-WindowsFeature`'s `Success`/`RestartNeeded` result is honoured the
   same way.
3. Runs the shared install chain from `OpenModulePlatform.Installation` in the
   Bootstrapper's bootstrap order (SQL scripts, module definitions, runtime
   database access, artifact preparation with the engine's antivirus-retry
   moves, registration, package library, HostAgent install) — minus database
   creation. Before the chain, the latest-available artifact selection
   (`InstallationEngine.SelectLatestAvailableArtifactPackages`) runs exactly
   where the Bootstrapper runs it, and its decisions are printed in the
   progress list and in `--dry-run`. When the profile's app-pool user equals
   the service account and no app-pool password is configured, the typed
   service account password is reused for the pools.
4. Waits for the HostAgent's first successful sync (active runtime state in
   the database) and probes the profile's portal URL — host header when set,
   otherwise the machine's DNS FQDN for `https` (certificate validation stays
   on, and the certificate is issued to the FQDN), `localhost` only for
   `http` — then shows the success summary or the exact failing step with the
   log path.

Every step is written to a timestamped log file next to the executable.
UI text is English or Swedish, picked from the OS display language.
Unexpected failures (UI thread, background exceptions, malformed JSON) are
caught and shown with the failing step and the log path; secrets are never
printed (the install request's `ToString` redacts the operator password).

## Dry run

```
OpenModulePlatform.Installer.exe --dry-run
OpenModulePlatform.Installer.exe --dry-run --machine-name SERVER01
```

`--dry-run` runs every check and prints what would be installed without
changing anything: no feature installs, no hosting bundle, no SQL writes, no
service or IIS changes. `--machine-name` (honoured only with `--dry-run`)
pretends the installer runs on another computer, so a prepared profile can be
validated before the server visit. When a red line the installer cannot fix
itself remains (SQL unreachable, the database missing, the https certificate
without a private key, ...), the dry run says plainly that the installation
WOULD BE BLOCKED. Exit codes: 0 = dry run completed and nothing blocks the
install, 2 = no or several matching profiles (or an existing installation),
3 = the install would be blocked by a check the installer cannot fix itself,
1 = an error.
