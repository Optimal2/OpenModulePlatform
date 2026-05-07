# Authentication and RBAC

This document describes the shared OMP authentication model, user model, role-principal mapping, and RBAC resolution flow. Keep this file aligned with `OpenModulePlatform.Auth`, `OpenModulePlatform.Web.Shared`, and the `omp.*` security tables.

## Goals

OMP separates authentication from authorization:

- authentication proves who connected and issues a shared OMP cookie
- authorization resolves the authenticated identity to OMP roles and permissions
- external principals, especially large AD groups, can grant access without creating one `omp.users` row per external user
- first-class OMP users can still exist when the platform needs profile state, local password sign-in, or provider links

## Web Applications

`OpenModulePlatform.Auth` is mounted as the `/auth` IIS application.

Normal OMP web applications, including the Portal and module UIs, should use anonymous IIS access and validate the shared OMP cookie in application code. Shared cookie setup lives in `OpenModulePlatform.Web.Shared.Extensions.OmpWebHostingExtensions`.

The shared cookie uses:

- scheme: `OmpAuth`
- default cookie name: `.OpenModulePlatform.Auth`
- path: `/`
- shared Data Protection application name and key location from `OmpAuth`

Unauthenticated users are redirected to `/auth/login?returnUrl=<local-path>`. Return URLs must be local absolute paths such as `/`, `/Admin/Rbac`, or `/SomeModule/`. Absolute URLs, protocol-relative URLs, and backslash paths are rejected.

Logout is posted to `/auth/logout`. It clears the shared OMP session cookie and the active role cookie, then redirects the user to `/auth/login`. For Windows Authentication, logout does not sign the user out of Windows, the browser, or the operating system. A subsequent Windows sign-in may therefore authenticate silently again.

## Built-In Providers

### AD Provider

The built-in AD provider is exposed at `/auth/ad`.

This endpoint is the only built-in OMP endpoint that needs IIS Windows Authentication. It reads the Windows principal from IIS, resolves direct user identifiers and group identifiers, and then signs in with the shared OMP cookie.

The AD provider emits role principals for:

- `User` with the Windows account name for legacy compatibility
- `ADUser` with the Windows account name
- `ADUser` with the user SID when available
- `ADGroup` only for group SIDs or translated `DOMAIN\Group` names that already exist in `omp.RolePrincipals`

If an `omp.user_auth` row links the AD provider identity to an active `omp.users` row, the cookie also includes the OMP user id.

The AD provider intentionally filters Windows group memberships before issuing the shared OMP cookie. A Windows identity can contain hundreds or thousands of AD groups, and writing all of them into the cookie can exceed IIS/HTTP header limits. The provider still enumerates the Windows groups during sign-in, but only persists the groups that are actually used by RBAC.

### Local Password Provider

The built-in local password provider uses provider key `lpwd` and table `omp.auth_provider_lpwd`.

Local password sign-in requires:

- an enabled `lpwd` row in `omp.auth_providers`
- a password hash in `omp.auth_provider_lpwd`
- an `omp.user_auth` row linking the local user name to an active `omp.users` row

Raw passwords must never be stored. The current hash format is `PBKDF2-SHA256$<iterations>$<saltBase64>$<hashBase64>`.

LPWD login records are always authentication links to first-class OMP users.
They cannot exist as standalone platform users. Portal administrators can add a
local login from `/admin/users/edit/{userId}`, which writes
`omp.auth_provider_lpwd` and the matching `omp.user_auth` row in one
transaction.

## User Tables

The core user tables are:

- `omp.users` - first-class OMP user records
- `omp.auth_providers` - enabled authentication providers
- `omp.user_auth` - provider identity links for OMP users
- `omp.auth_provider_lpwd` - local password provider data

An OMP user row is required when the identity needs local password sign-in or durable OMP-owned user state. It is optional for AD identities that are only authorized through direct AD user or AD group role principals.

