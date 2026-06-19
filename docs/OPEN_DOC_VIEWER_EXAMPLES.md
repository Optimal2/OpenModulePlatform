# OpenDocViewer Example CSP Notes

This note covers the host-side Content Security Policy requirements for the
OpenModulePlatform OpenDocViewer embed examples.

It is intentionally narrower than the full OpenDocViewer deployment guidance:

- this document is about the OMP host pages that embed the viewer
- OpenDocViewer's own CSP is configured separately in the viewer deployment
- `frame-ancestors` for the viewer must be emitted by the viewer response, not
  by the OMP host page

## Which examples this covers

The current examples are:

- `examples/WebAppModule/WebApp/Pages/OpenDocViewerDemo.cshtml`
- `examples/ServiceAppModule/WebApp/Pages/OpenDocViewerDemo.cshtml`
- `examples/WorkerAppModule/WebApp/Pages/OpenDocViewerDemo.cshtml`
- `examples/WebAppBlazorModule/WebApp/Components/Pages/OpenDocViewerDemo.razor`

All four examples embed OpenDocViewer in an `<iframe>` and start the default
sample document through `?sessionurl=` pointing at the host-owned
`/opendocviewer-demo/bundle` endpoint.

## Minimum host-side CSP requirements

For the default OMP examples, the host page CSP must allow:

- the viewer origin in `frame-src` or `child-src`
- any inline example script blocks through a nonce or hash in `script-src`

That means:

- same-origin viewer deployments can usually use `frame-src 'self'`
- cross-origin viewer deployments must add the exact viewer origin
- do not rely on `unsafe-inline` just to make the examples work; prefer a nonce
  or hash for the inline blocks
- for the current Razor Pages examples, a per-response nonce is usually the
  practical choice because the bootstrap block renders request-specific JSON

## Example-specific notes

### Razor Pages examples

These files contain inline `<script>` blocks today:

- `examples/WebAppModule/WebApp/Pages/OpenDocViewerDemo.cshtml`
- `examples/ServiceAppModule/WebApp/Pages/OpenDocViewerDemo.cshtml`
- `examples/WorkerAppModule/WebApp/Pages/OpenDocViewerDemo.cshtml`

If a host enables CSP on those pages, `script-src` must include a matching
nonce or hash for the inline bootstrap script.

In practice, that bootstrap block changes per request because it contains the
rendered bundle JSON. A fixed static hash is therefore usually not workable;
use a nonce unless you deliberately compute a hash for the exact rendered
response body.

The `WebAppModule` example also includes a second inline script for the local
file picker flow. That second block needs the same nonce or hash treatment.

### Blazor example

`examples/WebAppBlazorModule/WebApp/Components/Pages/OpenDocViewerDemo.razor`
does not add inline bootstrap script in the component itself. From the
OpenDocViewer integration alone, the host page only needs to allow the iframe
source in `frame-src` or `child-src`.

Normal Blazor or site-level CSP requirements still apply to the rest of the
application shell.

### Same-origin parent/opener bridge

The `WebAppModule` local-file example also uses the same-origin parent/opener
bridge by assigning `window.ODV_BOOTSTRAP` and reopening the viewer without
`noopener` for that specific flow.

That path has two practical requirements:

- the viewer must remain same-origin with the host page, otherwise
  `window.parent` and `window.opener` bootstrap access is unavailable
- the host page CSP still needs the inline script block to be allowed by nonce
  or hash

Allowing a cross-origin viewer in `frame-src` is not enough for that legacy
bridge flow.

## What the host page usually does not need

For the default `?sessionurl=` examples, the host page usually does not need
special `connect-src` entries for the bundle endpoint. The iframe navigates to
OpenDocViewer, and the viewer document performs the `sessionurl` fetch under
its own CSP.

Add host-side `connect-src` or `img-src` allowances only when the host page
itself starts fetching bundle data, source files, or preview images before the
iframe is loaded.

## Example policies

Example same-origin Razor Pages host policy shape:

```text
Content-Security-Policy:
  default-src 'self';
  base-uri 'self';
  object-src 'none';
  frame-src 'self';
  script-src 'self' 'nonce-{per-response-value}';
  style-src 'self' 'unsafe-inline';
  img-src 'self' data:;
```

Example cross-origin Blazor host policy shape:

```text
Content-Security-Policy:
  default-src 'self';
  base-uri 'self';
  object-src 'none';
  frame-src 'self' https://viewer.example;
  script-src 'self';
  style-src 'self' 'unsafe-inline';
  img-src 'self' data:;
```

Treat those as starting points only. Tighten them per application and keep the
viewer deployment policy aligned with the current OpenDocViewer documentation.
