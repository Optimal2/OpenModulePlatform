# Context Guard Module

`OpenModulePlatform.Web.ContextGuardModule` is a first-party OMP web module for a controlled hand-off from a calling system into a target application. It replaces a desktop-specific one-window pattern with a browser flow: a new hand-off supersedes the previous context according to the configured scope, and an ended or superseded context is refused by the server.

The module trusts only the authenticated OMP principal and its `omp:user_id` claim. It does not contact an identity provider and has no opinion about how OMP authenticated the user.

## Flow

```text
Calling system                 Context Guard                  OMP Auth             Target application
      |                             |                              |                       |
      | POST /handoff/{profile}     |                              |                       |
      |---------------------------->| stage fields once, in memory |                       |
      |<---------303 /show + cookie--| (one-time key parked, key-free address)   |
      |                             |-- login required ----------->|                       |
      |                             |<---------OMP cookie----------|                       |
      | GET /show (cookie or ?k=)    |                              |                       |
      |---------------------------->| redeem once; bind to OMP user|                       |
      |                             |-- form POST or GET in iframe------------------------>|
      |                             |                              |                       |
      | POST heartbeat + token      |                              |                       |
      |---------------------------->| current / superseded / ended |                       |
      | POST /handoff/{profile}/end |                              |                       |
      |---------------------------->| end matching correlation     |                       |
      |                             |-- iframe about:blank, then remove it                 |
```

The receive and end endpoints are anonymous in v1 because a cross-site form POST does not carry the `SameSite=Lax` OMP cookie. They explicitly disable antiforgery: they are inbound integration endpoints and cannot read a context or start one without an authenticated redemption. `/show`, heartbeat and state use the ordinary shared OMP authentication pipeline. A fully anonymous heartbeat/state caller receives the cookie authentication login challenge (`302` to the configured login path); an authenticated caller whose principal lacks the `omp:user_id` claim receives `403`. Heartbeat also validates the normal antiforgery token after authorization. Razor encoding is used for every rendered value. These machine-facing endpoints opt out of the host's HTML status-page re-execution so their documented status codes (400, 403, 404, 413, 415, 429) reach the caller unchanged.

The one-time key is random (256 bits) with a short TTL. The receive redirect parks it in a short-lived, `HttpOnly`, `SameSite=Lax` cookie scoped to the `/show` path and redirects to the key-free `/show` address, so the key never travels in the login `returnUrl`, address bars, server logs or browser history. The guard page consumes and deletes the cookie on first use and still accepts the `?k=` query form when no cookie is present (see "One-time-key transport" below). Redemption clears the stored payload and records an explicit redeemed state. A reused, expired, ended or unknown key produces a named hand-off page, never another user's data. A redeemed key remains `AlreadyRedeemed` even after its TTL, and a never-redeemed expired key remains `Expired` on repeat visits, until its tombstone is pruned or evicted.

## Calling-system contract

Send `application/x-www-form-urlencoded` or `multipart/form-data` fields to:

```text
POST {PathBase}/handoff/{profileKey}
```

- The profile's correlation field is required and non-empty.
- Files are rejected. Field count, individual value length and total request size are bounded.
- Only `AllowedFieldNames` are retained. Unknown fields are dropped and counted.
- A full pending store returns `429`; an invalid form returns `400`, `413` or `415`.
- A successful call returns `303 See Other` to the key-free `/show` address and sets the one-time-key cookie described above. The browser must follow it and sign in to OMP when challenged.
- `Method=POST` auto-submits a form into the iframe. `Method=GET` uses the same form mechanism and puts the allowed values in the target query string. Do not put secrets in GET fields: URLs can be retained by the target and intermediary logs.

To end the pending or live context when the calling program closes, send the same profile and correlation value to:

```text
POST {PathBase}/handoff/{profileKey}/end
```

The response is `204` whether or not a match existed, so the endpoint does not disclose context state. It can end but never start or read. An end call also cancels a matching not-yet-redeemed hand-off. Without this call, a web page cannot know that a desktop program closed. There is no separate calling-system heartbeat protocol in v1.

End-call validation, per profile: the correlation value must be at least `MinCorrelationLength` characters (default 16, so a short sequential case number is refused as an end key), otherwise `400`. If the profile configures `EndSecret`, the caller must also send it in the `endSecret` form field; it is compared in constant time, never logged, and a mismatch answers `403`. A profile with `EndEnabled=false` answers `404` on this route, indistinguishable from an unknown profile. Weakening the minimum below the safe default without setting `AllowWeakEndCorrelation=true` refuses host startup with an explicit error, as does configuring a secret on a disabled endpoint.

What remains possible for an anonymous party, by configuration:

| Configuration | What an anonymous caller can do |
| --- | --- |
| End enabled, no secret, default minimum | End any user's current context (and cancel any pending hand-off) for the profile when the ≥16-character correlation value is known; short sequential case numbers are refused. Rate-limited per remote address. |
| End enabled, with secret | Same, but only with the shared secret. A wrong or missing secret answers `403` without revealing whether anything matched; the secret itself is the guard, so distribute it only over a channel the anonymous party cannot read. |
| End enabled, weak minimum acknowledged | Same as the first row, except short guessable correlation values are also accepted as end keys. Only for calling systems whose case numbers are short; prefer a secret instead. |
| End disabled | Nothing: the route answers `404` and no anonymous end exists for the profile. |

Ending always matches across all users by profile plus correlation hash: the anonymous caller cannot identify a user, so the endpoint cannot scope the match to one. The guard page therefore always shows the calling profile and correlation value, so a replaced context is visible before the target loads.

`Origin` or `Referer` host restrictions are prepared per profile. An empty `AllowedOriginHosts` list deliberately accepts hand-offs from anywhere. In plain words, any web page can then cause a logged-in user's current context to be replaced. An empty or missing header is rejected (`403`) when the list is non-empty.

Both anonymous endpoints are rate-limited with the framework's fixed-window limiter, partitioned by remote address: `ReceiveMaxRequestsPerWindow` and `EndMaxRequestsPerWindow` requests per `AnonymousRateLimitWindowSeconds` seconds (defaults 60 per 60 s each). Excess answers `429`.

When `Portal:UseForwardedHeaders=true`, the module runs the shared ASP.NET Core forwarded-headers middleware before its rate limiter (the module maps endpoints before `UseOmpWebDefaults`). The middleware applies `X-Forwarded-For` only through the shared `ForwardedHeadersKnownProxies` / `ForwardedHeadersKnownNetworks` trust configuration, or the explicit trust-all setting, so callers behind a declared proxy receive separate per-client buckets instead of sharing the proxy's bucket. Declare the actual proxy addresses/networks; use trust-all only when a trusted proxy is guaranteed. When forwarded headers are disabled, a caller-supplied `X-Forwarded-For` is ignored and cannot create a fresh rate-limit bucket.

## Settings

The root section is `ContextGuard`. Validation runs at startup; invalid profiles or numeric limits prevent startup.

| Setting | Default | Meaning |
| --- | ---: | --- |
| `HandoffTtlSeconds` | `60` | One-time key lifetime. A login involving a card/PIN or another deliberate second factor can take longer than 60 seconds; increase this only enough for that real login flow. Longer retention increases the time unredeemed context fields remain in process memory. |
| `HeartbeatIntervalSeconds` | `5` | Browser heartbeat interval. |
| `HeartbeatTimeoutSeconds` | `30` | Server-side context lifetime without a successful heartbeat. Must be at least twice the interval. Expiry is evaluated on context operations, not by a background timer. |
| `HeartbeatFailureLimit` | `3` | Consecutive failed browser heartbeats before the page blanks itself. |
| `MaxPendingHandoffs` | `100` | Process-local capacity for hand-offs with a retained payload (staged fields). Full store returns `429`. Payload-free redeemed, ended and expired tombstones no longer consume this capacity. |
| `MaxHandoffTombstones` | `100` | Separate bound for payload-free tombstones (redeemed, ended, naturally expired). The oldest tombstone is evicted first when the bound is exceeded, so normal successful use can never starve new hand-offs. Entries of any kind are removed five minutes after the one-time-key TTL expires. |
| `MaxFieldCount` | `64` | Maximum total form values, including repeated names. |
| `MaxTotalFormBytes` | `65536` | Maximum form body size, including chunked requests where the server supports the per-request limit. |
| `MaxFieldValueLength` | `8192` | Maximum characters in one form value. |
| `AnonymousRateLimitWindowSeconds` | `60` | Fixed-window rate-limit window for the two anonymous endpoints. |
| `ReceiveMaxRequestsPerWindow` | `60` | Maximum receive (`POST /handoff/{profile}`) requests per window, per remote address. Excess answers `429`. |
| `EndMaxRequestsPerWindow` | `60` | Maximum end (`POST /handoff/{profile}/end`) requests per window, per remote address. Excess answers `429`. |
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
| `EndEnabled` | `true` | Set to `false` to switch the anonymous end endpoint off for this profile (`404`). |
| `MinCorrelationLength` | `16` | Minimum accepted end-key length. Below the safe default 16 requires `AllowWeakEndCorrelation=true`, otherwise startup fails. |
| `AllowWeakEndCorrelation` | `false` | Explicit acknowledgement that short end keys are accepted for this profile. |
| `EndSecret` | empty | Optional shared end secret, sent by the caller in the `endSecret` form field and compared in constant time. Empty means no secret is required. Setting a secret while `EndEnabled=false` fails startup. |