Portal administrators can manage first-class OMP users at `/admin/users`. The
minimal admin UI can list, create and edit `omp.users` rows and add AD provider
links in `omp.user_auth`. AD links use the same provider display name (`AD`) and
provider user key formats that `/auth/ad` resolves, such as `DOMAIN\User`,
`name:DOMAIN\User`, or `sid:S-1-5-21-...`.

If a Windows identity matches an `omp.user_auth` AD link to a disabled OMP user,
the auth app blocks sign-in instead of falling back to direct AD user/group role
principals. This keeps `account_status` authoritative for linked OMP users.

## User Settings

The Portal exposes `/account/settings` for the signed-in user. A signed-in AD
identity that is authorized by AD user or AD group principals, but does not yet
have an `omp.users` row, can create a first-class OMP user account from this
page. The self-service action creates an active `omp.users` row and links the
current AD provider keys in `omp.user_auth` in one transaction. The current
session cookie is then refreshed with the new `omp:user_id` claim, while the AD
user/group principal claims remain in place.

After an OMP user exists, the settings page lets the user update
`omp.users.display_name`, which is core user state and updates
`omp.users.updated_at`.

Portal-specific user settings live under the Portal schema. `omp_portal.user_settings.admin_metrics_collapsed` controls only the default expanded/collapsed state of the Portal admin metrics panel on page load. Clicks on the panel do not auto-save in this iteration. A missing row, or `admin_metrics_collapsed = false`, means the admin panel is expanded by default.

## RBAC Tables

RBAC is stored in:

- `omp.Permissions`
- `omp.Roles`
- `omp.RolePermissions`
- `omp.RolePrincipals`

`omp.RolePrincipals` binds a role to a principal key. Common principal types are:

- `OmpUser` - an OMP user id from `omp.users`
- `ADUser` - a Windows/AD account name or SID
- `ADGroup` - a Windows/AD group name or SID
- `LocalUser` - a local password provider user name
- `User` - legacy Windows account name compatibility
- `ServiceAccount` and `Host` - reserved for non-interactive or host-oriented assignments

Use `ADGroup` for large AD groups. OMP does not need to create `omp.users` rows for every AD group member.

## Request Flow

1. A user opens an OMP web app.
2. The web app validates the shared OMP cookie.
3. If no valid cookie exists, the web app redirects to `/auth/login`.
4. The user selects AD or local password sign-in.
5. `OpenModulePlatform.Auth` authenticates the provider identity and emits OMP role-principal claims.
6. `RbacService` resolves those claims against `omp.RolePrincipals`, `omp.Roles`, and `omp.RolePermissions`.
7. The active role and effective permissions control navigation and page access.

## Administration Guidance

For individual platform users, prefer `OmpUser` role principals once the user exists in `omp.users`.

For customer or enterprise AD groups, prefer `ADGroup` role principals. This keeps group membership in AD and avoids synchronizing large groups into OMP.

For local password users, create the OMP user first, then link the `lpwd` provider identity through `omp.user_auth`.

Keep provider-specific secrets and environment-specific bootstrap values out of public repositories.

## Troubleshooting AD Header Size

If `/auth/ad` fails with `HTTP Error 400. The size of the request headers is too long`, first determine where the rejection happens:

- If the error appears before the OMP auth app can sign the user in, IIS/HTTP.sys is rejecting the Windows Authentication header, often because the Kerberos token contains many AD group memberships. This happens before ASP.NET Core receives the request, so normal application logging may stay quiet.
- If the error appears after a successful AD challenge and redirect, the browser may be sending an oversized OMP cookie. The built-in AD provider filters AD group claims to avoid this, but old cookies may need to be deleted after upgrading.

On Windows/IIS hosts, HTTP.sys request header limits are controlled by the `HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters` registry values `MaxFieldLength` and `MaxRequestBytes`. Changing those values is an infrastructure decision and requires a restart of HTTP.sys or the server. Use the HTTPERR logs under `%SystemRoot%\System32\LogFiles\HTTPERR` to confirm `RequestHeadersTooLong` before changing host-level limits.
