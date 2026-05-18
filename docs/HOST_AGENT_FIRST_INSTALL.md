# HostAgent-First Installation Package

The HostAgent-first package is the preferred installation model for new OMP
environments. The package performs only the bootstrap work that cannot be done
by HostAgent itself:

1. Run the initial SQL setup and initialization scripts.
2. Prepare the central `ArtifactStore` with the package's component artifacts.
3. Install or update the HostAgent Windows service.
4. Start HostAgent so it can materialize templates, create/update the IIS site
   and app pools when configured, and deploy web apps, service apps, workers,
   and runtime configuration files from OMP metadata.

This keeps normal application deployment in the database/template model instead
of in one-off PowerShell installation logic.

## Package Layout

`scripts/deployment/package-hostagent-first.ps1` creates a package with this
shape:

```text
OpenModulePlatformHostAgentFirst-<version>/
  OpenModulePlatform.Bootstrapper.exe
  bootstrap.local.sample.json
  install-hostagent-first.cmd
  install-hostagent-first-console.cmd
  manifest.json
  payload/
    OpenModulePlatform.Portal.zip
    OpenModulePlatform.HostAgent.WindowsService.zip
    ...
  sql/
    bootstrap-local.sql
    OpenModulePlatform/
    OpenModulePlatform.Portal/
    ...
  tools/
    OpenModulePlatform.Bootstrapper/
      OpenModulePlatform.Bootstrapper.exe
```

The root bootstrapper is published separately with single-file settings so the
operator entry point stays as small and obvious as the .NET runtime allows. The
full bootstrapper publish output is still kept below `tools/` for scripted
console usage and troubleshooting.

The package can be zipped and copied as a single file, or generated as an
expanded folder by setting `Package.SkipZip = $true` or passing `-SkipZip`.
The expanded package is self-contained except for environment-specific values in
`bootstrap.local.sample.json`.

## Building A Package

Local development example:

```powershell
.\scripts\deployment\package-hostagent-first.ps1 `
  -ConfigPath .\scripts\deployment\omp-suite.local.psd1 `
  -OutputRoot .\artifacts\hostagent-first
```

Folder-only local/private package example:

```powershell
.\scripts\deployment\package-hostagent-first.ps1 `
  -ConfigPath .\scripts\deployment\omp-suite.local.psd1 `
  -OutputRoot .\artifacts\hostagent-first `
  -SkipZip
```

The script reads `omp-components.json` so each deployable component keeps its own
artifact version. The repository version can still be used as the package
version, but it is not forced onto every app artifact.

## Installing

1. Expand the package on the target server.
2. Copy `bootstrap.local.sample.json` to an environment-specific JSON file or
   edit it in place for a disposable local environment.
3. Set at least:
   - `sql.server`
   - `sql.database`
   - `sql.bootstrapPortalAdminPrincipal`
   - `artifactStoreRoot`
   - `hostAgent.serviceName`
   - `hostAgent.serviceAccountName`, when HostAgent must run as a specific
     Windows account instead of LocalSystem
   - `hostAgent.installPath`
   - `hostAgent.webAppsRoot`
   - `hostAgent.portalPhysicalPath`
   - `hostAgent.servicesRoot`
4. Double-click `OpenModulePlatform.Bootstrapper.exe` in the package root, or
   run:

```cmd
install-hostagent-first.cmd
```

Both entry points open the graphical installer. The EXE requests administrator
rights, loads the package defaults, lets the operator review or change common
SQL, path, HostAgent, and IIS settings, and then runs the same bootstrap steps.

For non-interactive console installation, run:

```cmd
tools\OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.exe --config bootstrap.local.sample.json
```

Use `--yes` only for controlled automated runs:

```cmd
tools\OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.exe --config bootstrap.local.sample.json --yes
```

The bootstrapper intentionally prompts with `[Y/N, default N]` when `--yes` is
not supplied.

## SQL Files

The public local SQL entry point is:

```text
sql/bootstrap-hostagent-first-local.sql
```

Package builds also generate a package-local SQL entry point:

```text
sql/bootstrap-local.sql
```

Both files use SQLCMD-style `:r` includes. The bootstrapper expands those
includes and applies the same environment patches that the older suite installer
applied:

- replace `USE [OpenModulePlatform]` with the configured database name
- inject the bootstrap portal admin principal
- inject component artifact versions from the package manifest

The neutral bootstrap registers the shared Auth app as the `/auth` web
application. Auth is not shown as a portal module entry, but it is deliberately
modeled as a normal web-app artifact so HostAgent can create the IIS child
application and keep its runtime configuration in
`omp.ArtifactConfigurationFiles` like other deployable apps.

Customer-specific packages should keep customer-specific SQL values outside the
public repository and inject them into the package configuration or generated
SQL during protected package creation.

## VGR And Other Customer Packages

The public repository contains only neutral local bootstrap behavior. Customer
packages should generate their own protected `bootstrap.<environment>.json` and,
when needed, customer SQL files that seed hosts, templates, shared content
mirrors, service names, and paths for that environment.

`package-hostagent-first.ps1` can include protected customer SQL files from the
deployment config without committing them to the public repository:

```powershell
HostAgentFirst = @{
    AdditionalSqlFiles = @(
        'vgr-test-bootstrap.sql',
        @{ Source = 'vgr-prod-bootstrap.sql'; Destination = 'Customer\vgr-prod-bootstrap.sql' }
    )
}
```

The package script copies those files into `sql/Customer` by default and appends
them after the neutral OMP initialization scripts in `sql/bootstrap-local.sql`.

For example, a protected VGR package can set:

- `hostAgent.serviceName` to the actual service name used on the servers
- `artifactStoreRoot` to the shared ArtifactStore UNC path
- `hostAgent.ensureIisSite`, `hostAgent.iisBinding*`, and app pool identity
  settings when HostAgent should create or repair the IIS site and app pools
- `HostAgent:FileMirrors` in `hostAgent.appSettings` for shared Content files
- separate SQL/bootstrap JSON files for test and production if paths or host
  keys differ

Only HostAgent should need a manual service bootstrap. Once HostAgent is running,
application versions should be changed through OMP artifacts and instance
templates.
