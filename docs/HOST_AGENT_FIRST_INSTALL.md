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
  uninstall-hostagent-first.cmd
  uninstall-hostagent-first-clean.cmd
  uninstall-hostagent-first.ps1
  manifest.json
  payload/
    OpenModulePlatform.HostAgent.WindowsService.zip
    omp_portal__omp_portal__web-app__omp-portal__<version>.zip
    content_webapp__content_webapp_webapp__web-app__content-webapp__<version>.zip
    opendocviewer__opendocviewer_webapp__web-app__opendocviewer__<version>.zip
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

## Environment Configuration Model

Use one bootstrapper executable and one package layout for an environment type.
Values that differ between machines, developers, test, production, and customer
installations should live in configuration files or protected package payloads,
not in separate source-code branches or hand-edited installer scripts.

The public OMP repository should keep only neutral package generation and sample
configuration. Private installation repositories can carry:

- environment-specific bootstrap JSON files
- protected `.psd1` package-build configuration
- customer SQL extension files
- customer or machine-specific payload files
- local wrapper scripts that call public OMP helpers with private defaults

This keeps the OMP repository focused on platform code while still letting each
installation have its own service account, host keys, paths, artifact payloads,
and Content file mirror settings.

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

Neutral public package example:

```cmd
scripts\deployment\package-hostagent-first-public.cmd
```

This uses `scripts\deployment\omp-suite.config.sample.psd1` and writes a
non-customer-specific package to `artifacts\hostagent-first-public`. Protected
customer packages should use `.local.psd1` files outside source control.

The script reads `omp-components.json` so each deployable component keeps its own
artifact version. The repository version can still be used as the package
version, but it is not forced onto every app artifact. Components with normal
OMP module/app metadata are packaged as manifest-based artifact package objects.
HostAgent is included twice by design: once as a direct installer payload for
the first manual service bootstrap, and once as a manifest-based `host-agent`
artifact package that later HostAgent self-upgrade can consume. Bootstrap
infrastructure without module/app metadata remains as direct installer payload.

OpenDocViewer is also packaged as a manifest-based OMP artifact package. The
deployable `dist` output is stored as the package payload, while
`odv.site.config.js` is registered as an artifact configuration file. Set
`OpenDocViewer.SiteConfigPath` in the package config to include a site-specific
file; when it is left empty the package includes a neutral config file that can
be edited later through Portal.

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
SQL, path, HostAgent, and IIS settings, and then runs the selected action.

On a development machine where the installer package still lives below an
OpenModulePlatform source checkout, the graphical installer can also compare
the package and installed database with the source repository manifest:

- `Check source objects` reads `omp-components.json`, the module definition
  files, the current package payload, and the target database. It reports
  module definitions or artifact entries whose versions or content differ from
  source. When an `OpenDocViewer` repository exists as a sibling of
  `OpenModulePlatform`, its component manifest is checked as well.
- `Upgrade from source` runs the repository package builder, refreshes the
  current package's `payload` and `module-definitions` folders, saves the
  refreshed artifact list and SQL version overrides to the bootstrap JSON, and
  then runs the normal install/update action.

If the package has been copied outside the source tree, use the `Developer` tab
to point the installer at the source repository root and, when needed, a
specific package `.psd1` config. This developer workflow is intentionally local;
production updates should still use controlled artifact/module-definition
packages.

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

## Uninstalling

The graphical installer also includes uninstall actions:

- `Uninstall runtime` removes the HostAgent service, runtime services whose
  executables live below the configured services root, the configured IIS site,
  and IIS app pools that use the configured app-pool prefix. Runtime files and
  database objects are kept.
- `Clean uninstall` performs the runtime uninstall and also removes configured
  runtime folders such as Portal, WebApps, Services, ArtifactStore,
  ArtifactCache, DataProtectionKeys, and artifact import folders.
- `Full uninstall` performs the clean uninstall and removes all user objects
  from the configured SQL database. The database itself is never dropped.

Folder packages still include script helpers for non-GUI runtime cleanup:

```text
uninstall-hostagent-first.cmd
uninstall-hostagent-first-clean.cmd
```

Those helpers do not remove SQL Server databases or database objects. Use the
GUI full-uninstall action when database objects should be removed as part of the
same operator-confirmed uninstall.

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

## Content File Mirroring

Content pages can be backed by HTML files and server-report JSON files. In
HostAgent-managed installations those files should usually live outside the
immutable web-app artifact and be mirrored into the local Content web app
runtime folder.

