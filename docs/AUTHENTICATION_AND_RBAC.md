# Authentication and RBAC

This document describes the shared OMP authentication model, user model, role-principal mapping, and RBAC resolution flow. Keep this file aligned with `OpenModulePlatform.Auth`, `OpenModulePlatform.Web.Shared`, and the `omp.*` security tables.

## Goals

OMP separates authentication from authorization:

- authentication proves who connected and issues a shared OMP cookie
- authorization resolves the authenticated identity to OMP roles and permissions
- external principals, especially large AD groups, can grant access without creating one `omp.users` row per external user
- first-class OMP users can still exist when the platform needs profile state, local password sign-in, or provider links

`OpenModulePlatform.Auth` is the server-side authentication broker for OMP. It
talks to Windows Authentication, local password sign-in, or OIDC identity
providers, then issues the same shared OMP cookie for the Portal and module web
applications. Normal OMP web apps do not receive identity-provider secrets or
tokens.

## Web Applications

`OpenModulePlatform.Auth` is mounted as the `/auth` IIS application.

Normal OMP web applications, including the Portal and module UIs, should use anonymous IIS access and validate the shared OMP cookie in application code. Shared cookie setup lives in `OpenModulePlatform.Web.Shared.Extensions.OmpWebHostingExtensions`.

The shared cookie uses:

- scheme: `OmpAuth`
- default cookie name: `.OpenModulePlatform.Auth`
- path: `/`
- shared Data Protection application name and key location from `OmpAuth`

### Session Lifetime

OMP Auth stores session lifetime in the global config setting
`auth/providerSessionLifetimes`. The value is a JSON object whose property names
are `omp.auth_providers.provider_id` values and whose property values are
session lifetime minutes. Provider id `0` is not a real provider; it is the
fallback used when the active provider has no provider-specific entry.

The seeded default keeps the historical 10-hour behavior:

```json
{ "0": 600 }
```

An installation can shorten one provider while leaving the fallback unchanged:

```json
{
  "0": 600,
  "2": 120
}
```

The setting is read from `/admin/configsettings` as a global
`auth/providerSessionLifetimes` row. Scoped user, permission, or role values are
not used by OMP Auth sign-in. Missing or empty values, invalid JSON, or a JSON
value that is not an object fall back to the built-in 600-minute default.
Invalid entries inside an otherwise valid JSON object are ignored; positive
values below 5 minutes are clamped to 5, and values above 10080 minutes are
clamped to 10080.

The lifetime is stamped into the OMP cookie during sign-in. Changing the setting
affects new sign-ins only; it does not rewrite or shorten existing sessions that
already have an issued cookie.

### Session Revocation and Absolute Lifetime

The session lifetime is absolute. Sliding renewal is disabled, so activity does
not push `ExpiresUtc` forward: when the stamped lifetime ends, the session ends
and the user signs in again. A re-sign-in that only refreshes claims (for
example saving account settings in the Portal) reuses the current ticket's
properties, so it cannot restart the clock either.

Every request that carries the shared cookie is re-validated against
`omp.users` by the `OnValidatePrincipal` hook in the shared cookie setup:

- the account must still exist and have `account_status = 1` (active);
- the `omp:security_stamp` claim in the cookie must still match the account's
  current `security_stamp`.

`omp.users.security_stamp` is a per-account value that is rotated when the
account is disabled (including the duplicate-account merge) and when its local
password is reset. Rotation ends every live session for that account: the next
request is rejected and the browser is sent to the login page. Cookies issued
before the stamp existed carry no claim and are signed out once at upgrade.

The verified account state is cached per user and application to keep the hook
from adding a database round trip to every request. Two global config settings
in the `auth` category tune the behavior:

- `auth/sessionRevocationCacheSeconds` (default `60`): how long a verified
  state may be cached before it is read again. This window bounds how quickly
  a disable or password change reaches an active session; the trade-off is
  revocation latency against database load (at most one read per user and
  application per window). `0` checks every request, values above 300 are
  clamped to 300.
- `auth/sessionRevocationFailureMode` (default `strict`): what happens when the
  account state cannot be verified at all, for example while the database is
  unavailable. `strict` rejects the session (fail closed; the user signs in
  again once the state can be read). `lenient` keeps the session until the
  next check (fail open, for installations that prefer availability). Missing
  or unreadable values behave as `strict`.

Unauthenticated users are redirected to `/auth/login?returnUrl=<local-path>`. Return URLs must be local absolute paths such as `/`, `/Admin/Rbac`, or `/SomeModule/`. Absolute URLs, protocol-relative URLs, and backslash paths are rejected.

