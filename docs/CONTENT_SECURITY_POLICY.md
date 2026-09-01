# Content Security Policy

The platform emits a Content-Security-Policy (CSP) from the shared
`UseOmpSecurityHeaders` middleware
(`OpenModulePlatform.Web.Shared/Extensions/OmpWebHostingExtensions.cs`),
alongside X-Content-Type-Options, Referrer-Policy, X-Frame-Options,
Permissions-Policy, and HSTS.

## Rollout model

Every app ships in **report-only** mode first: the middleware emits
`Content-Security-Policy-Report-Only`, so nothing breaks — violations are
reported, not blocked. Enforcement is a configuration flip per app once its
report log is clean:

```json
"Portal": {
  "SecurityHeaders": {
    "ContentSecurityPolicy": {
      "ReportOnly": false
    }
  }
}
```

The section lives under the app's web-options section (`Portal` for the
Portal and the modules, `WebApp` for the Auth app) — the same section name
the app passes to `AddOmpWebDefaults`/`UseOmpWebDefaults`.

## Configuration

`ContentSecurityPolicyOptions`
(`OpenModulePlatform.Web.Shared/Options/SecurityHeadersOptions.cs`):

- `Enabled` (default `true`) — emit the header at all.
- `ReportOnly` (default `true`) — report-only vs. enforcing header name.
- `Policy` (default null) — full replacement policy string. When unset the
  shared baseline (`OmpContentSecurityPolicy.Baseline`,
  `OpenModulePlatform.Web.Shared/Security/OmpContentSecurityPolicy.cs`) is
  used. An app that needs extra sources sets the complete policy here, with a
  `"//"` comment in appsettings.json stating which code forces each addition.
- `ReportPath` (default `/omp/csp-report`) — where browsers POST violation
  reports; combined with the request PathBase at emission time. Empty string
  omits the `report-uri` directive.

An app can also tighten or replace the policy imperatively: the middleware
uses the same set-if-missing pattern as the other security headers, so a
header set earlier in the app's own pipeline always wins.

## Violation collection

Violation reports are collected two ways:

