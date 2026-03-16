# Example WebApp Blazor Module

This project is the Blazor Server port of the minimal OMP web-only example module, renamed so it can coexist with the original Razor Pages version.

It demonstrates:

- a module-specific schema
- a Blazor Server web application registered through the OMP core model
- a module entry in OMP Portal through an app instance route
- reuse of the shared OMP web defaults for auth, RBAC and SQL access

## What changed in the port

The original Razor Pages implementation has been replaced with interactive Blazor Server components.
The module still follows the same OMP principles:

- the module owns its schema and configuration table
- Windows/integrated auth and OMP RBAC still gate the UI
- the repository layer still talks directly to `omp_example_webapp_blazor_module.Configurations`
- the route structure still exposes overview, configuration list and configuration editing

## Component structure

- `Components/App.razor` - application shell
- `Components/Routes.razor` - router and not-found handling
- `Components/Layout/MainLayout.razor` - shared layout/header/navigation
- `Components/Pages/Overview.razor` - module overview page
- `Components/Pages/Configurations/Index.razor` - configuration list
- `Components/Pages/Configurations/Edit.razor` - configuration editing
- `Components/ExampleWebAppBlazorModuleComponentBase.cs` - shared auth/title helper for components

Use this project as a starting point for simple OMP modules that should use Blazor Server rather than Razor Pages. The module identity, permissions, schema and route are unique so the original and Blazor examples can be installed side by side.