Use these `ContentWebApp` settings in the deployment config when a shared source
folder should be mirrored to the local IIS runtime:

```powershell
ContentWebApp = @{
    ServerReportsPath = 'App_Data/ContentReports'
    HtmlFilesPath = 'App_Data/ContentPages'
    SharedServerReportsPath = 'D:\Shared\OMP\Data\ContentReports'
    SharedHtmlFilesPath = 'D:\Shared\OMP\Data\ContentPages'
}
```

`package-hostagent-first.ps1` converts the two shared paths into
`HostAgent:FileMirrors` in the generated bootstrap JSON. HostAgent then mirrors:

```text
SharedServerReportsPath -> <WebAppsRoot>\<ContentWebAppPath>\App_Data\ContentReports
SharedHtmlFilesPath     -> <WebAppsRoot>\<ContentWebAppPath>\App_Data\ContentPages
```

For a single-machine development install, the shared paths can simply be two
folders on the same disk as the runtime. For a multi-server install, point the
shared paths at the common file share and keep the target paths local on each
web server. The Content app reads the local target paths, so both cases exercise
the same runtime behavior.

The Content module setup SQL intentionally creates no sample pages. For local
smoke testing, use `scripts/dev/seed-content-webapp-test-pages.ps1`; it creates
one page of each supported type and only queries OMP-owned tables.

Customer-specific packages should keep customer-specific SQL values outside the
public repository and inject them into the package configuration or generated
SQL during protected package creation.

## VGR And Other Customer Packages

The public repository contains only neutral local bootstrap behavior. Customer
packages should generate their own protected `bootstrap.<environment>.json` and,
when needed, customer SQL files that seed hosts, templates, shared content
mirrors, service names, and paths for that environment.

`package-hostagent-first.ps1` can include protected customer SQL files and
module-definition JSON files from the deployment config without committing them
to the public repository:

```powershell
HostAgentFirst = @{
    # Optional cleanup list for legacy Windows services whose executable paths
    # no longer live below the configured HostAgent/Services roots.
    AdditionalServiceNamesToRemove = @(
        'OpenModulePlatform.WorkerManager',
        'OpenModulePlatform.Service.ExampleServiceAppModule'
    )

    AdditionalSqlFiles = @(
        'vgr-test-bootstrap.sql',
        @{ Source = 'vgr-prod-bootstrap.sql'; Destination = 'Customer\vgr-prod-bootstrap.sql' }
    )

    AdditionalModuleDefinitionFiles = @(
        '..\IbsPackager\ibs_packager.module-definition.json',
        '..\iKrock2\ikrock.module-definition.json',
        '..\VajSkrivare\vajskrivare.module-definition.json'
    )

    AdditionalArtifactFiles = @(
        @{
            Source = '..\IbsPackager\artifacts\archive\ibs_packager__ibs_packager_web__web-app__ibs-packager-web__0.3.3.zip'
            Payload = 'payload\ibs_packager__ibs_packager_web__web-app__ibs-packager-web__0.3.3.zip'
            Target = 'ibs-packager/web/0.3.3'
        }
    )
}
```

The package script copies those files into `sql/Customer` by default and appends
the SQL files after the neutral OMP initialization scripts in
`sql/bootstrap-local.sql`. Module-definition source files normally live at each
module root and are listed in `omp-components.json`; the package script copies
them into the package-local `module-definitions` import folder and imports them
after SQL initialization so artifact import can validate versions against the
applied definitions. Additional artifact files are copied into the same package
payload and listed in `bootstrap.local.sample.json`; the installer executable is
unchanged and only the payload/config differs between environments.

For example, a protected VGR package can set:

- `hostAgent.serviceName` to the actual service name used on the servers
- `hostAgent.additionalServiceNamesToRemove` when uninstall packages must remove
  older runtime services that are no longer discoverable from the current
  runtime folder
- `artifactStoreRoot` to the shared ArtifactStore UNC path
- `hostAgent.ensureIisSite`, `hostAgent.iisBinding*`, and app pool identity
  settings when HostAgent should create or repair the IIS site and app pools
- `HostAgent:FileMirrors` in `hostAgent.appSettings` for shared Content files
- separate SQL/bootstrap JSON files for test and production if paths or host
  keys differ

Only HostAgent should need a manual service bootstrap. Once HostAgent is running,
application versions should be changed through OMP artifacts and instance
templates.
