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

Developer checkouts can use the shorter runner layout below. This is the layout
used by the public sample installer and by private installation repositories:

```text
installer/
  OpenModulePlatform.Bootstrapper.exe
  data/                         # generated and ignored
  payload/                      # generated and ignored
  sql/                          # generated and ignored
  tools/                        # generated and ignored
hosts/
  <profile>/
    bootstrap.json
    package.psd1
    sql/
    host-configs/
    config-overlays/
```

`installer` is the runnable package root. `hosts/<profile>` is the source of
truth for machine-specific bootstrap settings and optional host-specific inputs.
Generated data is repopulated by the installer sync action on developer
machines and should not be committed.

The bootstrapper supports this package shape, and new package builds should
produce it for copied/zipped release packages:

```text
OpenModulePlatformHostAgentFirst-<version>/
  OpenModulePlatform.Bootstrapper.exe
  data/
    global/
      artifacts/
        omp_portal__omp_portal__web-app__omp-portal__<version>.zip
        content_webapp__content_webapp_webapp__web-app__content-webapp__<version>.zip
        opendocviewer__opendocviewer_webapp__web-app__opendocviewer__<version>.zip
        <other module artifact packages available for install/import>
      module-definitions/
        omp_core.module-definition.json
        omp_portal.module-definition.json
        <other module definitions available for install/import>
      sql/
        bootstrap-local.sql
    hosts/
      <profile>/
        sql/
        artifacts/
        files/
  payload/
    OpenModulePlatform.HostAgent.WindowsService.zip
  install-hostagent-first.cmd
  install-hostagent-first-console.cmd
  uninstall-hostagent-first.cmd
  uninstall-hostagent-first-clean.cmd
  uninstall-hostagent-first.ps1
  manifest.json
  tools/
    bootstrap-config-editor/
      index.html
    OpenModulePlatform.Bootstrapper/
      OpenModulePlatform.Bootstrapper.exe
```

The package has two data levels: `data/global` contains portable objects shared
by every host in the package, while `data/hosts/<profile>` contains only
bootstrap helper files that are unique to that host. Private universal
installer repositories should keep the canonical host profile outside generated
package content:

```text
hosts/<profile>/bootstrap.json
hosts/<profile>/package.psd1
hosts/<profile>/sql/
hosts/<profile>/host-configs/
hosts/<profile>/config-overlays/
```

The selected `bootstrap.json` file is the host profile; there is no separate
installation-instance layer in the package layout. Runtime differences that
belong to modules or artifacts are represented as host configuration and config
overlay objects rather than by duplicating global module or artifact packages.
Generated package folders such as `data` and `sql` are output caches and should
not be treated as the source of truth.

The package library has no separate `initial` and `available` folders. All
portable module definitions and artifact package objects live in
`data/global`; the selected bootstrap configuration decides which artifact
versions should be installed or selected immediately. Top-level `payload` is
reserved for the direct HostAgent service zip needed before HostAgent can deploy
itself from an artifact package.

The root bootstrapper is published separately with single-file settings so the
operator entry point stays as small and obvious as the .NET runtime allows. The
full bootstrapper publish output is still kept below `tools/` for scripted
console usage and troubleshooting.

The package can be zipped and copied as a single file, or generated as an
expanded folder by setting `Package.SkipZip = $true` or passing `-SkipZip`.
The expanded package is self-contained except for environment-specific values in
the bootstrap configuration files. The graphical installer reads
`hosts\<profile>\bootstrap.json` beside the package, and still supports
package-local `configs\*.json` for older layouts. It automatically selects the
one profile that matches the local computer name. The root-level
`bootstrap.local.sample.json` is kept only for command-line and older automation
compatibility.

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
  -ConfigPath .\scripts\deployment\hostagent-first.local.psd1 `
  -OutputRoot .\artifacts\hostagent-first
```

Folder-only local/private package example:

```powershell
.\scripts\deployment\package-hostagent-first.ps1 `
  -ConfigPath .\scripts\deployment\hostagent-first.local.psd1 `
  -OutputRoot .\artifacts\hostagent-first `
  -SkipZip
