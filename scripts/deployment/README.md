# OpenModulePlatform Deployment Scripts

These scripts provide a configuration-driven install, uninstall, and package flow
for the public OpenModulePlatform components:

- OMP Portal and Auth
- OMP HostAgent, WorkerManager, and WorkerProcessHost
- OpenDocViewer static web app
- OMP Content Web App and iFrame Web App standard modules
- OMP example web, service, worker, and Blazor modules

Environment-specific values are intentionally kept out of Git. Copy
`omp-suite.config.sample.psd1` to `omp-suite.local.psd1` and adjust the local file
for the target machine or server. You can also keep several ignored
environment files, for example `omp-suite.dev.local.psd1` and
`omp-suite.prod.local.psd1`, and select one with `-ConfigPath`.

## Scripts

- `package-omp-suite.ps1` builds a release package from source repositories.
- `install-omp-suite.ps1` installs from source or from an expanded package,
  depending on `DeploymentMode` in the config.
- `uninstall-omp-suite.ps1` removes IIS apps/app pools, Windows services, files,
  and, only when explicitly enabled, configured database objects while leaving
  the database itself in place.
- `install-omp-suite.cmd` and `uninstall-omp-suite.cmd` are double-click
  wrappers for Windows servers. They run the matching PowerShell script without
  extra arguments and keep the console open so the operator can read errors.

## Typical Local Developer Flow

```powershell
cd <OpenModulePlatform repo>\scripts\deployment
Copy-Item .\omp-suite.config.sample.psd1 .\omp-suite.local.psd1
notepad .\omp-suite.local.psd1
.\install-omp-suite.ps1
```

Use `DeploymentMode = 'Source'` for development machines. The installer will
create a local package first and then deploy that package, so the same deployment
path is exercised locally and in packaged environments.

The configured database is expected to already exist unless
`Options.CreateDatabase = $true` is explicitly set.

`Options.StartServices` controls whether Windows services are started by the
installer. Services are still installed with Automatic startup. In
load-balanced/customer environments it is usually safer to keep this set to
`$false`, validate all nodes, and then start HostAgent/WorkerManager manually.

## Typical Test/Production Flow

Build the package on a build/developer machine:

```powershell
.\package-omp-suite.ps1 -ConfigPath .\omp-suite.local.psd1
```

Relative `Package.OutputRoot` values are resolved from the repository root, not
from the current PowerShell directory. This keeps elevated shells from writing
packages under `C:\Windows\System32`.

Copy `OpenModulePlatformSuite-<version>.zip` to the target server, extract it,
create an environment-specific `omp-suite.local.psd1` next to the installer, and
run:

```powershell
.\install-omp-suite.ps1
```

`DeploymentMode` is read from `omp-suite.local.psd1`, so the same no-argument
installer command works for source-based developer installs and package-based
test/production installs.

Protected/customer-specific packages can be made ready to run by setting
`Package.IncludeInstallConfig = $true` in the local config used for packaging.
The package script then copies that config into the package as
`omp-suite.local.psd1`, next to `install-omp-suite.ps1`. Only enable this for
private packages that are allowed to contain customer paths and credentials.

If OpenDocViewer needs customer-specific runtime files, set
`Package.OpenDocViewerPackageZip` to a prebuilt OpenDocViewer zip. The package
script then uses that zip as `payload\OpenDocViewer.dist.zip` instead of
zipping `OpenDocViewer\dist` directly. This is useful when the ODV package must
include a site-local `odv.site.config.js` and `help\site` manual content.

For HTTPS deployments, set `Iis.Protocol = 'https'` and either
`Iis.CertificateThumbprint` or `Iis.CertificateSerialNumber`. If different
servers use different certificates, add a `Hosts` entry per server and leave
`HostKey` empty. The installer resolves the current server from `COMPUTERNAME`
and applies that host's certificate settings.

`PublicBaseUrl` should contain the externally visible root URL. When a module
does not provide its own base URL, portal topbar links are generated from the
portal base URL because that is the normal OMP hosting layout.

When releasing a new suite version, update the version in the package config
used for the release. Keep the fallback defaults in `package-omp-suite.ps1`,
`install-omp-suite.ps1`, and `omp-suite.config.sample.psd1` aligned so source
installs, packaged installs, and the sample config report the same suite
version.

Use `ConfigSettings` for installation-scoped settings that should be inserted or
updated during SQL installation. The built-in branding settings are
`branding/platformName` and `branding/portalName`; they control visible UI text
only and do not rename technical identifiers, schemas, permissions, cookies, or
assemblies.

All OMP web apps that share authentication must use the same
`DataProtectionKeyPath`. In a load-balanced environment this must be a shared
folder reachable by every IIS node; otherwise cookies and antiforgery tokens
created by one node may fail with HTTP 400 when a later request reaches another
node. The IIS app-pool account on every node must be able to read and write this
folder.

When the portal or web modules are hosted behind a reverse proxy or load
balancer, set `Portal.UseForwardedHeaders = $true` and configure
`Portal.ForwardedHeadersKnownProxies` or
`Portal.ForwardedHeadersKnownNetworks` for the trusted proxy addresses. Use
`Portal.ForwardedHeadersTrustAllProxies = $true` only in an isolated deployment
where all requests are guaranteed to pass through the trusted proxy.

## Uninstall

```powershell
.\uninstall-omp-suite.ps1
```

Use `-ConfigPath` when uninstalling an environment that uses a named local
configuration file:

```powershell
.\uninstall-omp-suite.ps1 -ConfigPath .\omp-suite.prod.local.psd1
```

The uninstall script is deliberately config-driven. It removes only the IIS
objects, Windows services, and paths listed in the local config by default. It
does not drop the database, change database permissions, or remove database
objects unless `Options.RemoveDatabaseObjects = $true` is explicitly set in the
local configuration.

Temporary safety switches are available for partial cleanup: `-KeepFiles`,
`-KeepDatabaseObjects`, `-KeepIis`, and `-KeepServices`. These switches override
the removal options in the local config for that run only.

The install script expects the configured database to already exist by default.
It does not create the database or grant the run-as account database access
unless `Options.CreateDatabase = $true` or
`Options.GrantRunAsDatabaseAccess = $true` is explicitly set.

SQL scripts are executed through the .NET `System.Data.SqlClient` provider that
ships with Windows PowerShell 5. `sqlcmd` is not required on target servers.

## Sensitive Data

Do not commit `*.local.psd1` files. They may contain service accounts, database
server names, passwords, customer paths, or admin principals.
