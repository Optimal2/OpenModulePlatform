# Example WebAppModule

This project is a minimal OMP web-only example module.

It demonstrates:

- a module-specific schema
- a web application registered through the OMP core model
- a module entry in OMP Portal through an app instance route
- logging through the shared web host defaults, using `ILogger<T>` in module code

Use this project as a starting point for simple web-focused OMP modules.

## Logging

This template does not configure NLog directly in `Program.cs`.
That setup is provided by `AddOmpWebDefaults(...)` in the shared web host layer.

As a module author, the normal pattern is therefore:

- call `builder.AddOmpWebDefaults(...)`
- inject `ILogger<T>` into pages/services
- log with `LogInformation`, `LogWarning`, `LogError`, and similar methods

This keeps the module template small while still giving it shared logging behavior.