1. **Endpoint + log.** `MapOmpCspReportEndpoint` (mapped automatically by
   `UseOmpWebDefaults`, and directly by the Auth app's hand-rolled pipeline)
   accepts the browser's POST at `/omp/csp-report` and logs it as a warning
   under the `OpenModulePlatform.Web.Shared.Security.CspReport` category.
   With the default NLog configuration that lands in each app's own log file,
   so per-app report triage is a log grep. The endpoint is anonymous
   (browsers send reports without antiforgery tokens), returns 204, and caps
   the body it reads at 64 KB.
2. **Browser console.** During report-only rollout, every violation is also
   printed by the browser's developer console — the fastest feedback while
   testing a page.

`report-uri` is used rather than `report-to`: `report-uri` is deprecated but
universally supported (including Firefox), needs no `Reporting-Endpoints`
header plumbing, and the payload is only ever logged.

## The shared baseline

```
default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline';
img-src 'self' data:; font-src 'self'; connect-src 'self' ws: wss:; media-src 'self';
worker-src 'none'; object-src 'none'; base-uri 'self'; form-action 'self';
frame-src 'self'; frame-ancestors 'self'
```

Directive notes:

- `connect-src ... ws: wss:` — SignalR (topbar hub, live-refresh hub, Blazor
  circuits) negotiates same-origin WebSockets; browsers that do not treat
  `'self'` as matching WebSocket schemes (Safari) need the explicit scheme
  entries. Script/style execution stays gated by the other directives.
- `img-src ... data:` — the shared stylesheet
  `OpenModulePlatform.Web.Shared/wwwroot/css/omp-datetime.css` embeds an SVG
  icon as a `data:` URI.
- `worker-src 'none'` / `object-src 'none'` — no workers or plugins anywhere
  in the platform (verified against the vendored bundles).
- `unsafe-eval` is **not** present and must not be added: verified against
  the webamp bundle (its only `new Function` is a dead setImmediate polyfill
  path), the vendored signalr.min.js (a caught `Function("return this")`
  fallback), and Blazor Server (no eval, no WASM in these apps).

### Why `unsafe-inline` remains in the baseline

The baseline must work unmodified for every app, and these code paths still
require it (each is a removal candidate; see "Migration plan"):

- **Inline `<script>` blocks in Portal pages** (16 blocks):
  `Pages/Notifications.cshtml`, `Pages/Messages/Index.cshtml`,
  `Pages/Messages/Thread.cshtml`, `Pages/Admin/AppInstanceEdit.cshtml`,
  `Pages/Admin/ArtifactUpload.cshtml`, `Pages/Admin/ConfigSettings.cshtml`,
  `Pages/Admin/IFrameUrls.cshtml`, `Pages/Admin/InstanceTemplateAppEdit.cshtml`,
  `Pages/Admin/Maintenance.cshtml`, `Pages/Admin/ModuleEdit.cshtml`,
  `Pages/Admin/ModulePackageImport.cshtml`, `Pages/Admin/Navigation.cshtml`,
  `Pages/Admin/PortalEntries.cshtml`, `Pages/Admin/UniversalPackageBuilder.cshtml`,
  `Pages/Admin/Rbac/Role.cshtml`, `Pages/Shared/_ValidationScriptsPartial.cshtml`.
- **Inline `<script>` in ContentWebAppModule**: the admin editor bootstrap
  (`Pages/Admin/Edit.cshtml`) and the data-dependent `DB_JSON_SCRIPT` block
  emitted by `Services/ServerReportRenderer.cs` (cannot be hashed — the JSON
  payload varies per report).
- **Inline `<script>` in the example modules' OpenDocViewer demo pages**
  (server-rendered JSON bootstrap via `@Html.Raw`).
- **The shared fallback status page**
  (`BuildFallbackStatusPageHtml`) — a deliberate inline-HTML page that must
  render even when static assets are unavailable during error handling.
- **Inline `<style>` and style attributes**: webamp injects its skin CSS as a
  runtime `<style>` element (dynamic, cannot be hashed), and Razor renders
  dynamic style attributes the dashboard needs
  (`Portal/Pages/Index.cshtml` widget geometry,
  `Portal/Pages/Messages/_ThreadMessages.cshtml`,
  `Portal/Pages/Admin/PortalEntries.cshtml`).

## Per-app policies

The appsettings.json of each app carries its effective policy:

- **Portal** — baseline plus `blob:` in `img-src`/`media-src` (webamp unzips
  skins in JS and serves sprites via `URL.createObjectURL`; track/album-art
  blobs) and `frame-src 'none'` (Portal embeds nothing).
- **ContentWebAppModule** — baseline plus `https://uicdn.toast.com` in
  `script-src`/`style-src` (the admin editor loads the TOAST UI editor from
  that CDN) and `https:` in `img-src` (trusted DB/file-driven content may
  reference external images by design).
- **iFrameWebAppModule** — baseline with `frame-src 'self' https: http:`: the
  module's purpose is embedding administrator-configured URLs stored in the
  database, so frame targets are runtime data, not a build-time list.
- **Auth** — the strict-policy reference: after the login page's inline
  `<style>`/`<script>` blocks were moved to `wwwroot/css/login.css` and
  `wwwroot/js/login.js`, the Auth app runs
  `default-src 'self'; script-src 'self'; style-src 'self'; ...` with **no
  unsafe-inline anywhere**.
- **examples/** — the baseline. The OpenDocViewer demo pages frame the ODV
  origin configured via `OpenDocViewer:BaseUrl`; deployments with an
  off-origin viewer extend `frame-src` through the same configuration.

## Migration plan (removing the remaining unsafe-inline)

1. Move the Portal and Content-module inline script blocks to static JS
   files; replace `DB_JSON_SCRIPT` with an `application/json` block read by
   static JS.
2. Vendor the TOAST UI editor (removes the only third-party origin).
3. Then drop `'unsafe-inline'` from the baseline's `script-src`, and from
   `style-src` once webamp is replaced or its style injection is hashed.

## Security review notes (independent review, 2026-09-01)

An independent security review of this design raised points that are
recorded here as accepted follow-ups rather than silently dropped:

- `connect-src 'self' ws: wss:` accepts any WebSocket origin. Tightening
  candidate: pin the deployment's own ws(s):// origin per app once
  enforcement starts (the host varies per installation, so the shared
  baseline cannot pin it).
- The iFrame module's `frame-src 'self' https: http:` is the weakest
  exception. Before that app enforces, replace it with a DB-driven allowlist
  of exact https: origins (the module already scheme-sanitizes; an origin
  allowlist is the natural next step).
- The Content module's `img-src https:` trusts administrator-authored
  content by design; tightening means an explicit image-origin allowlist.
- The report endpoint is anonymous and log-only with a 64 KB body cap. If
  report volume or abuse becomes a concern, add rate limiting and payload
  schema validation before enforcement.
- CSP reports can contain URLs and referrer data; treat the app logs they
  land in as operational telemetry with the usual access limits.
- Enforcement exit gate per app: flip `ReportOnly` to `false` only after a
  bake period with zero actionable violations in the app's CspReport log,
  and keep the flip reversible (it is a config value, not a code change).
- `upgrade-insecure-requests` is deliberately absent: the platform supports
  plain-HTTP intranet deployments, where the directive would break every
  non-TLS page.

## Verifying the policy (break test)

The middleware contract is covered by
`OpenModulePlatform.Portal.Tests/Security/OmpSecurityHeadersTests.cs`. To
prove a browser actually enforces a policy, run any app with a strict
configured policy and `ReportOnly=false`, then load a page carrying an inline
`<script>` and an external `<img>`: the browser console must show
"Refused to execute inline script ..." / "Refused to load the image ..."
and the app log must carry the matching `CspReport` warnings. This was
executed against the Portal during the initial rollout (2026-09-01).