The ordinary `Portal`, `OmpAuth`, `ConnectionStrings`, `OmpTime` and `NLog` sections remain host-owned. The module has no module SQL and no runtime migrations.

## Heartbeat and target state checks

The guard page calls `POST {PathBase}/context/{contextId}/heartbeat`. A superseded or ended answer, or the configured number of consecutive failures, makes the page navigate the iframe to `about:blank`, remove it and the delivery form, clear the in-page context/antiforgery values, show a neutral message and call `window.close()`. Browsers normally permit `window.close()` only for script-opened windows; **blanking is the guarantee, closing is best effort**.

A cooperating target can use the authenticated `GET {PathBase}/context/{contextId}/state` endpoint. It returns `current`, `superseded` or `ended`. Both state and heartbeat verify that the authenticated OMP user owns the context; another user's id is not disclosed. The target must preserve the OMP cookie and same-origin/CORS rules itself. The authoritative refusal is server-side, not the iframe JavaScript.

Superseded and ended context metadata is retained for three `HeartbeatTimeoutSeconds` periods so an existing guard page can still learn its terminal state. Store maintenance runs at most once per `HeartbeatTimeoutSeconds` period, while the requested context is checked for timeout on every state or heartbeat operation. After retention, the id returns `ended` without context metadata; it never becomes `current` again.

In `redirect` mode the browser leaves the module, so the module cannot blank or close the target. Heartbeat and state still refuse superseded ids for a cooperating target.

## Memory bound

The hand-off store holds at most `MaxPendingHandoffs` entries with a retained payload plus at most `MaxHandoffTombstones` payload-free tombstones, each evicted oldest-first beyond its bound and all removed five minutes after the one-time-key TTL expires. With the defaults (100/100) the bound is 100 staged field sets — each limited by `MaxTotalFormBytes` — plus 100 small key/hash/timestamp records. No hand-off payload ever reaches SQL or logs; only profile key, counts, context ids and the correlation hash are logged, never field or correlation values and never the end secret.

## One-time-key transport and residual risks

The receive endpoint parks the key in a cookie (`.omp-context-guard-handoff`: `HttpOnly`, `SameSite=Lax`, path-scoped to `/show`, lifetime one-time-key TTL plus five minutes) and redirects to key-free `/show`. Its `Secure` flag follows the OMP session cookie's `OmpAuth:CookieSecurePolicy`: the default `always` marks it `Secure` even when the backend request is HTTP, while `sameAsRequest` follows the request's effective HTTPS scheme after any enabled forwarded-headers processing. The module client cookie and hand-off-cookie deletion use the same policy. `/show` prefers the parked cookie, consumes and deletes it on first use, and falls back to `?k=` only when no cookie is present. The login `returnUrl` is therefore key-free.

Residual risks, stated plainly:

- One parked key per browser: starting a second hand-off in the same browser before redeeming the first overwrites the parked cookie, so the first tab then redeems the second hand-off instead (or shows `unknown key` if that key is already used). Complete one hand-off before starting the next in the same browser.
- The `?k=` query form is still accepted, so an operator can complete a hand-off whose parked cookie was lost between receive and login (for example cleared cookies). The module itself no longer emits key-bearing URLs, and `history.replaceState` still strips any query that does arrive.
- A browser that refuses cookies cannot log in (the OMP cookie is required), so no cookie-less end-to-end flow exists; such a browser lands on the named hand-off error page either way.

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

- `OpenModulePlatform.Web.ContextGuardModule.Tests`: one-time redemption (including concurrent redemption yielding exactly one success), TTL with expired-vs-reused tombstone reasons, pending-only capacity with bounded oldest-first tombstone eviction, field filtering and limits, both supersede scopes, heartbeat timeout/state, end-request checks (length, secret, disabled) with proof that secrets and field/correlation values do not enter log messages, and option validation including the weak-end acknowledgement rule.
- `OpenModulePlatform.UiTests/ContextGuardModuleTests.cs`: real loopback HTTP and Chromium on separate calling/guard origins: anonymous POST → login → exact iframe fields; same-user supersede and blanking; `user+client` isolation; redeemed/expired keys; end-call blanking; key-free browser URLs; markup/script field values rendered inert in guard page and target; heartbeat refused without a valid antiforgery token; fully anonymous heartbeat/state callers challenged with `302` to login and authenticated callers without an OMP identity refused with `403`; short end correlation refused (`400`); secret-gated end (`403`/`204`); disabled end (`404`); disallowed origin refused (`403` at HTTP level); body/count/value limits refused (`413`); per-address rate limiting (`429`), including independent buckets for forwarded clients and spoofed-header refusal when forwarding is disabled; hand-off cookie `Secure` behavior for the default and `sameAsRequest` policies.

Run the database-free browser suites, including these five tests, with:

```powershell
& "./scripts/local-ci.ps1" -IncludeUi
```