The login page is localized for Swedish and English. Windows/AD sign-in is the
only primary visible option. Alternate Windows credentials and local password
sign-in are hidden behind the "Other sign-in options" disclosure and should be
used only when the normal AD flow cannot be used.

Logout is posted to `/auth/logout`. It always clears the shared OMP session cookie and the active role cookie, then redirects the user back to `/auth/login`.

When the current OMP session came from the configured OIDC / AD FS provider, OMP also invokes ASP.NET Core's standard OpenID Connect sign-out flow after clearing the OMP cookie. This is a best-effort federated logout redirect to the provider's published end-session endpoint. If the provider metadata does not expose an end-session endpoint, OMP completes local logout and returns to the login page.

For Windows Authentication, logout cannot sign the user out of Windows, clear browser Integrated Windows Authentication credentials, or end the operating-system session. A subsequent Windows sign-in may therefore authenticate silently again. To fully change Windows identity, the user must close the browser session, use another browser/profile, or sign out of Windows depending on the workstation and browser policy.

## Built-In Providers

### AD Provider

The built-in AD provider is exposed at `/auth/ad`.

This endpoint is the only built-in OMP endpoint that needs IIS Windows Authentication. It reads the Windows principal from IIS, resolves direct user identifiers and group identifiers, and then signs in with the shared OMP cookie.

The login page also has an alternate Windows/AD account prompt. It validates the
entered account through Windows account-management APIs before issuing the same
shared OMP cookie. The password is not stored by OMP. Use this
alternate-account path only on trusted local development hosts or over HTTPS.

The AD provider emits role principals for:

- `User` with the Windows account name for legacy compatibility
- `ADUser` with the Windows account name
- `ADUser` with the user SID when available
- `ADGroup` only for group SIDs or translated `DOMAIN\Group` names that already exist in `omp.RolePrincipals`

If an enabled `omp.user_auth` row links the AD provider identity to an active `omp.users` row, the cookie also includes the OMP user id.

The AD provider intentionally filters Windows group memberships before issuing the shared OMP cookie. A Windows identity can contain hundreds or thousands of AD groups, and writing all of them into the cookie can exceed IIS/HTTP header limits. The provider still enumerates the Windows groups during sign-in, but only persists the groups that are actually used by RBAC.

### OIDC / AD FS Provider

OMP Auth can optionally expose a server-side OpenID Connect provider for AD FS
or another OIDC identity provider. It is disabled by default and is configured
under `OmpAuth:Oidc`.

Supported settings include:

- `Enabled`
- `DisplayName`
- `ProviderName`, the `omp.auth_providers.display_name` value used for OIDC links
- `Authority` or `MetadataAddress`
- `ClientId`
- `ClientSecret`
- `CallbackPath`
- `Scopes`
- `ResponseType` set to `code`
- `ClaimTypes:ProviderUserKeyClaimType`
- `ClaimTypes:UserIdClaimType`
- `ClaimTypes:NameClaimType`
- `ClaimTypes:DisplayNameClaimType`
- `ClaimTypes:UserSidClaimType`
- `ClaimTypes:UpnClaimType`
- `ClaimTypes:SamAccountNameClaimType`
- `ClaimTypes:DomainClaimType`
- `ClaimTypes:GroupsClaimType`
- `ClaimTypes:GroupClaimTypes`
- `ClaimTypes:GroupSidClaimTypes`
- `ClaimTypes:GroupNameClaimTypes`
- `TranslateSidClaimsToAccountNames` (default `true`)
- `Diagnostics:Enabled` (default `false`)
- `Diagnostics:IncludeClaimValues` (default `false`)

The `DOMAIN\name` principal form is deliberately built from more than one
source, so a single misconfigured claim mapping cannot strip every AD-linked
role. The resolver reads `unique_name` and `windowsaccountname` (both the short
claim names and the full WS URIs) as user-principal candidates, and — when
`TranslateSidClaimsToAccountNames` is enabled — translates SID claims to their
account names on the domain-joined auth server, mirroring what the Windows
sign-in path already does in the opposite direction. Group claims are enriched
in both directions (SID to name and name to SID) so a role row matches
regardless of which form the provider sends. Translation is fail-safe: a failed
lookup is logged and skipped, never fails the sign-in, and results are cached
per sign-in so each distinct value costs at most one directory lookup.

