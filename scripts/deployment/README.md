# OpenModulePlatform Deployment Scripts

These scripts provide a configuration-driven install, uninstall, and package flow
for the public OpenModulePlatform components:

- OMP Portal and Auth
- OMP HostAgent, WorkerManager, and WorkerProcessHost
- OpenDocViewer static web app
- OMP example web, service, worker, Blazor, and iframe modules

Environment-specific values are intentionally kept out of Git. Copy
`omp-suite.config.sample.psd1` to `omp-suite.local.psd1` and adjust the local file
for the target machine or server.

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

## Uninstall

```powershell
.\uninstall-omp-suite.ps1
```

The uninstall script is deliberately config-driven. It removes only the IIS
objects, Windows services, and paths listed in the local config by default. It
does not drop the database, change database permissions, or remove database
objects unless `Options.RemoveDatabaseObjects = $true` is explicitly set in the
local configuration.

The install script expects the configured database to already exist by default.
It does not create the database or grant the run-as account database access
unless `Options.CreateDatabase = $true` or
`Options.GrantRunAsDatabaseAccess = $true` is explicitly set.

## Sensitive Data

Do not commit `*.local.psd1` files. They may contain service accounts, database
server names, passwords, customer paths, or admin principals.
