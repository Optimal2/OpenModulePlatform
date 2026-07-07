# OpenModulePlatform.Web.Shared Integration Checklist

This checklist documents the integration contract for OMP web applications that consume `OpenModulePlatform.Web.Shared`.

## Startup registration

- [ ] In `Program.cs`, call `AddOmpWebDefaults<TAppResource>(optionsSectionName: "Portal")` where `TAppResource` is the application's shared resource type.
- [ ] In the web pipeline, call `UseOmpWebDefaults()`.

## Layout / shared UI

- [ ] Include the `PortalTopBar` partial in `_Layout.cshtml`:
  ```cshtml
  @await Component.InvokeAsync("PortalTopBar")
  ```
- [ ] Include `portal-topbar.css` and `portal-topbar.js` from the shared library in the layout.
- [ ] Blazor consumers: use the shared `PortalTopBar` component (`OpenModulePlatform.Web.Shared.Components.Layout.PortalTopBar`).

## Authentication configuration

- [ ] Provide a matching `OmpAuth` configuration section with the following key settings aligned between Portal and consuming web apps:
  - `CookieName`
  - `ApplicationName`
  - `DataProtectionKeyPath`

## Role-switch endpoints

- [ ] Both `/security/set-active-role` and `/rbac/set-active-role` are valid role-switch endpoints.
- [ ] Use the constants `OmpAuthDefaults.SetActiveRolePath` and `OmpAuthDefaults.RbacSetActiveRolePath` instead of hardcoding either path.

## Not-a-consumer note

- [ ] `ODVGateway` is intentionally standalone and is **not** a `Web.Shared` consumer. Do not apply this checklist to it.
