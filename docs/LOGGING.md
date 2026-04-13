# Logging with NLog

This solution uses NLog as the shared logging provider for the web hosts and the example worker service.
The guidance in this document applies to the `0.1.x` release line.

## Web applications

All web projects that call `AddOmpWebDefaults(...)` inherit the same NLog bootstrap from `OpenModulePlatform.Web.Shared`.
This keeps the logging provider and package version centralized while allowing each application to keep its own `appsettings.json` values, such as the log file name.

## Worker service

`OpenModulePlatform.Service.ExampleServiceAppModule` registers NLog directly on its generic host because it runs as its own process and does not consume the shared web project.

## Configuration

Each executable project has an `NLog` section in `appsettings.json`.
By default logs are written to:

- `logs/<AppName>-yyyy-MM-dd.log`
- the console output of the running process

The log directory is ignored by Git through `.gitignore`.
