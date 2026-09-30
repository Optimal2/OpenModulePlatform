# Theme contract (light / dark)

Status: version 1, first slice. Implemented in the Portal, the Auth login page,
the shared top bar (Razor and Blazor), the content and iFrame modules and the
example modules' web apps (Razor, Blazor, service and worker). Consumer
repositories can adopt it with the steps below. The contract is
deliberately small so later steps can extend or change it; anything not listed
here is not part of it yet.

## What an app gets

- A System / Light / Dark menu in the shared top bar (and on the login page).
- The choice is stored once per host and applies to every OMP app on that host.
- The palette is set before the first paint, under the strict CSP
  (`script-src 'self'`, no inline script).
- Printed pages always use the light palette.

## The contract

On `<html>`, set by `omp-theme.js`:

| Attribute | Values | Meaning |
|---|---|---|
| `data-theme` | `light` \| `dark` | The palette in use. Style against this. |
| `data-theme-mode` | `system` \| `light` \| `dark` | The user's choice. `system` follows `prefers-color-scheme`, also while the page is open. |

`omp-theme.css` sets `color-scheme` to match, so native controls and scrollbars follow.
Without a stored choice the mode is `system`; without system detection the palette is `light`.

### Stored preference

Name `OMP_THEME_PREFERENCE`, value (JSON, URI-encoded in the cookie):

```json
{"version":1,"mode":"dark","revision":"<unique per change>"}
```

- Cookie: `Path=/`, `SameSite=Lax`, `Max-Age` one year, `Secure` on HTTPS, not
  `HttpOnly` (the script owns it). Host-only by default. A shared parent domain is
  opt-in: `<script src="…/omp-theme.js" data-cookie-domain="example.org">`, and is
  ignored when the page's host is not under that domain.
- `localStorage` under the same name is a mirror. The `revision` orders the two: it
  starts with the creation time, and on page start and on every re-read (focus,
  `pageshow`, the tab becoming visible, a `storage` event) the newest value wins and
  is copied to the other store. On a tie, or when a revision cannot
  be read, the cookie wins. So a mirror that is newer because a cookie write was
  silently dropped is not overwritten by the older cookie.
