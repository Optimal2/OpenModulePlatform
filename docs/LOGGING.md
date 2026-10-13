# Logging with NLog

This solution uses NLog as the shared logging provider for the web hosts, the classic example worker service, and the Windows worker runtime executables.
The guidance in this document applies to the `0.1.x` release line.

## Web applications

All web projects that call `AddOmpWebDefaults(...)` inherit the same NLog bootstrap from `OpenModulePlatform.Web.Shared`.
This keeps the logging provider and package version centralized while allowing each application to keep its own `appsettings.json` values, such as the log file name.

## Worker service

`OpenModulePlatform.Service.ExampleServiceAppModule` registers NLog directly on its generic host because it runs as its own process and does not consume the shared web project.

## Worker manager and child host

`OpenModulePlatform.WorkerManager.WindowsService` and `OpenModulePlatform.WorkerProcessHost` both register NLog on their generic hosts.

Manager-driven worker plugins, such as `OpenModulePlatform.Worker.ExampleWorkerAppModule`, do not bootstrap logging on their own. They receive `ILogger` dependencies from the child host service provider.

### Worker process host: one file per process

WorkerManager starts one `OpenModulePlatform.WorkerProcessHost` process per worker
instance, all from the same provisioned folder, so they share one log directory:
`<host folder>\..\Logs\WorkerProcessHost` (a sibling of the versioned host folder,
on the drive the host was provisioned to). Each process writes its own file:

- `OpenModulePlatform.WorkerProcessHost-yyyy-MM-dd-<process id>.log`

The first line of every file names the worker instance it belongs to
(`Worker process host starting. BaseDirectory=…, WorkerInstanceKey=…`). Until
0.3.75 all processes wrote to one file per day, and only the process that opened
it first wrote anything: every other worker's lines were lost (measured on one
installation: 36 worker process starts in a day, 2 host start lines on disk, and
no line from any worker of one module in four days of files).

The configuration that runs is the one `Program.cs` sets before the host is built
(`ConfigureDefaultNLog`); `UseNLog()` does not replace it, so the `NLog` section in
the host's `appsettings.json` is not used by a running host (and artifact payloads
strip `appsettings.json` anyway). That section is kept in step with the same file
name.

What the naming guarantees: two worker processes never write the same file, so
no process loses its lines to another, whichever starts first. Files are kept 14
days: every host process deletes this host's files (`OpenModulePlatform.WorkerProcessHost-*.log`,
the old single-file name included) older than 14 days by last write when it
starts, and NLog's `MaxArchiveDays` cleans a running process's own files when it
rolls over to a new day. What it does not guarantee: one file per worker per day
(a worker that is restarted starts a new file, so a day can hold several files
for one worker; find them by the `WorkerInstanceKey` on the first line), and it
does not change the WorkerManager service's own log, which is one process.

## Configuration

Each executable project has an `NLog` section in `appsettings.json`.
By default logs are written to:

- `logs/<AppName>-yyyy-MM-dd.log` (one file per process name and day; a host
  that runs several processes of the same executable needs a per-process name,
  see the worker process host above)
- the console output of the running process

The log directory is ignored by Git through `.gitignore`.

### Packaged web apps

The checked-in `appsettings.json` of a packaged web app never reaches a host: the
artifact payload strips `appsettings*.json`, and HostAgent writes the component's
`Packaging/appsettings.json` (merged over its built-in web-app template) instead.
The built-in template carries no `NLog` section, so a component packaged without
one runs with no log targets at all. The Portal, Auth and Content module therefore
repeat their `NLog` section in `Packaging/appsettings.json`;
`PackagedNLogConfigurationTests` in the Portal test project parses every packaged
and checked-in section the way NLog does at startup. Web apps that go through
`UseOmpWebDefaults` render the request correlation id with
`${scopeproperty:item=CorrelationId}` in their layouts (see
`docs/conventions/logging.md`); the Auth app has its own pipeline without that
scope and leaves the renderer out.
