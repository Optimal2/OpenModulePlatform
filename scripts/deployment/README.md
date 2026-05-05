# OpenModulePlatform Deployment Scripts

These scripts provide a configuration-driven install, uninstall, and package flow
for the public OpenModulePlatform components:

- OMP Portal and Auth
- OMP HostAgent, WorkerManager, and WorkerProcessHost
- OpenDocViewer static web app
- OMP example web, service, worker, Blazor, and iframe modules

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

## Typical Test/Production Flow

Build the package on a build/developer machine:

```powershell
.\package-omp-suite.ps1 -ConfigPath .\omp-suite.local.psd1
```

Copy `OpenModulePlatformSuite-<version>.zip` to the target server, extract it,
create an environment-specific `omp-suite.local.psd1` next to the installer, and
run:

```powershell
.\install-omp-suite.ps1 -DeploymentMode Package
```

For HTTPS deployments, set `Iis.Protocol = 'https'` and either
`Iis.CertificateThumbprint` or `Iis.CertificateSerialNumber`. If different
servers use different certificates, add a `Hosts` entry per server and leave
`HostKey` empty. The installer resolves the current server from `COMPUTERNAME`
and applies that host's certificate settings.

`PublicBaseUrl` should contain the externally visible root URL. When a module
does not provide its own base URL, portal topbar links are generated from the
portal base URL because that is the normal OMP hosting layout.

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

## Sensitive Data

Do not commit `*.local.psd1` files. They may contain service accounts, database
server names, passwords, customer paths, or admin principals.