```

Neutral public package example:

```cmd
scripts\deployment\package-hostagent-first-public.cmd
```

This uses `scripts\deployment\hostagent-first.config.sample.psd1` and writes a
non-customer-specific package to `artifacts\hostagent-first-public`. Protected
customer packages should use `.local.psd1` files outside source control.

The script reads `omp-components.json` so each deployable component keeps its own
artifact version. The repository version can still be used as the package
version, but it is not forced onto every app artifact. Components with normal
OMP module/app metadata are packaged as manifest-based artifact package objects.
HostAgent is included twice by design: once as a direct installer payload for
the first manual service bootstrap, and once as a manifest-based `host-agent`
artifact package that later HostAgent self-upgrade can consume. Other compiled
runtime components, including WorkerManager and WorkerProcessHost, are packaged
only as OMP artifact packages with module/app metadata.

During bootstrap the installer also records the current `host-agent` artifact in
`omp.HostAgentDesiredStates` for the configured host. The configured
`HostAgent:ServiceName` is treated as the stable service-name prefix, so the
initial Windows service is created with the current artifact version appended
for example `OMP.HostAgent.0.3.35`. The initial runtime folder follows the same
versioned convention as later self-upgrades:
`HostAgent:ServicesRoot\HostAgent-<version>`, for example
`E:\OMP\Services\HostAgent-0.3.35`. Future HostAgent artifact imports can then
update the desired row. The running HostAgent provisions the new artifact,
creates the next side-by-side versioned service and folder, starts it in
takeover mode, and the new service removes the previous Windows service after it
has acquired the host lease.

OpenDocViewer is also packaged as a manifest-based OMP artifact package. The
deployable `dist` output is stored as the package payload. Site-specific
`odv.site.config.js` belongs in a host config overlay, not in the global
artifact package. Put protected overlay packages below
`hosts/<profile>/config-overlays` in a private installer repository or import
them later through Portal.

## Installing

1. Expand the package on the target server.
2. Put one or more environment-specific bootstrap JSON files in
   `hosts/<profile>/bootstrap.json` beside the package. Keeping profile names
   such as `linus`, `alfons`, `vgr-production-1825`, and `vgr-test` lets one
   package carry multiple installation profiles. For older disposable local
   environments, editing `configs\bootstrap.local.sample.json` in place is also
   acceptable.
   Profiles can include a `profile` section:

   ```json
   {
     "profile": {
       "displayName": "VGR Production - VGMS1825",
       "machineNames": [ "VGMS1825", "VGMS1825.vgregion.se" ]
     }
   }
   ```

   When the graphical installer starts, it must find exactly one profile whose
   `profile.machineNames`, `hostAgent.hostName`, or `hostAgent.hostKey` matches
   the local computer name. If no profile matches, or if more than one profile
   matches, the installer stops with instructions instead of falling back to a
   potentially wrong configuration. Use
   `tools\bootstrap-config-editor\index.html` to create or adjust a machine-
   specific config file.
3. Set at least:
   - `sql.server`
   - `sql.database`
   - `sql.bootstrapPortalAdminPrincipal`
   - `artifactStoreRoot`
   - `hostAgent.serviceName`
   - `hostAgent.serviceAccountName`, when HostAgent must run as a specific
     Windows account instead of LocalSystem
   - `security.portableEncryptionKey` plus encrypted password values when the
     config must carry service or IIS app-pool passwords
   - `hostAgent.installPath`
   - `hostAgent.hostKey`, when the environment should use a real host key
     instead of the neutral `sample-host` bootstrap default
   - `hostAgent.webAppsRoot`
   - `hostAgent.portalPhysicalPath`
   - `hostAgent.servicesRoot`
4. Double-click `OpenModulePlatform.Bootstrapper.exe` in the package root, or
   run:

```cmd
install-hostagent-first.cmd
```

Both entry points open the graphical installer. The EXE requests administrator
rights, loads `hosts\<profile>\bootstrap.json` beside the package (or legacy
`configs\*.json` files when present), locks onto the profile matching the local
computer, and shows common SQL, path, HostAgent, and IIS settings as read-only
values. Operational changes are made in the JSON file and then loaded with
`Reload config`.

The first visible action is intentionally the safe/common action:

- If no existing installation is detected from the configured HostAgent service,
  Portal path, or IIS site, the recommended action is `Install OpenModulePlatform`.
- If an existing installation is detected, the recommended action is
  `Upgrade existing installation`.

The initial bootstrap seeds the standard host roles `IISHost` and `ServiceHost`
alongside the default development host. Local single-machine installs assign the
same concrete host to both roles so role-targeted desired apps work immediately.

On machines where the matched config resolves valid source repositories, the
recommended action has a checked option to refresh package objects from source
before it runs. The refresh copies newer or missing module definitions and
artifact packages, and treats same-version/different-content packages as
something to fix before continuing. On production servers without source
repositories, source-dependent actions are disabled and hidden from the normal
path.

Advanced actions such as full bootstrap/reinstall, package-only refresh,
complete package rebuild, and uninstall are behind `Show other functions`.
They are still available for deliberate maintenance, but the default UI keeps
them out of the normal operator path.

The `Upgrade / complete` action is a package catch-up action for an existing
installation. It imports missing, newer, or changed module definition documents,
copies missing artifact folders, publishes missing package-library files, and
installs HostAgent only if no HostAgent service is present. It recognizes the
current `OMP.HostAgent` naming standard, older `OpenModulePlatform.HostAgent`
services, and HostAgent services whose executable is already using the target
HostAgent folder. If an older versioned HostAgent service is already present,
the bootstrapper leaves the runtime service alone and lets HostAgent
self-upgrade complete the version switch. The running desired HostAgent also
cleans up duplicate HostAgent services, including older service-name prefixes,
without deleting the active install directory. Existing artifact folders and an
existing HostAgent service are deliberately left unchanged; use
`Install or update` when a full bootstrap/reconfiguration pass is intended.

When imported module definitions include validation SQL, the bootstrapper runs
the read-only validation script first. Idempotent repair SQL runs only when the
validation reports an unhealthy state or the validation itself cannot complete.

On a development machine where the installer package still lives below an
OpenModulePlatform source checkout, the graphical installer can also compare
the package and installed database with the source repository manifest:

- `Check source objects` reads `omp-components.json`, the module definition
  files, the current package payload, and the target database. It reports
  module definitions or artifact entries whose versions or content differ from
  source. When an `OpenDocViewer` repository exists as a sibling of
  `OpenModulePlatform`, its component manifest is checked as well.
- `Create updated installer package` saves the current installer settings,
  starts a separate refresh process, rebuilds a fresh HostAgent-first package
  from source, replaces the current package folder after the installer exits,
  and starts the updated installer. This keeps the running EXE from locking the
  package files that need to be replaced.
- `Sync package objects` is the normal lightweight developer action. It fills
  the package object library from source manifests and selectively builds only
  missing .NET artifact packages. Use this before install/upgrade when a private
  developer package is intentionally minimal. It may update artifact targets in
  the running installer configuration so the current install/upgrade action uses
  the freshly synced versions, but it does not rewrite the tracked host profile
  files. Persisting host config changes is an explicit package refresh or manual
  config-editing step.

Private developer installer repositories can keep the committed package small:
the root `OpenModulePlatform.Bootstrapper.exe` plus host profiles below
`hosts/<profile>`. Generated package folders such as `data`, `payload`, `sql`,
and `tools` can be ignored in Git and repopulated locally by `Sync package
objects` before the main install or upgrade action. If that refresh option is
disabled or developer source roots are unavailable, the package must already
contain the required generated files.

When only installer code changed, update just the committed executable:

```powershell
.\scripts\deployment\update-installer-runner-only.ps1 `
  -PackageRoot "E:\Linus Dunkers\Documents\GitHub\DEV\OpenModulePlatform\Universal\installer"
```