Because the sign-in replaces the provider principal with OMP's own and tokens
are not saved, "the provider sent nothing", "wrong claim type configured" and
"right claim, wrong form" are otherwise indistinguishable in the logs. Setting
`Diagnostics:Enabled` to `true` makes each validated OIDC sign-in log the
incoming claim types, the value count per type, and the resulting role
principals. A sign-in that fails after validation (no configured provider user
key, or a matched but disabled user) logs the same incoming claim types and
counts, because those failures are exactly the cases the diagnostics exist to
tell apart. Raw claim values are only logged when `Diagnostics:IncludeClaimValues`
is also `true`, because values can contain personal data. Independently of
diagnostics, a configured AD-mapping claim type (`SamAccountNameClaimType`,
`DomainClaimType`, `UserSidClaimType` and the group claim-type lists) that was
absent from a validated or failed sign-in is logged as a warning once per claim
type per process.

OMP Auth uses the server-side confidential authorization-code flow. The
identity provider sends the browser back to the Auth application, the Auth
application redeems the code with the server-side client secret, validates the
`id_token`, maps configured claims to an OMP identity, and then issues the
normal shared `OmpAuth` cookie. The client secret is stored only in server-side
Auth configuration, normally an environment-specific protected config overlay
or secret store. It must not be placed in browser-side code, module web-app
configuration, public repositories, or generated documentation.

Tokens are not saved into the authentication ticket, exposed to browser-side
code, or forwarded to downstream OMP applications. OMP uses the identity claims
from the sign-in token and does not require an access token for downstream API
calls.

The OIDC sign-in endpoint is `/oidc` inside the Auth application. Because the
Auth application is normally mounted as `/auth`, the site-level path is
`/auth/oidc`. `CallbackPath` defaults to `/signin-oidc` inside the Auth
application, so the normal relying-party redirect URI registered in AD FS is:

```text
https://<omp-host>/auth/signin-oidc
```

Use the real external scheme, host, port, and path seen by the user's browser.
For local HTTP development, the equivalent default is
`http://localhost:8088/auth/signin-oidc`. A custom `CallbackPath` must start
with `/` and must not contain backslashes; invalid values fall back to
`/signin-oidc`. AD FS redirect URI comparison is exact, so mismatched HTTP vs
HTTPS, host aliases, ports, trailing paths, or reverse-proxy public URLs will
break the callback.

OIDC identities use the configured `ProviderName` in `omp.auth_providers` and
`omp.user_auth`; the default is `OIDC`. Use a stable provider name such as
`OIDC` or `ADFS` and keep it stable after links exist. The provider user key is
based on the issuer and configured `ProviderUserKeyClaimType` when an issuer is
present. For AD FS this should normally be a stable claim such as object SID,
`nameidentifier`, or `sub`, depending on the relying-party policy and whether
the value is stable across the identity lifetime. OMP also tries lookup aliases
for the subject, user name, SID, UPN, SAM/account name, and `DOMAIN\name`
candidates when those claims are present. If the OIDC identity is linked to a
disabled OMP user, sign-in is blocked instead of falling back to direct
principal claims.

The OIDC provider emits role principals for:

- `User` with the configured user-name claim
- `ADUser` with the configured user-name claim
- `ADUser` with useful AD-style user principal candidates when present, such as SID, UPN, SAM/account name, or `DOMAIN\name`
- `OIDCUser` with the provider user key
- `OIDCSubject` with the configured user-id claim
- `ADGroup` for every configured group claim value sent by the identity provider, including SID values and configured name values

OMP does not maintain a separate allow-list of OIDC groups. If AD FS sends broad
group claims, OMP can use those values directly as `ADGroup` role principals.
That keeps group ownership in AD FS/AD, but it also means the resulting OMP
cookie can grow with the number and length of group claims. Prefer compact group
identifiers or scoped group issuance rules at the identity provider when users
belong to many groups. AD FS policy may still decide which groups are emitted.
If all groups are emitted, AD FS administrators must consider identity-token,
cookie, and HTTP header size limits.

AD FS deployments sometimes require the `allatclaims` scope for desired claims
to appear in the `id_token` used by OMP Auth. Add it to `OmpAuth:Oidc:Scopes`
only when the AD FS relying-party policy requires it; OMP treats scopes as
configuration and always keeps tokens out of the shared OMP cookie.

AD FS group issuance is an identity-provider decision. Administrators can emit
all groups, selected groups, group SIDs, group names, or role-style claims. OMP
can consume any configured claim type and resolve the resulting values as
`ADGroup` role principals. OMP itself should not require a maintained
group allow-list for OIDC sign-in; a scoped AD FS claim issuance policy may
still be useful for token size, privacy, and auditability.

