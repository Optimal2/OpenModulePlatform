# OMP publish helper

This helper publishes the currently publishable OMP projects and skips the shared library projects.

## Included projects

- OpenModulePlatform.Portal
- OpenModulePlatform.Service.ExampleServiceAppModule
- OpenModulePlatform.Web.ExampleServiceAppModule
- OpenModulePlatform.Web.ExampleWebAppBlazorModule
- OpenModulePlatform.Web.ExampleWebAppModule
- OpenModulePlatform.Web.ExampleWorkerAppModule
- OpenModulePlatform.Worker.ExampleWorkerAppModule
- OpenModulePlatform.HostAgent.WindowsService
- OpenModulePlatform.WorkerManager.WindowsService
- OpenModulePlatform.WorkerProcessHost

Excluded on purpose:

- OpenModulePlatform.Web.Shared
- OpenModulePlatform.Worker.Abstractions

## What changed in this fixed version

- Native `dotnet` commands are now executed with `$PSNativeCommandUseErrorActionPreference = $false` inside a local script block.
- Restore and publish output are captured into log files instead of surfacing as `NativeCommandError` from stderr.
- The script auto-detects `OpenModulePlatform.slnx` or `OpenModulePlatform.sln` in the repo root.
- Restore now writes to `artifacts\publish\restore.log`.

## Basic usage

Run the script from the repo root:

```powershell
.\publish-all.ps1 -Configuration Release -OutputRoot "E:\OMP\Publish\OMP" -Restore -CleanOutput
```

For the standard local runtime layout, copy or deploy these published folders:

```text
OpenModulePlatform.Portal                 -> E:\OMP\Sites\Portal
OpenModulePlatform.HostAgent.WindowsService -> E:\OMP\Services\HostAgent
OpenModulePlatform.WorkerManager.WindowsService -> E:\OMP\Services\WorkerManager
OpenModulePlatform.WorkerProcessHost      -> E:\OMP\Services\WorkerProcessHost
```

The Portal IIS application can then point to `E:\OMP\Sites\Portal`; the Windows services should point to their corresponding service folders.

## Local Windows service names

The standard local service names are:

```text
OpenModulePlatform.HostAgent
OpenModulePlatform.WorkerManager
```

Use those names when stopping or starting the runtime around publish operations.

Default usage without an explicit output root is also supported:

```powershell
.\publish-all.ps1 -Restore -CleanOutput
```

Run in parallel with four workers:

```powershell
.\publish-all.ps1 -Restore -CleanOutput -Parallel -MaxParallel 4
```

Publish for Windows x64 framework-dependent:

```powershell
.\publish-all.ps1 -Restore -CleanOutput -Parallel -MaxParallel 4 -Runtime win-x64 -SelfContained:$false
```

Publish for Windows x64 self-contained:

```powershell
.\publish-all.ps1 -Restore -CleanOutput -Parallel -MaxParallel 4 -Runtime win-x64 -SelfContained:$true
```

## Output

The default output root is:

```text
artifacts\publish\<ProjectName>
```

Each project also gets its own log file:

```text
artifacts\publish\<ProjectName>.publish.log
```

Restore also gets its own log file:

```text
artifacts\publish\restore.log
```


## Example module folder layout

Example modules use this layout:

```text
examples/<ModuleName>/
  sql/
    1-setup-*.sql
    2-initialize-*.sql
  WebApp/
  WorkerApp/      # when the module includes a worker app
  ServiceApp/     # when the module includes a Windows service app
```

The folder directly under `examples` is always the module folder. Application
projects live under app-specific subfolders such as `WebApp`, `WorkerApp`, or
`ServiceApp`. SQL scripts are module-owned and stay in the module-level `sql`
folder, not under an individual app project.


## Local runtime configuration

Published service folders need runtime `appsettings.json` files with real local
paths and a real OMP database connection string. Repository defaults intentionally
keep those values empty.

For a local development machine you can generate standard runtime configuration:

```powershell
.\scripts\write-local-runtime-config.ps1 `
  -RuntimeRoot "E:\OMP" `
  -SqlServer "localhost" `
  -Database "OpenModulePlatform" `
  -HostKey "sample-host"
```

The script preserves existing config files by default. Pass `-Overwrite` only when
you intentionally want to replace the runtime files.

The standard service names are:

```text
OpenModulePlatform.HostAgent
OpenModulePlatform.WorkerManager
```

The WorkerManager runtime config must point to:

```text
E:\OMP\Services\WorkerProcessHost\OpenModulePlatform.WorkerProcessHost.exe
```

and normally uses database catalog mode:

```json
"CatalogMode": "OmpDatabase"
```
