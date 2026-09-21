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

## Configuration

Each executable project has an `NLog` section in `appsettings.json`.
By default logs are written to:

- `logs/<AppName>-yyyy-MM-dd.log`
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
