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