This runner-only update does not rebuild or commit module definitions, artifact
zips, SQL payloads, manifests, helper tools, or package libraries. It is the
preferred DEV-repository path when the installer binary changed but the package
contents should remain generated on each developer machine.

Do not run `scripts/deployment/package-hostagent-first.ps1` directly with the
existing universal installer package as its output target. That script creates a
fresh package from one package config and can legitimately omit package-local
host configs that belong to a universal/private package. To refresh an existing
package from the command line, use the package-preserving wrapper instead:

```powershell
.\scripts\deployment\refresh-existing-hostagent-first-package.ps1 `
  -PackageRoot "E:\Linus Dunkers\Documents\GitHub\DEV\OpenModulePlatform\Universal\installer" `
  -ConfigName linus
```

The wrapper copies the package-local bootstrapper runner to a temporary folder
and lets the bootstrapper perform its normal refresh from outside the package
directory. This preserves host profiles, host-specific data, and private package
contents while still updating generated payloads and metadata. The GUI
button `Create updated installer package` uses the same detached-runner idea.

If the package has been copied outside the source tree, use the `Developer` tab
to point the installer at the source repository roots and, when needed, a
specific package `.psd1` config. The source-root field accepts semicolon-
separated paths. The list must include the `OpenModulePlatform` repository and
may include sibling module repositories such as `OpenDocViewer`, `IbsPackager`,
`iKrock2`, or `VajSkrivare` when their `omp-components.json` manifests should
be included in the source comparison. This developer workflow is intentionally
local; production updates should still use controlled artifact/module-definition
packages.

