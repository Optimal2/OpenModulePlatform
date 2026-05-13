# Example WebAppModule

This project is a minimal OMP web-only example module.

It demonstrates:

- a module-specific schema
- a web application registered through the OMP core model
- a module entry in OMP Portal through an app instance route
- the shared Portal topbar, including module navigation, favorites, language, roles, and logout
- shared OMP status and error pages through `OmpStatusPageModelBase`, `OmpErrorPageModelBase`, and the `OmpError` view component
- logging through the shared web host defaults, using `ILogger<T>` in module code

Use this project as a starting point for simple web-focused OMP modules.

## Shared Web Defaults

This template does not configure NLog, request localization, topbar services,
status-code handling, or exception handling directly in `Program.cs`. That setup
is provided by `AddOmpWebDefaults(...)` and `UseOmpWebDefaults(...)` in the
shared web host layer.

As a module author, the normal pattern is therefore:

- call `builder.AddOmpWebDefaults(...)`
- include the shared topbar CSS/JS assets in the layout
- render the topbar through `@await Component.InvokeAsync("PortalTopBar")`
- include `/Error` and `/status/{statusCode:int}` Razor Pages using the shared base models
- inject `ILogger<T>` into pages/services
- log with `LogInformation`, `LogWarning`, `LogError`, and similar methods

This keeps module templates small while still giving them the same topbar,
localization, error handling, and logging behavior as the Portal and production
modules.
