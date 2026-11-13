# Context Guard Module

`OpenModulePlatform.Web.ContextGuardModule` is a first-party OMP web module for a controlled hand-off from a calling system into a target application. It replaces a desktop-specific one-window pattern with a browser flow: a new hand-off supersedes the previous context according to the configured scope, and an ended or superseded context is refused by the server.

The module trusts only the authenticated OMP principal and its `omp:user_id` claim. It does not contact an identity provider and has no opinion about how OMP authenticated the user.

## Flow

```text
Calling system                 Context Guard                  OMP Auth             Target application
      |                             |                              |                       |
      | POST /handoff/{profile}     |                              |                       |
      |---------------------------->| stage fields once, in memory |                       |
      |<---------303 /show?k=key----|                              |                       |
      |                             |-- login required ----------->|                       |
      |                             |<---------OMP cookie----------|                       |
      | GET /show?k=key             |                              |                       |
      |---------------------------->| redeem once; bind to OMP user|                       |
      |                             |-- form POST or GET in iframe------------------------>|
      |                             |                              |                       |
      | POST heartbeat + token      |                              |                       |
      |---------------------------->| current / superseded / ended |                       |
      | POST /handoff/{profile}/end |                              |                       |
      |---------------------------->| end matching correlation     |                       |
      |                             |-- iframe about:blank, then remove it                 |
```

The receive and end endpoints are anonymous in v1 because a cross-site form POST does not carry the `SameSite=Lax` OMP cookie. They explicitly disable antiforgery: they are inbound integration endpoints and cannot read a context or start one without an authenticated redemption. `/show`, heartbeat and state use the ordinary shared OMP authentication pipeline; heartbeat also validates the normal antiforgery token. Razor encoding is used for every rendered value.

The one-time key is random (256 bits), has a short TTL and is removed from the visible URL with `history.replaceState` after redemption. Redemption clears the stored payload. A reused, expired, ended or unknown key produces a named hand-off page, never another user's data. A redeemed key remains `AlreadyRedeemed` even after its TTL until its tombstone is pruned.

## Calling-system contract

Send `application/x-www-form-urlencoded` or `multipart/form-data` fields to:

```text
POST {PathBase}/handoff/{profileKey}
```

- The profile's correlation field is required and non-empty.
- Files are rejected. Field count, individual value length and total request size are bounded.
- Only `AllowedFieldNames` are retained. Unknown fields are dropped and counted.
- A full pending store returns `429`; an invalid form returns `400`, `413` or `415`.
- A successful call returns `303 See Other` to `/show?k=...`. The browser must follow it and sign in to OMP when challenged.
- `Method=POST` auto-submits a form into the iframe. `Method=GET` uses the same form mechanism and puts the allowed values in the target query string. Do not put secrets in GET fields: URLs can be retained by the target and intermediary logs.

To end the pending or live context when the calling program closes, send the same profile and correlation value to:

```text
POST {PathBase}/handoff/{profileKey}/end
```

The response is `204` whether or not a match existed, so the endpoint does not disclose context state. It can end but never start or read. An end call also cancels a matching not-yet-redeemed hand-off. Without this call, a web page cannot know that a desktop program closed. There is no separate calling-system heartbeat protocol in v1.

`Origin` or `Referer` host restrictions are prepared per profile. An empty `AllowedOriginHosts` list deliberately accepts hand-offs from anywhere. In plain words, any web page can then cause a logged-in user's current context to be replaced. The guard page therefore always shows the calling profile and correlation value. An empty or missing header is rejected when the list is non-empty.

## Settings

The root section is `ContextGuard`. Validation runs at startup; invalid profiles or numeric limits prevent startup.

| Setting | Default | Meaning |
| --- | ---: | --- |
| `HandoffTtlSeconds` | `60` | One-time key lifetime. A login involving a card/PIN or another deliberate second factor can take longer than 60 seconds; increase this only enough for that real login flow. Longer retention increases the time unredeemed context fields remain in process memory. |
| `HeartbeatIntervalSeconds` | `5` | Browser heartbeat interval. |
| `HeartbeatTimeoutSeconds` | `30` | Server-side context lifetime without a successful heartbeat. Must be at least twice the interval. Expiry is evaluated on context operations, not by a background timer. |
| `HeartbeatFailureLimit` | `3` | Consecutive failed browser heartbeats before the page blanks itself. |
| `MaxPendingHandoffs` | `100` | Process-local capacity for all hand-off entries, including payload-free redeemed, ended and expired tombstones. Full store returns `429`. Tombstones are pruned five minutes after the one-time-key TTL expires. |
| `MaxFieldCount` | `64` | Maximum total form values, including repeated names. |
| `MaxTotalFormBytes` | `65536` | Maximum form body size, including chunked requests where the server supports the per-request limit. |
| `MaxFieldValueLength` | `8192` | Maximum characters in one form value. |
| `Scope` | `user` | `user`: one context per OMP user. `user+client`: one per OMP user and module client cookie, so another workstation does not supersede. |
| `Profiles` | example | Dictionary of hand-off profiles. A real deployment must replace the example target and fields. |