#### OIDC RBAC Examples

Prefer stable AD group SIDs when they are available. Names are easier to read
but can change when a group is renamed.

```text
Role: PortalAdmins
PrincipalType: ADGroup
Principal: S-1-5-21-1000000000-2000000000-3000000000-51234

Role: CaseReaders
PrincipalType: ADGroup
Principal: S-1-5-21-1000000000-2000000000-3000000000-62001

Role: RecordsOperators
PrincipalType: ADGroup
Principal: EXAMPLE\Records Operators
```

When AD FS emits group names instead of SIDs, configure
`ClaimTypes:GroupNameClaimTypes` with those claim names and create matching
`ADGroup` role principals. When AD FS emits SID values, configure
`ClaimTypes:GroupSidClaimTypes` or `ClaimTypes:GroupsClaimType` as appropriate
and use the SID values in RBAC.

#### Local OIDC Validation

Automated OMP tests do not require real AD FS, VPN, external network access, or
customer secrets. They cover:

- disabled and incomplete OIDC configuration hiding the provider without
  registering an OIDC authentication scheme
- local placeholder OIDC configuration registering the expected
  authorization-code, PKCE, claim-type, scope, and OMP-cookie boundaries
- claim mapping for AD FS-style user, SID, UPN, account-name, and group claims
- conversion from a resolved OIDC identity into the shared `OmpAuth` cookie
  principal without saving identity-provider tokens

If a developer wants an end-to-end local browser exercise, use a local-only OIDC
test server or simulator and register the Auth app redirect URI for the local
mount path, normally `http://localhost:8088/auth/signin-oidc`. Keep all values
local placeholders; do not use customer URLs, relying-party identifiers, or
secrets in this repository.

Example non-secret local placeholder configuration:

```json
{
  "OmpAuth": {
    "Oidc": {
      "Enabled": true,
      "DisplayName": "Local simulated AD FS",
      "ProviderName": "ADFS",
      "Authority": "https://idp.local.test/adfs",
      "MetadataAddress": "",
      "ClientId": "omp-local-auth",
      "ClientSecret": "<local-development-client-secret>",
      "CallbackPath": "/signin-oidc",
      "ResponseType": "code",
      "Scopes": [ "openid", "profile", "allatclaims" ],
      "ClaimTypes": {
        "ProviderUserKeyClaimType": "sub",
        "UserIdClaimType": "sub",
        "NameClaimType": "upn",
        "DisplayNameClaimType": "name",
        "UserSidClaimType": "objectsid",
        "UpnClaimType": "upn",
        "SamAccountNameClaimType": "samaccountname",
        "DomainClaimType": "netbiosname",
        "GroupsClaimType": "groups",
        "GroupClaimTypes": [ "roles" ],
        "GroupSidClaimTypes": [ "group_sid" ],
        "GroupNameClaimTypes": [ "group_name" ]
      }
    }
  }
}
```

`Authority` and `MetadataAddress` are mutually alternative ways to point the
middleware at local discovery metadata. With `Enabled` set to `true`, OMP Auth
requires one of those values, `ClientId`, `ClientSecret`, and
`ResponseType: "code"`. If the required values are incomplete, OMP Auth logs
that OIDC sign-in is disabled, hides the provider on the login page, and does
not register the OIDC challenge endpoint.

For HostAgent-managed installations, keep these settings in an Auth app
`appsettings.json` config overlay or another protected runtime configuration
source. The neutral artifact configuration only contains shared cookie and
database settings; environment-specific OIDC authority, client id, client
secret, callback path, scopes, and claim mappings belong outside the public
repository.

### Local Password Provider

The built-in local password provider uses provider key `lpwd` and table `omp.auth_provider_lpwd`.

Local password sign-in requires:

- an enabled `lpwd` row in `omp.auth_providers`
- a password hash in `omp.auth_provider_lpwd`
- an enabled `omp.user_auth` row linking the local user name to an active `omp.users` row

Raw passwords must never be stored. The current hash format is `PBKDF2-SHA256$<iterations>$<saltBase64>$<hashBase64>`.

LPWD login records are always authentication links to first-class OMP users.
They cannot exist as standalone platform users. Portal administrators can add a
local login while creating a user from `/admin/users/create`, or later from
`/admin/users/edit/{userId}`. Both add flows write `omp.auth_provider_lpwd` and
the matching `omp.user_auth` row in one transaction. The edit page can also
reset the local password hash or remove the local login; removal deletes both
the `omp.user_auth` link and the `omp.auth_provider_lpwd` row.

