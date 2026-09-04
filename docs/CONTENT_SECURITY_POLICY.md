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

- **Inline `<script>` in ContentWebAppModule**: the admin editor bootstrap
  (`Pages/Admin/Edit.cshtml`) and administrator-authored trusted HTML content
  (which may carry its own scripts by design).
- **Inline `<script>` in the example modules' OpenDocViewer demo pages**
  (server-rendered JSON bootstrap via `@Html.Raw`, plus one pure client-side
  block in the WebAppModule demo). Classified block by block under
  "Example modules: inline-script classification" below — all four are movable.
- **The shared fallback status page**
  (`BuildFallbackStatusPageHtml`) — a deliberate inline-HTML page that must
  render even when static assets are unavailable during error handling. It
  carries inline `<style>` only, never `<script>`.
- **Inline `<style>` and style attributes**: webamp injects its skin CSS as a
  runtime `<style>` element (dynamic, cannot be hashed), and Razor renders
  dynamic style attributes the dashboard needs
  (`Portal/Pages/Index.cshtml` widget geometry,
  `Portal/Pages/Messages/_ThreadMessages.cshtml`,
  `Portal/Pages/Admin/PortalEntries.cshtml`).

The Portal's 16 inline `<script>` blocks moved to static files under
`OpenModulePlatform.Portal/wwwroot/js/` on 2026-09-04 (campaign
csp-vagen-till-enforcement), and the Content module's data-dependent
`DB_JSON_SCRIPT` block now renders as a non-executable
`<script type="application/json">` data block read by the static
`wwwroot/js/omp-server-report.js` (the documented `window.<name>` /
`window.<name>Report` globals are assigned by that reader, which the renderer
emits immediately after each data block so source-order consumers are
unaffected). `PortalInlineScriptGuardTests` fails the build if an executable
inline block returns to a Portal page.

## Per-app policies

The appsettings.json of each app carries its effective policy:

> **The hardening lives in configuration, and losing the key un-does it silently.**
> `OmpContentSecurityPolicy.Build` falls back to `Baseline` whenever
> `ContentSecurityPolicy:Policy` is null, empty, or whitespace
> (`OpenModulePlatform.Web.Shared/Security/OmpContentSecurityPolicy.cs`), and that baseline
> still carries `script-src 'self' 'unsafe-inline'`. So an app whose `Policy` key is dropped —
> by a hand-edited `appsettings.json`, or by a config file copied forward from an older
> artifact version — silently returns to allowing inline script. There is no warning and no
> failed startup: the header is simply weaker. When you change an app's configuration, assert
> the emitted `Content-Security-Policy` header rather than assuming the key survived.

- **Portal** — baseline plus `blob:` in `img-src`/`media-src` (webamp unzips
  skins in JS and serves sprites via `URL.createObjectURL`; track/album-art
  blobs) and `frame-src 'none'` (Portal embeds nothing). Since
  2026-09-04 `script-src` has **no** `unsafe-inline`; `style-src` keeps it for
  webamp's runtime-injected skin stylesheet and the dynamic style attributes
  listed above.
- **ContentWebAppModule** — baseline plus `https:` in `img-src` (trusted
  DB/file-driven content may reference external images by design). The TOAST
  UI editor is vendored under `wwwroot/lib/toastui-editor/` (see
  `PROVENANCE.md` there), so no third-party origin remains in any directive.
- **iFrameWebAppModule** — baseline with `frame-src 'self'`, replaced per
  request by the `UseIFrameFrameSourceCsp` middleware
  (`Security/IFrameFrameSourcePolicy.cs`) with an allowlist of the exact
  origins of the enabled URLs in `omp_iframe.urls` (cached 60 s; on a database
  error the directive falls back to `frame-src 'self'` for that request). The
  old `https: http:` scheme wildcards are gone. Since 2026-09-04 (campaign
  csp-sista-undantagen) `script-src` has **no** `unsafe-inline` — the module
  renders zero inline scripts, pinned by `IFrameCspSmokeTests`
  (OpenModulePlatform.UiTests) against Index and Standalone. `style-src` keeps
  the baseline's `unsafe-inline` (shared runtime-injected styles).
- **Auth** — the strict-policy reference: after the login page's inline
  `<style>`/`<script>` blocks were moved to `wwwroot/css/login.css` and
  `wwwroot/js/login.js`, the Auth app runs
  `default-src 'self'; script-src 'self'; style-src 'self'; ...` with **no
  unsafe-inline anywhere**.