Each `Profiles:{profileKey}` entry supports:

| Setting | Default | Meaning |
| --- | --- | --- |
| `DisplayName` | profile key | Neutral name shown on the guard page. |
| `TargetUrl` | required | Absolute HTTP(S) target URL, without user information. Its origin is added to the module's frame/form CSP. |
| `Method` | `POST` | `POST` or `GET` delivery of allow-listed fields. |
| `Delivery` | `embed` | `embed` hosts the target in the guarded iframe. `redirect` auto-submits to `_top` and leaves the guard page. |
| `AllowedFieldNames` | required | Case-sensitive allow-list. The correlation field must be included. |
| `CorrelationFieldName` | `correlation` | Field used for display and end-call matching. Only its SHA-256 hash is retained with live context metadata; the original value is rendered only on initial redemption. |
| `AllowedOriginHosts` | empty | Optional Origin/Referer host allow-list, without scheme, port or path. Empty accepts anywhere. |

The ordinary `Portal`, `OmpAuth`, `ConnectionStrings`, `OmpTime` and `NLog` sections remain host-owned. The module has no module SQL and no runtime migrations.

## Heartbeat and target state checks

The guard page calls `POST {PathBase}/context/{contextId}/heartbeat`. A superseded or ended answer, or the configured number of consecutive failures, makes the page navigate the iframe to `about:blank`, remove it and the delivery form, clear the in-page context/antiforgery values, show a neutral message and call `window.close()`. Browsers normally permit `window.close()` only for script-opened windows; **blanking is the guarantee, closing is best effort**.

A cooperating target can use the authenticated `GET {PathBase}/context/{contextId}/state` endpoint. It returns `current`, `superseded` or `ended`. Both state and heartbeat verify that the authenticated OMP user owns the context; another user's id is not disclosed. The target must preserve the OMP cookie and same-origin/CORS rules itself. The authoritative refusal is server-side, not the iframe JavaScript.

Superseded and ended context metadata is retained for three `HeartbeatTimeoutSeconds` periods so an existing guard page can still learn its terminal state. Store maintenance runs at most once per `HeartbeatTimeoutSeconds` period, while the requested context is checked for timeout on every state or heartbeat operation. After retention, the id returns `ended` without context metadata; it never becomes `current` again.

In `redirect` mode the browser leaves the module, so the module cannot blank or close the target. Heartbeat and state still refuse superseded ids for a cooperating target.

## Deliberate v1 limits

- **One instance per route.** Both stores are process-local. Pending payloads are never written to SQL or logs. Current-context records contain only user/client/context/profile ids, a correlation hash and timestamps. With multiple load-balanced instances, a hand-off key can be missing on the next request, supersede and end calls can affect a different process, and heartbeat state is not shared. Startup logs this limitation.
- **Accept from anywhere by default.** Configure origin hosts only when the calling systems are known; this is not caller authentication.
- **Redirect mode cannot blank.** Use embed mode when visual replacement is required.
- **`window.close()` is browser-controlled.** The blanked page and server refusal are the safety mechanism.
- **No desktop-close detection.** The calling system must send the end call, or the context ends only after heartbeat timeout/browser failure.
- **No payload persistence or distributed store.** Process restart loses pending and live state. A future shared implementation must preserve the no-SQL/no-log rule for raw hand-off fields.

## Install and configure

No package import or host restart is part of the repository change. An operator installs it through the normal OMP two-step model:

1. Apply `context_guard.module-definition.json` through Portal module-definition administration.
2. Build and import the `context-guard` web-app artifact (or the repository universal package) through the normal local packaging workflow. This creates no official product release.
3. Create/select exactly one active app instance for the route and bind the imported artifact. Do not enable multiple active instances for this v1 module.
4. Supply the host configuration/overlay. Replace the example profile with the real neutral profile key, target URL, method, delivery and allowed fields; include the normal OMP auth, connection and logging sections. Use HTTPS and the normal shared Data Protection setup.
5. Restart only the module's own host/app pool when deployment requires it, then point the calling system at `{moduleBase}/handoff/{profileKey}`.
6. Verify the anonymous login round trip, one-time reuse error, supersede behavior for the selected scope, and close-on-exit end call before production use.

See `OpenModulePlatform.Web.ContextGuardModule/appsettings.json` for the neutral example and `Packaging/appsettings.json` for the host-generated packaging template.

## Validation

- `OpenModulePlatform.Web.ContextGuardModule.Tests`: one-time redemption, TTL, capacity, field filtering and limits, both supersede scopes, heartbeat timeout/state, end behavior, options and proof that field/correlation values do not enter log messages.
- `OpenModulePlatform.UiTests/ContextGuardModuleTests.cs`: real loopback HTTP and Chromium on separate calling/guard origins: anonymous POST → login → exact iframe fields; same-user supersede and blanking; `user+client` isolation; reused/expired keys; end-call blanking.

Run the database-free browser suites, including these five tests, with:

```powershell
& "./scripts/local-ci.ps1" -IncludeUi
```
