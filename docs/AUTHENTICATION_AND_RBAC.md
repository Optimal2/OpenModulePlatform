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

## Built-In Providers

### AD Provider

The built-in AD provider is exposed at `/auth/ad`.

This endpoint is the only built-in OMP endpoint that needs IIS Windows Authentication. It reads the Windows principal from IIS, resolves direct user identifiers and group identifiers, and then signs in with the shared OMP cookie.

The AD provider emits role principals for:

- `User` with the Windows account name for legacy compatibility
- `ADUser` with the Windows account name
- `ADUser` with the user SID when available
- `ADGroup` with group SIDs and translated `DOMAIN\Group` names when available

If an `omp.user_auth` row links the AD provider identity to an active `omp.users` row, the cookie also includes the OMP user id.

### Local Password Provider

The built-in local password provider uses provider key `lpwd` and table `omp.auth_provider_lpwd`.

Local password sign-in requires:

- an enabled `lpwd` row in `omp.auth_providers`
- a password hash in `omp.auth_provider_lpwd`
- an `omp.user_auth` row linking the local user name to an active `omp.users` row

Raw passwords must never be stored. The current hash format is `PBKDF2-SHA256$<iterations>$<saltBase64>$<hashBase64>`.

## User Tables

The core user tables are:

- `omp.users` - first-class OMP user records
- `omp.auth_providers` - enabled authentication providers
- `omp.user_auth` - provider identity links for OMP users
- `omp.auth_provider_lpwd` - local password provider data

An OMP user row is required when the identity needs local password sign-in or durable OMP-owned user state. It is optional for AD identities that are only authorized through direct AD user or AD group role principals.

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
