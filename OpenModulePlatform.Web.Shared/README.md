# OpenModulePlatform.Web.Shared

Shared web infrastructure for OMP portal and module web applications.

The library provides:

- common hosting defaults
- SQL connection factory support
- shared RBAC integration
- common Razor Page base classes
- lightweight HTTP helpers

Use this project when building an OMP web application that should align with the shared portal conventions.

## OpenDocViewer examples

The shared `OpenDocViewer` helper types in this project back the OMP
OpenDocViewer embed examples under `examples/`.

If you enable a strict Content Security Policy on those host pages, review
`docs/OPEN_DOC_VIEWER_EXAMPLES.md` first. The Razor Pages examples currently
contain inline bootstrap script, and the `WebAppModule` variant also contains a
second inline local-file script block that must be covered by a nonce or hash.
Because the bootstrap block renders request-specific JSON, a nonce is usually
the practical option.
