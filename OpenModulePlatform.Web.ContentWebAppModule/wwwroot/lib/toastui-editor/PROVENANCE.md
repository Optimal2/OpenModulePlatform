# Provenance: TOAST UI Editor 3.2.2

| Item | Value |
| --- | --- |
| Package | `@toast-ui/editor` 3.2.2 (npm registry) |
| Files | `toastui-editor-all.min.js` (534 289 bytes) and `toastui-editor.min.css` (165 438 bytes), copied unchanged from `https://uicdn.toast.com/editor/3.2.2/` |
| License | MIT (NHN Cloud Corp.) — see TOASTUI-LICENSE.txt |
| Upstream | https://github.com/nhn/tui.editor |

## Why it is vendored

Campaign csp-vagen-till-enforcement: the admin editor
(`Pages/Admin/Edit.cshtml`) loaded these files from `https://uicdn.toast.com`,
which was the only third-party origin in any OMP content-security policy.
Vendoring removes the origin from the module's `script-src`/`style-src` and the
runtime dependency on the CDN's availability.

The stylesheet is self-contained: every `url(...)` reference is an embedded
`data:` URI, so no font or image origins are needed alongside it.

## Upgrade

Download the two files for the new version from the same CDN path (or extract
`package/dist/` from `npm pack @toast-ui/editor@<version>`), replace them here,
and update this file.
