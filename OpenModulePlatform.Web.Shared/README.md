<!-- File: OpenModulePlatform.Web.Shared/README.md -->
# OpenModulePlatform.Web.Shared

`OpenModulePlatform.Web.Shared` contains the shared infrastructure used by OMP web applications.

## Included building blocks

- `AddOmpWebDefaults(...)` and `UseOmpWebDefaults(...)`
- `SqlConnectionFactory`
- `RbacService`
- `OmpPageModel`
- `OmpSecurePageModel`
- `HttpRequest.GetPublicBaseUrl()`

The shared project is intended to keep hosting, authorization and common page-model behavior consistent across portal and module web applications.