- **examples/** — the baseline. The OpenDocViewer demo pages frame the ODV
  origin configured via `OpenDocViewer:BaseUrl`; deployments with an
  off-origin viewer extend `frame-src` through the same configuration.

## Example modules: inline-script classification

Every inline `<script>` block under `examples/` (inventoried 2026-09-04,
campaign csp-sista-undantagen), classified as movable or rejected-with-reason.
Inline event handlers and `javascript:` URLs were also swept for: none exist.
Once the four blocks below are migrated, the baseline's
`script-src 'unsafe-inline'` has no remaining consumer in `examples/`.

1. `examples/WebAppModule/WebApp/Pages/OpenDocViewerDemo.cshtml:32` —
   bootstrap block: `window.ODV_BOOTSTRAP = @Html.Raw(Model.BundleJson)` plus a
   `window.ODV_DEMO_OPTIONS` object with `JsonSerializer`-escaped values.
   **Movable.** Same migration as the Content module's `DB_JSON_SCRIPT` block:
   render the JSON as non-executable `<script type="application/json">` data
   blocks and read them from a static JS file (the reader assigns the
   documented `window.*` globals immediately after each data block, so
   source-order consumers are unaffected). The `Html.Raw` string-escape
   question disappears with the pattern — JSON goes through the data block's
   HTML encoding, never into executable script context.
2. `examples/WebAppModule/WebApp/Pages/OpenDocViewerDemo.cshtml:52` — the
   local-files IIFE (file-input handling, `URL.createObjectURL` bundle
   building, new-tab wiring). Contains no server-rendered data at all.
   **Movable.** Lifts verbatim into a static `wwwroot/js/` file; it consumes
   only the globals from block 1 and DOM ids.
3. `examples/ServiceAppModule/WebApp/Pages/OpenDocViewerDemo.cshtml:21` —
   bootstrap block: `window.ODV_BOOTSTRAP = @Html.Raw(Model.BundleJson)`.
   **Movable.** Same `application/json` data-block pattern as block 1.
4. `examples/WorkerAppModule/WebApp/Pages/OpenDocViewerDemo.cshtml:21` —
   identical bootstrap block. **Movable.** Same pattern.

No block is rejected: none of them needs to stay inline (no error-path
rendering constraint like the fallback status page, no administrator-authored
scripting surface like the Content module's trusted HTML).

## Migration plan (removing the remaining unsafe-inline)

1. ~~Move the Portal and Content-module inline script blocks to static JS
   files; replace `DB_JSON_SCRIPT` with an `application/json` block read by
   static JS.~~ **Done 2026-09-04** (Portal blocks and `DB_JSON_SCRIPT`; the
   Content module's `Pages/Admin/Edit.cshtml` editor bootstrap remains, as
   does trusted-HTML inline scripting by design).
2. ~~Vendor the TOAST UI editor (removes the only third-party origin).~~
   **Done 2026-09-04** (`wwwroot/lib/toastui-editor/`).
3. Drop `'unsafe-inline'` from the baseline's `script-src` once the example
   modules' demo pages and the Content module's editor bootstrap are migrated
   (trusted-HTML content scripting keeps the Content module on a per-app
   `unsafe-inline` regardless), and from `style-src` once webamp is replaced
   or its style injection is hashed. ~~The iFrame module has no inline scripts
   at all; its `script-src 'unsafe-inline'` is now unjustified and can be
   dropped with a smoke test as the only cost.~~ **Done 2026-09-04** (campaign
   csp-sista-undantagen): the iFrame module runs `script-src 'self'`, proven
   by `IFrameCspSmokeTests`; the example blocks are classified above, all
   movable. As part of the same campaign the `ReplaceFrameSource` regex gained
   a `(?<![-\w])` lookbehind so a future `child-frame-src` directive cannot be
   corrupted by the frame-src rewrite (regression tests in
   `IFrameFrameSourcePolicyTests`).
4. Then the per-app enforcement flips — see "Enforcement exit gate" below.

## Security review notes (independent review, 2026-09-01; decided 2026-09-04)

An independent security review of this design raised points that were decided
one at a time in campaign csp-vagen-till-enforcement. A rejected finding keeps
its reason here so it does not come back:

1. **`connect-src 'self' ws: wss:` accepts any WebSocket origin.**
   **Rejected for the shared baseline, kept as flip-time hardening.** The
   deployment's own WebSocket host varies per installation, so the shared
   baseline cannot pin it; a script that can open a WebSocket can already open
   an HTTP connection to the same origin, and `ws:` to an attacker host
   additionally requires script execution, which the other directives gate.
   Operators who flip an app to enforcement may pin the exact `wss://` origin
   in that app's configured `Policy` — see the exit-gate checklist.
2. **The iFrame module's `frame-src 'self' https: http:` was the weakest
   exception.** **Implemented.** `UseIFrameFrameSourceCsp` now replaces the
   directive per request with the exact origins of the enabled configured
   URLs, and the Standalone read path (`Pages/Standalone.cshtml.cs`) applies
   the same `OmpUrlSafety.SanitizeHref` guard as Index (R8-P1-4 symmetry).
3. **The Content module's `img-src https:` trusts administrator-authored
   content.** **Rejected (accepted risk, by design).** The module's trusted
   HTML/markdown model deliberately lets administrators reference intranet
   image origins; an image-origin allowlist would break existing content
   silently, and images cannot execute script. The residual risk (a malicious
   or compromised administrator embedding a tracking pixel) is an
   administrator-trust question, not a CSP gap — the pages already render raw
   HTML by design.
4. **The report endpoint is anonymous and log-only with a 64 KB body cap.**
   **Rejected for now (watch item).** The endpoint only writes a warning log
   line; the 64 KB cap bounds memory per request, and NLog's file targets
   bound disk growth. Rate limiting and payload schema validation add real
   complexity to a log-only sink; they become mandatory if the CspReport log
   category ever shows abuse volume during the bake period.
5. **CSP reports can contain URLs and referrer data.** **Accepted (process,
   not code).** The app logs that collect `CspReport` warnings are operational
   telemetry and keep the same access limits as the rest of the application
   logs; no report content is rendered back into any page.
6. **Enforcement exit gate.** Formalized below — the flip stays a reversible
   per-app configuration change.
7. **`upgrade-insecure-requests` is deliberately absent**: the platform
   supports plain-HTTP intranet deployments, where the directive would break
   every non-TLS page. Unchanged by this campaign.

## Enforcement exit gate (ReportOnly: false)

The flip is an operator decision per app, taken only when all of this is true:

1. **Bake period.** The app has run with its final report-only policy (the
   exact `Policy` string it will enforce) for **at least 14 days** of normal
   traffic, covering every page type the app serves — not just the start page.
2. **Empty actionable log.** The app's `CspReport` log category
   (`OpenModulePlatform.Web.Shared.Security.CspReport` warnings in the app's
   own log file) shows **zero actionable violations** for the whole bake
   period. Actionable means the report's `blocked-uri`/`violated-directive`
   traces to platform or module markup, scripts, or styles. Reports caused by
   browser extensions, translation overlays, or `about:`/`chrome-extension:`
   URLs are not actionable but must be written down when dismissed, so the
   next reviewer does not re-triage them.
3. **Coverage proof.** A manual pass (or the Playwright smoke suite) visited
   every page family of the app during the bake — for the Portal: dashboard,
   notifications, messages list and thread, and every admin page.
4. **Flip mechanics.** Set `ReportOnly: false` in the app's
   `<Section>:SecurityHeaders:ContentSecurityPolicy` config and restart the
   app. The flip is config-only and reversible; keep the previous value ready.
   At flip time an operator may additionally pin `connect-src` to the
   deployment's exact WebSocket origin (point 1 above).
5. **After the flip.** Watch the same log category for 7 days with
   enforcement active; any new actionable violation flips the app back to
   report-only and re-enters triage.

## Verifying the policy (break test)

The middleware contract is covered by
`OpenModulePlatform.Portal.Tests/Security/OmpSecurityHeadersTests.cs`. To
prove a browser actually enforces a policy, run any app with a strict
configured policy and `ReportOnly=false`, then load a page carrying an inline
`<script>` and an external `<img>`: the browser console must show
"Refused to execute inline script ..." / "Refused to load the image ..."
and the app log must carry the matching `CspReport` warnings. This was
executed against the Portal during the initial rollout (2026-09-01).
