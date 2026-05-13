# Manual administration configuration

## Purpose

The Portal is intended to be the normal path for manual OMP configuration.
After the SQL bootstrap has completed, an administrator should not normally need to edit core objects directly in the database.

## Recommended order of work

### 1. Verify RBAC

Start by confirming that the correct principal has Portal administrator access.

Important:

- set `@BootstrapPortalAdminPrincipal` when running bootstrap SQL directly, or use `scripts/manage-local-install.ps1 -BootstrapPortalAdminPrincipal` so the installer escapes it safely
- verify that the correct user, OMP user, provider principal, or AD group exists in `RolePrincipals`
- sign in to the Portal and confirm that the administration pages are available

`RolePrincipals` supports both first-class OMP users and external principals:

- use `OmpUser` when the role should follow a row in `omp.users`
- use `ADUser` for a direct Windows/AD user assignment
- use `ADGroup` for large AD groups that should grant access without creating `omp.users` rows for every member
- use `LocalUser` for identities authenticated by the built-in `lpwd` provider

Legacy `User` role-principal rows are migrated to `ADUser` by the OMP SQL setup
and initialization scripts.

When an AD account is linked to an OMP user, use the OMP user as the primary
role principal. Direct `ADUser` assignments for linked AD keys can be moved to
the OMP user from `/admin/users/edit/{userId}`. The migration only handles
direct `ADUser` assignments; `ADGroup` assignments are intentionally left as-is.

The Portal role editor currently exposes `OmpUser` and `ADUser` as addable
principal types. Existing `ADGroup` assignments still resolve, but adding new AD
group assignments is disabled in the editor until group selection has its own
workflow.

The built-in auth app is mounted at `/auth`. AD sign-in goes through `/auth/ad`, and local password sign-in goes through `/auth/login`.

For the full authentication and RBAC model, see [`AUTHENTICATION_AND_RBAC.md`](AUTHENTICATION_AND_RBAC.md).

### Instance display names

The visible platform and portal names are installation settings, not literals in
the UI. The shared web layer reads these rows from `omp.config_settings`:

- `branding/platformName` defaults to `OMP`
- `branding/portalName` defaults to `Portal`

Deployment configs can seed or update them through `ConfigSettings`. VGR uses
`EMP` as the platform name, while the local developer install can use `LOMP` to
make branding substitutions easy to verify. Technical identifiers, permission
names, schemas, cookies, and assembly names keep their stable OMP names.

### 2. Create or adjust the instance

An `Instance` is the highest manual scope in OMP.

It should normally exist before you add:

- hosts
- module instances
- app instances

### 3. Add hosts

Hosts belong to a specific `Instance`.

For a manual installation, this is part of the core runtime model.
Template-related host rows are not required to make the system function manually.

If a web app instance uses a relative `RoutePath`, Portal resolves it relative to `Hosts.BaseUrl` when that value is set. When `Hosts.BaseUrl` is empty, Portal assumes the app is reachable through the same public base URL as Portal. Set `Hosts.BaseUrl` to an absolute root URL, including scheme, host, and optional port, only when that host must resolve through a different public base URL.

### 4. Verify or create modules and apps

This is the definition level of the model.

- `Modules` describe module definitions
- `Apps` describe app definitions
- `Artifacts` describe deployable build outputs

### 5. Create module instances

This is where a module definition is placed into a concrete OMP instance.

### 6. Create app instances

This is the most important runtime level for manual operation.

At the `AppInstance` level you specify, among other things:

- which module instance it belongs to
- which host it runs on
- which app definition it represents
- which artifact it uses
- which route, path, or public URL applies
- which configuration applies
- which desired state and verification policy apply

## When the template and deployment pages matter

These areas mainly belong to automation and the future HostAgent flow:

- instance templates
- host templates
- template topology
- host deployment assignments
- host deployments

For a purely manual installation, those areas can usually be left alone.

## Practical guidance

- use the Portal for ongoing administration whenever possible
- use SQL mainly for the initial installation and controlled bootstrap work
- treat `AppInstance` as the central runtime unit
- avoid storing runtime data on `Modules` or `Apps`