HostAgent-first packages keep one shared portable object library under
`data/global`:

- `module-definitions` contains module definitions that the bootstrapper can
  import and Portal can later present as package-library items.
- `artifacts` contains artifact package objects that the bootstrapper can copy
  into ArtifactStore and Portal can later import.
- `host-configs` contains host configuration JSON/zip objects.
- `config-overlays` contains host-specific config overlay JSON/zip objects.

During bootstrap these files are copied, without deleting older files, into
  `ArtifactStoreRoot\_available\module-definitions` and
  `ArtifactStoreRoot\_available\artifacts`,
  `ArtifactStoreRoot\_available\host-configs`, and
  `ArtifactStoreRoot\_available\config-overlays`. Portal reads those folders
  from the `ArtifactUpload` settings so admins can import modules and
  host-specific overlays after the base installation is complete. External
  module artifacts are copied here from
  `HostAgentFirst.AvailableArtifactArchiveRoots` and from
  `RuntimeRoot\ArtifactArchive`. The package builder also scans `artifacts`
  folders below the configured source repositories. Matching standard
  artifact-package file names are copied when found.

The same shape is used by repository-level object generation. Run
`build-omp-objects.ps1` from an OMP-related repository, or call
`scripts/omp/build-repository-objects.ps1` from the OpenModulePlatform
repository with `-RepositoryRoot`, and use the package's `data/global` folder as
`-OutputRoot` when the generated objects should be added to an installer
package. Host-specific values live in the selected
`hosts\<profile>\bootstrap.json` file and in generated host config/config
overlay objects, not in the global module definition or artifact package. If
the bootstrapper itself needs host-local helper files, place them below
`data/hosts/<profile>`.

The GUI action `Sync package objects` is the lightweight alternative to
`Create updated installer package`. It uses the same source manifest comparison
as `Check source objects`, then updates module-definition JSON files and copies
already-built standard artifact packages into the shared package library as
needed. When a required standard artifact package is
missing and the component manifest points at a single .NET project through
`projectPath`, the bootstrapper publishes only that project, wraps the publish
output as an OMP artifact package, and writes it to `RuntimeRoot\ArtifactArchive`
for reuse. This avoids a full package rebuild when one or two compiled
artifacts are missing. Non-.NET artifacts, such as externally built web bundles,
must still exist in the package, `ArtifactStoreRoot\_available`,
`RuntimeRoot\ArtifactArchive`, or a source repository `artifacts` folder.
For minimal developer packages it also restores the configured bootstrap SQL
files, including the package-local include tree, from the configured source
repositories and private package config folder. If the configured HostAgent
package is absent, the current run can use the synced standard HostAgent
artifact package directly.
The sync action writes a timestamped diagnostic log to the user's temp folder
(`omp-installer-sync-*.log`) and keeps tracked host config files unchanged.
The command-line installer can use the same behavior before a bootstrap or
upgrade with `--sync-package-objects-before-action`.