- If both are denied, the switch still works for the current page.
- A value with another `version` or an unknown `mode` is ignored.
- The script never reads or writes any other application's preferences
  (for example a document viewer's own settings).

Same-origin tabs sync through the `storage` event. Other ports/apps on the host
re-read the cookie on focus, `pageshow` and when the tab becomes visible.

### Tokens

`omp-theme.css` defines these custom properties for both palettes:

| Token | Use |
|---|---|
| `--omp-bg` | Page background |
| `--omp-surface`, `--omp-surface-muted` | Cards, panels, menus; muted variant |
| `--omp-text`, `--omp-text-muted` | Body text; secondary text |
| `--omp-link`, `--omp-link-hover` | Links and primary fills |
| `--omp-on-accent` | Text on a `--omp-link` fill (primary buttons, counters) |
| `--omp-focus` | Focus outline |
| `--omp-control-border` | Input and button borders (≥3:1 against the surface) |
| `--omp-border`, `--omp-border-strong` | Decorative separators |
| `--omp-accent-surface` | Selected/current rows, soft highlights |
| `--omp-shadow`, `--omp-overlay` | Elevation and modal backdrop |
| `--omp-{success,warning,danger,info}-{text,bg,border}` | Status messages, chips, banners |
| `--omp-{success,warning,danger,info}-bg-hover` | Hover/focus fill of a status-coloured control; text stays `-text` |
| `--omp-danger-surface` | A whole panel about a destructive action (delete zones); normal `--omp-text` on it |
| `--omp-danger-solid` | Solid fill of counters and badges (unread counts); text on it is `--omp-on-accent` |
| `--omp-series-{1..6}-{bg,border}` | Categorical colours that tell items apart without a status (message senders); text on them is `--omp-text` |

Rules:

- Style with tokens, never with a hard-coded colour that only works on one palette.
- Keep the app's existing variable names as aliases, with the light value as fallback:
  `--surface: var(--omp-surface, #ffffff);`. That is how the Portal migrated
  without renaming its selectors.
- Do not redefine `--omp-*` tokens in an app stylesheet; request a new token instead.
- Status text and background come in pairs; use both from the same family, and
  tokenise the background of every state (hover, focus, error) together with its text.

### JavaScript API

`window.ompTheme`:

- `getMode()` → `system | light | dark`
- `getTheme()` → `light | dark`
- `setMode(mode)` → stores and applies a choice
- `changeEvent` → `"omp:theme-changed"`, dispatched on `window` with
  `detail: { mode, theme, source }` where `source` is `user`, `system` or `storage`.

## Adopting it in an app

1. In the layout `<head>`, before the app's stylesheets, with PathBase-aware URLs:

   ```html
   <script src="~/_content/OpenModulePlatform.Web.Shared/js/omp-theme.js"></script>
   <link rel="stylesheet" href="~/_content/OpenModulePlatform.Web.Shared/css/omp-theme.css" />
   ```

   The script must be synchronous (no `defer`/`async`).
2. Alias the app's colour variables to the tokens and replace hard-coded colours.
3. The top bar menu appears automatically once `data-theme-mode` is set; without the
   script it stays hidden. A page without the top bar renders the menu with
   `@await Component.InvokeAsync("OmpThemeSwitch")` (Razor) or
   `<OpenModulePlatform.Web.Shared.Components.Layout.OmpThemeSwitch />` (Blazor; the
   Blazor top bar uses the same component). The shared top bar is off unless
   `PortalTopBar:Enabled` is set, so a layout that relies on it renders the menu
   itself when it is off (`@if (!WebAppOptions.Value.PortalTopBar.Enabled)`), never both.
   That includes views that hide the app's own header, such as the iFrame module's
   standalone view, which keeps a slim row with only the menu.
4. Test both palettes (see `OpenModulePlatform.UiTests/ThemeContractTests.cs`, and
   `ModuleThemeContractTests.cs` for a module web app), with the top bar on and off:
   switching, reload, System following `prefers-color-scheme`, and text contrast.
   Include the states a page shows only with particular data (error rows, group
   conversations, failed checks, the dashboard's edit menu, delete zones) and hover
   (buttons and table rows), not just the page at rest; the Portal tests insert each
   page's markup for those states into the live page. An icon-only control is
   measured through its `aria-label`, since its icon is drawn in the text colour.

### Contrast gate: 4.5:1

Text must reach WCAG AA for normal text, **4.5:1**, in both palettes and on hover.
The contrast test fails on anything below that, in the Portal, the Auth login page,
the platform's module web apps and the example modules alike.

The gate used to fail only below 3:1 (AA large text / non-text) and report the band
between 3:1 and 4.5:1 as warnings while hard-coded colours were migrated. Once those
reports were empty everywhere, the temporary gate and its warning reports were
removed. Consumer repositories that adopt the contract gate their own web apps at
4.5:1 too, with the shared scenarios; there is no 3:1 step left to fall back on.

## Menu markup and behaviour

```html
<div class="omp-theme-switch" data-omp-theme-switch>
  <button type="button" data-omp-theme-toggle aria-haspopup="menu" aria-expanded="false">…</button>
  <div role="menu" data-omp-theme-menu hidden>
    <button type="button" role="menuitemradio" data-omp-theme-option="system">…</button>
    <button type="button" role="menuitemradio" data-omp-theme-option="light">…</button>
    <button type="button" role="menuitemradio" data-omp-theme-option="dark">…</button>
  </div>
</div>
```

The markup has no state; the script marks `aria-checked` when the menu opens.
Enter/Space/ArrowDown open it, ArrowUp/ArrowDown/Home/End move, Enter chooses,
Escape closes; focus returns to the toggle. Behaviour is document-level event
delegation, so Blazor re-renders need no init call.

## Not in version 1

- Document viewer (ODV) synchronisation and its `normal` palette, including any
  `postMessage` bridge for cross-origin frames.
- Server-side rendering of `data-theme` (the head script does it before paint).
- Semantic chart colour series (the categorical `--omp-series-*` tokens are not
  tuned for charts).