Local password user names have one canonical form shared by every write and
every read (R7-F12): `LocalPasswordIdentity.NormalizeUserName` (trim +
invariant lowercase) in application code, and a comparison pinned to the
binary collation `Latin1_General_100_BIN2` in SQL, so the database's default
collation cannot redefine matching. The core setup script carries an
idempotent migration (omp_core 0.3.117) that folds pre-existing
differently-cased `omp.auth_provider_lpwd` user names and their `lpwd`
`omp.user_auth` keys to the canonical form; rows whose canonical form would
collide with another row are left untouched for an operator to resolve
manually.

## User Tables

The core user tables are:

- `omp.users` - first-class OMP user records
- `omp.auth_providers` - enabled authentication providers
- `omp.user_auth` - provider identity links for OMP users
- `omp.auth_provider_lpwd` - local password provider data

`omp.user_auth.auth_status` stores the link status as open-ended text. OMP
currently creates `enabled` links and treats only `enabled` links as usable for
sign-in and AD-user role resolution. `disabled` and `deleted` are reserved
status values for administrative or automation workflows; additional status
values may be introduced without changing the table shape.

An OMP user row is required when the identity needs local password sign-in or durable OMP-owned user state. It is optional for AD identities that are only authorized through direct AD user or AD group role principals.

Portal administrators can manage first-class OMP users at `/admin/users`. The
admin UI can list, create and edit `omp.users` rows, optionally create an
initial AD link or local password login, add and remove authentication links,
and reset local passwords. AD links use the same provider display name (`AD`)
and provider user key formats that `/auth/ad` resolves, such as `DOMAIN\User`,
`name:DOMAIN\User`, or `sid:S-1-5-21-...`.

If a Windows identity matches an enabled `omp.user_auth` AD link to a disabled OMP user,
the auth app blocks sign-in instead of falling back to direct AD user/group role
principals. This keeps `account_status` authoritative for linked OMP users.

The core config setting `auth/externalUserProvisioningMode` controls whether
AD identities can be automatically promoted to first-class OMP users after a
successful sign-in. The default value is `Manual`, which preserves the manual
admin and self-service flows. `AutoIfRole` creates an active `omp.users` row
and AD `omp.user_auth` links during the same sign-in only when
the AD identity resolves at least one non-system role through `User`, `ADUser`,
or `ADGroup` role principals. The built-in `Everyone` and `AuthenticatedUsers`
roles do not count. AD group roles may trigger provisioning, but they are not
copied or migrated to the new OMP user; group-based access remains group-based
so removal from an AD group removes that access at the next login/session
refresh. A disabled linked OMP user still blocks sign-in and is never bypassed
by auto-provisioning. `AutoIfAuthenticated` creates and links an OMP user for a
successful AD sign-in when the identity is allowed to receive the built-in
`AuthenticatedUsers` principal according to
`rbac/authenticatedUsersWindowsDomains`. `IfRole` is accepted as an alias for
`AutoIfRole`, `IfAuthenticated` is accepted as an alias for
`AutoIfAuthenticated`, and the legacy `AutomaticForAuthorizedUsers` value is
accepted as an alias for `AutoIfRole`.

## User Settings

The Portal exposes `/account/settings` for the signed-in user. A signed-in AD
identity that is authorized by AD user or AD group principals, but does not yet
have an `omp.users` row, can create a first-class OMP user account from this
page. The self-service action creates an active `omp.users` row and links the
current AD account name (`DOMAIN\User`) in `omp.user_auth` in one transaction.
It does not create additional `name:` or `sid:` AD provider links. The current
session cookie is then refreshed with the new `omp:user_id` claim, while the AD
user/group principal claims remain in place.

After an OMP user exists, the settings page lets the user update
`omp.users.display_name`, which is core user state and updates
`omp.users.updated_at`.

Portal-specific user settings live under the Portal schema. Allowed settings are
registered in `omp_portal.user_setting_definitions`; per-user values are stored
in type-specific tables such as `omp_portal.user_setting_int_values` and
`omp_portal.user_setting_string_values`. This keeps high-volume user settings as
rows instead of adding a new column for every preference.

`Portal/AdminMetricsCollapsed` is an int-backed setting. `0` means the Portal
admin metrics panel starts expanded and `1` means it starts collapsed. A missing
value row falls back to the definition default (`0`), so default/false values do
not need to be stored for every user. Clicks on the panel do not auto-save in
this iteration.