For non-interactive console installation, run:

```cmd
tools\OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.exe --config bootstrap.local.sample.json
```

When using a profile below `hosts`, pass the profile JSON and the package root
explicitly for console automation:

```cmd
OpenModulePlatform.Bootstrapper.exe --config ..\..\hosts\linus\bootstrap.json --payload-root .
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

Package builds also generate a package-local SQL entry point:

```text
sql/bootstrap-local.sql
```

That file uses SQLCMD-style `:r` includes. The bootstrapper expands those
includes and applies environment patches:

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
        'OpenModulePlatform.HostAgent',
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
            Payload = 'data\global\artifacts\ibs_packager__ibs_packager_web__web-app__ibs-packager-web__0.3.3.zip'
            Target = 'ibs-packager/web/0.3.3'
        }
    )
}
```

The package script copies host-specific bootstrap SQL files into the active
host data folder and adds those files as separate bootstrap SQL steps after the
neutral OMP initialization script. Module-definition source files normally live
at each module root and are listed in `omp-components.json`; the package script
copies module definitions into `data/global/module-definitions`. Additional
artifact files are copied into `data/global/artifacts` and listed in the
matching config file. Host configuration and config overlay objects are copied
into `data/global/host-configs` and `data/global/config-overlays`. The installer
executable is unchanged and only the selected host config differs between
environments.

Artifact source paths in a bootstrap config can also be written relative to the
selected data level, for example `artifacts/<package>.zip`. The bootstrapper
looks below `data/hosts/<config-file-name-without-extension>` first and then
falls back to `data/global`. Runtime configuration differences should normally
be captured as config overlay objects in the central library; host-local
artifact overrides should be reserved for bootstrap repair scenarios.

For example, a protected VGR package can set:

- `hostAgent.serviceName` to the actual service name used on the servers
- `hostAgent.additionalServiceNamesToRemove` when uninstall packages must remove
  older runtime services that are no longer discoverable from the current
  runtime folder
- `artifactStoreRoot` to the shared ArtifactStore UNC path
- `hostAgent.ensureIisSite`, `hostAgent.iisBinding*`, and app pool identity
  settings when HostAgent should create or repair the IIS site and app pools
- `HostAgent.IisAppPoolOverrides` when one web app must run under a dedicated
  IIS app-pool account. Override keys are matched against app instance key,
  route path, and final app-pool name; apps without a match
  keep using the package-level app-pool identity.
- `HostAgent:FileMirrors` in `hostAgent.appSettings` for shared Content files
- separate SQL/bootstrap JSON files for test and production if paths or host
  keys differ

## Password Handling

New HostAgent-first packages should not store clear-text passwords in either
bootstrap JSON or generated HostAgent appsettings.

The bootstrap JSON may contain portable encrypted values in fields such as
`hostAgent.serviceAccountPassword`, `hostAgent.iisAppPoolPassword`, and
`hostAgent.iisAppPoolOverrides.*.password`. Use
`tools\bootstrap-config-editor\index.html` and its `Encrypt password fields`
action to produce values in the `enc:aesgcm:v1:...` format. The portable key can
be stored as `security.portableEncryptionKey` or supplied through
`security.portableEncryptionKeyEnvironmentVariable`. This encryption is
package-portable by design so the installer can run on the target machine; keep
the key with the same care as the installer package.

During installation the bootstrapper decrypts those values only in memory,
writes them into `hostagent.credentials.json`, and protects that file with
Windows DPAPI using `HostAgent:CredentialStore:ProtectionScope`. Generated
HostAgent appsettings contain only credential keys such as
`HostAgent:IisAppPoolPasswordCredentialKey` and
`HostAgent:SelfUpgrade:ServiceAccountPasswordCredentialKey`. With the default
`LocalMachine` protection scope, the stored password cannot be moved to another
computer and decrypted there.

Only HostAgent should need a manual service bootstrap. Once HostAgent is running,
application versions should be changed through OMP artifacts and instance
templates.
