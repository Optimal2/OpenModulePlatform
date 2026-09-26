# Diagnostics Scripts

This folder contains operational diagnostics that are safe to run on an OMP host
without changing IIS, Windows service state, or SQL data.

## HostAgent

Run the administrator diagnostics from an elevated PowerShell session:

```powershell
.\Test-HostAgentAdminDiagnostics.ps1 `
  -ServiceName 'OMP.HostAgent' `
  -AppPath 'D:\OMP\Services\HostAgent' `
  -OutputPath '.\hostagent-admin.json'
```

Run the runtime-identity diagnostics from a PowerShell session started as the
same Windows account that runs the HostAgent service:

```powershell
.\Test-HostAgentRuntimeIdentityDiagnostics.ps1 `
  -ServiceName 'OMP.HostAgent' `
  -AppPath 'D:\OMP\Services\HostAgent' `
  -OutputPath '.\hostagent-runtime.json'
```

The runtime script does not execute a HostAgent cycle by default. Add `-RunOnce`
only when you intentionally want to run one cycle and potentially process files
already waiting in the configured import folder.

## OMP Web App

Run web-app diagnostics from an elevated PowerShell session on the target host.
The script is read-only and collects IIS, deployed-file, OMP database, log,
event-log, and optional HTTP probe data for one web application:

```powershell
.\Test-OmpWebAppDiagnostics.ps1 `
  -ServiceName 'OMP.HostAgent' `
  -HostAgentPath 'D:\OMP\Services\HostAgent' `
  -RoutePath 'my-web-app' `
  -AppInstanceKey 'my_web_app' `
  -Url 'https://example.invalid/my-web-app' `
  -OutputPath '.\omp-webapp-my-web-app.json'
```

Module-specific checks live outside this repository. Pass one or more scripts
with `-ModuleCheckScript`, or place them in a `module-checks` folder next to
the diagnostics script (the folder is gitignored). Each script is dot-sourced
with `-Config <effective appsettings>` after the shared checks, so it can call
`Add-Check`, `Get-ConfigValue`, `Invoke-SqlRows` and the other helpers:

```powershell
param([System.Collections.IDictionary]$Config)

if (-not $Config.Contains('ExampleModule')) { return }
$schema = [string](Get-ConfigValue -Config $Config -Path 'ExampleModule:DataSchema' -Default 'omp_example_module')
Add-Check 'Module compatibility' 'ExampleModule data schema' 'Info' "DataSchema=$schema"
```