Portal administrators can add, edit, and delete per-user Portal setting values
from `/admin/users/edit/{userId}`. The edit page only manages value rows for a
specific user; the allowed setting list is still controlled by
`omp_portal.user_setting_definitions` and should be changed through Portal SQL
upgrades.

## RBAC Tables

RBAC is stored in:

- `omp.Permissions`
- `omp.Roles`
- `omp.RolePermissions`
- `omp.RolePrincipals`

`omp.RolePrincipals` binds a role to a principal key. Common principal types are:

- `OMPSystem` - built-in platform principals such as `Everyone` and `AuthenticatedUsers`
- `OmpUser` - an OMP user id from `omp.users`
- `ADUser` - a Windows/AD account name or SID
- `ADGroup` - a Windows/AD group name or SID
- `LocalUser` - a local password provider user name
- `ServiceAccount` and `Host` - reserved for non-interactive or host-oriented assignments

Use `ADGroup` for large AD groups. OMP does not need to create `omp.users` rows for every AD group member.
Legacy `User` role-principal rows are migrated to `ADUser` by the core setup and initialization scripts.

Core initialization creates two built-in baseline roles:

- `Everyone`, bound to `OMPSystem|Everyone`
- `AuthenticatedUsers`, bound to `OMPSystem|AuthenticatedUsers`

These roles are intended for common baseline access, not for modeling one role
per application. For example, if an app should be visible to every authenticated
user, assign the app's view permission to `AuthenticatedUsers` instead of
creating a separate app-specific role.

Built-in baseline roles are ambient in runtime authorization. Their permissions
are added to the active role's permissions, but they do not replace the active
user role or clutter the role switcher when the user has normal roles such as
`PortalAdmins`.

`AuthenticatedUsers` can be limited to specific Windows account prefixes with
the core config setting `rbac/authenticatedUsersWindowsDomains`. The value is a
comma- or semicolon-separated list of allowed domain, workgroup, or computer
prefixes from `DOMAIN\User` style principals. Empty or `*` accepts any
authenticated principal.

`auth/externalUserProvisioningMode` accepts `Manual`, `AutoIfRole`, and
`AutoIfAuthenticated`. `IfRole` and `IfAuthenticated` are accepted as shorter
aliases. Invalid or missing values are treated as `Manual` by the
Auth app.

## Request Flow

1. A user opens an OMP web app.
2. The web app validates the shared OMP cookie.
3. If no valid cookie exists, the web app redirects to `/auth/login`.
4. The user selects AD, OIDC, or local password sign-in.
5. `OpenModulePlatform.Auth` authenticates the provider identity and emits OMP role-principal claims.
6. `RbacService` adds the built-in system principals that apply to the request.
7. `RbacService` resolves claims and system principals against `omp.RolePrincipals`, `omp.Roles`, and `omp.RolePermissions`.
8. Ambient baseline permissions plus the active role permissions control navigation and page access.

## Operations and Upgrade Notes

### Self-registration on upgraded installations

`auth/selfRegistrationEnabled` is opt-in: new installations seed it to
`false`, and every reader treats a missing, invalid, or unreadable value as
disabled. The seed only inserts the value (`WHEN NOT MATCHED THEN INSERT`)
and never overwrites an existing row, because an existing row may be a
deliberate operator decision. Installations that were seeded while the
default was `true` therefore keep running with self-registration switched on
after an upgrade — the new default does not reach them.

**Upgrade action:** if self-registration is not intended, set
`auth/selfRegistrationEnabled` to `false` from `/admin/configsettings` (or
directly in `omp.config_settings`).

The effective state is discoverable instead of silent:

- The Auth app logs a warning at startup while self-registration is enabled.
  Installations that allow self-registration deliberately can silence the
  warning with `OmpAuth:SelfRegistrationStartupWarning = false` in the Auth
  app's `appsettings.json`. The default is `true`, because a silently
  insecure default is exactly what this guard exists to catch.
- The authenticated `/runtime-versions` endpoint on the Auth app reports the
  effective state as `selfRegistration.enabled` (`true`, `false`, or `null`
  when the setting could not be read), with a `warning` text.

### Session verification failure mode: strict is fail-closed

