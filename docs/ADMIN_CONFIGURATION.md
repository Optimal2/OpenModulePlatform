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

The built-in auth app is mounted at `/auth`. AD sign-in goes through `/auth/ad`, and local password sign-in goes through `/auth/login`.

For the full authentication and RBAC model, see [`AUTHENTICATION_AND_RBAC.md`](AUTHENTICATION_AND_RBAC.md).

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

If a web app instance uses a relative `RoutePath` and runs on a different host than the Portal, set `Hosts.BaseUrl` to an absolute root URL that includes the scheme, host, and optional port.

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