`auth/sessionRevocationFailureMode` defaults to `strict` (see "Session
Revocation and Absolute Lifetime"): when the account state cannot be verified
at all — for example while the database is unreachable — **every**
authenticated session is rejected. In practice that is a platform-wide
sign-out for as long as the database is down. It is the right default for
sensitive data, but operations should decide in advance, not discover it
during an outage: `lenient` is a deliberate degradation valve that keeps
active sessions until the account state can be read again.

### One-time sign-out when the session security stamp rolls out

The release that introduced `omp:security_stamp` validation signs out every
active session once: cookies issued before the stamp existed carry no claim
and are rejected on the first request after the upgrade. Plan that rollout as
a universal re-login event, not a transparent upgrade.

### Client IP behind a reverse proxy (login throttle prerequisite)

The login throttle keys its per-client lockout bucket on the client IP: 50
failed attempts per client within 15 minutes lock that client out for 15
minutes (per-account throttling is 10 failures in the same window). When the
Auth app sits behind a reverse proxy, `WebApp:UseForwardedHeaders` (default
`false`) must be set to `true` with `WebApp:ForwardedHeadersKnownProxies` /
`WebApp:ForwardedHeadersKnownNetworks` restricted to the real proxy
addresses — see `docs/HOSTING_WINDOWS_IIS.md`. Without it every request
carries the proxy's address, the whole organization shares one lockout
bucket, and 50 failed logins from anywhere lock everyone out, including at
the login page.

### Local password user-name canonicalization collisions (R7-F12)

The canonicalization migration folds legacy `lpwd` user names to the
canonical (trimmed, lowercased) form but deliberately leaves rows whose
canonical form collides with another row — choosing which of two credential
sets survives is an operator decision, not a migration decision. The
migration prints the number of rows left unresolved; that output is visible
when the script runs through `sqlcmd` (the automated module-SQL import path
discards PRINT output). The leftovers can be checked at any time:

```sql
SELECT user_name
FROM omp.auth_provider_lpwd
WHERE user_name COLLATE Latin1_General_100_BIN2 <> LOWER(LTRIM(RTRIM(user_name)));

SELECT target.provider_user_key
FROM omp.user_auth target
INNER JOIN omp.auth_providers ap ON ap.provider_id = target.provider_id
WHERE ap.display_name = N'lpwd'
  AND target.provider_user_key COLLATE Latin1_General_100_BIN2 <> LOWER(LTRIM(RTRIM(target.provider_user_key)));
```

Any returned row is a case-fold collision: decide which credential set
survives, then remove or rename the other row by hand.

## Administration Guidance

For individual platform users, prefer `OmpUser` role principals once the user exists in `omp.users`.
When an AD provider identity is linked to an OMP user through `omp.user_auth`,
use the `OmpUser` principal as the primary assignment target. Portal
administrators can move direct `ADUser` role assignments for linked AD keys to
the OMP user from `/admin/users/edit/{userId}`. This migration does not move
`ADGroup` assignments.

### Bulk AD-to-OMP principal move

For moving many rows at once there is a separate admin view at
`/admin/security/ad-principal-migration`
(`OpenModulePlatform.Portal/Pages/Admin/Rbac/AdPrincipalMigration.cshtml`,
linked from the roles list as "Move AD principals"). It lists every
`ADUser`/`User` role row, shows a **preview** of what each row would resolve to,
and executes only after an explicit confirmation. The move runs in a
serializable transaction and is idempotent (`WHERE NOT EXISTS`), so re-running it
is safe; a concurrent run that deadlocks (SQL error 1205) is retried exactly
once rather than failing the page.

Two properties are deliberate and worth knowing before you use it:

- **Source rows are retained.** The move inserts the `OmpUser` assignment; it
  does not delete the `ADUser` row it came from. Cleaning those up is a separate
  decision.
- **`ADGroup` rows are never touched**, by the bulk move or by the per-user
  migration.

A row that cannot be moved is skipped with a stated reason rather than guessed
at. The reasons are `NoEnabledAdLink` ("no enabled AD link matches this
principal"), `AmbiguousLinkedUsers` ("the principal resolves to more than one
active OMP user"), `LinkedUserInactive`, and `UnsupportedPrincipalType`. The
preview also flags rows whose target OMP user has no non-AD sign-in link — after
the move that user's access still depends on the AD path.

### Ambiguous AD links fail closed

`AmbiguousLinkedUsers` is not a corner case, and the reason is the storage
model. Uniqueness on `omp.user_auth` is `(provider_id, provider_user_hash)`,
and the hash is a SHA-256 over the raw key — which makes it **case-sensitive**.
Two active AD links that differ only in letter case are therefore two distinct
rows, and they can point at two different OMP users.

Both the bulk move and the single-role editor (`Pages/Admin/Rbac/Role.cshtml`)
count *distinct active linked users* and abstain when more than one resolves,
reporting the ambiguity in the same wording. No role is ever assigned on a
guess. The role editor additionally offers a **preserve-literal** checkbox, so
an administrator who genuinely wants to store the exact `DOMAIN\name` principal
can suppress the `ADUser -> OmpUser` rewrite; when the rewrite does happen, the
result message names it and states the reason.

To find case-fold collisions before they surprise you, use the
`provider_user_key` collation query earlier in this document.

For customer or enterprise AD groups, prefer `ADGroup` role principals. This keeps group membership in AD and avoids synchronizing large groups into OMP.

For access that should apply broadly across applications, prefer the built-in
`Everyone` or `AuthenticatedUsers` roles. Keep app-specific roles for cases
where the role has a real business meaning beyond a single permission bundle.

The Portal role editor currently supports adding `OmpUser` and `ADUser`
principals directly. `OmpUser` suggestions come from active `omp.users` rows;
`ADUser` suggestions come from distinct existing `ADUser` rows in
`omp.RolePrincipals`. `ADGroup` remains part of the RBAC model and existing
assignments continue to resolve, but direct AD group assignment in the editor is
left disabled until group selection has a dedicated workflow.

For local password users, create the OMP user and local login together from
`/admin/users/create` when possible. For an existing OMP user, link the `lpwd`
provider identity through `omp.user_auth` from the edit page. Removing all
authentication links from a user is allowed, but that user cannot sign in again
until an administrator adds a new AD link or local login.

Keep provider-specific secrets and environment-specific bootstrap values out of public repositories.

## Troubleshooting AD Header Size

If `/auth/ad` fails with `HTTP Error 400. The size of the request headers is too long`, first determine where the rejection happens:

- If the error appears before the OMP auth app can sign the user in, IIS/HTTP.sys is rejecting the Windows Authentication header, often because the Kerberos token contains many AD group memberships. This happens before ASP.NET Core receives the request, so normal application logging may stay quiet.
- If the error appears after a successful AD challenge and redirect, the browser may be sending an oversized OMP cookie. The built-in AD provider filters AD group claims to avoid this, but old cookies may need to be deleted after upgrading.

On Windows/IIS hosts, HTTP.sys request header limits are controlled by the `HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters` registry values `MaxFieldLength` and `MaxRequestBytes`. Changing those values is an infrastructure decision and requires a restart of HTTP.sys or the server. Use the HTTPERR logs under `%SystemRoot%\System32\LogFiles\HTTPERR` to confirm `RequestHeadersTooLong` before changing host-level limits.

## Troubleshooting OIDC / AD FS

Use this checklist before changing code:

- Missing metadata: confirm `OmpAuth:Oidc:Authority` or
  `OmpAuth:Oidc:MetadataAddress` points to reachable discovery metadata from
  the OMP server. If both are empty while OIDC is enabled, OMP logs that OIDC is
  disabled due to invalid configuration.
- Bad client secret: confirm the server-side `ClientSecret` matches the AD FS
  confidential client/relying-party configuration. OMP never sends this value to
  the browser and does not store it in the shared cookie.
- Invalid redirect URI: confirm AD FS has the exact external redirect URI,
  normally `https://<omp-host>/auth/signin-oidc`. Check scheme, host, port,
  path, and reverse-proxy public URL handling.
- Missing group claims: inspect the `id_token` claims issued by AD FS and align
  `GroupsClaimType`, `GroupClaimTypes`, `GroupSidClaimTypes`, and
  `GroupNameClaimTypes` with the emitted claim names. Add `allatclaims` only
  when AD FS policy otherwise places the needed claims outside the `id_token`.
- Token or cookie too large: reduce emitted groups, prefer SID claims over long
  names, or scope the AD FS issuance policy. Very large identity tokens or OMP
  cookies can exceed browser, proxy, IIS, or HTTP.sys header limits.
- Clock skew: verify the OMP server and AD FS server clocks use reliable time
  synchronization. Skew can make otherwise valid tokens appear expired or not
  yet valid.
- SameSite or cookie issues: OMP's shared cookie is `SameSite=Lax` by default.
  Standard top-level OIDC redirects work with this setting. Embedded iframe or
  cross-site hosting scenarios may require HTTPS, `Secure`, `SameSite=None`,
  and matching frame/CSP configuration at the hosting layer.
