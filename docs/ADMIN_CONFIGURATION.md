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

- use `OMPSystem` only for the built-in `Everyone` and `AuthenticatedUsers` baseline roles created by OMP initialization
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

OMP users can be created from `/admin/users/create` before the person has ever
signed in with AD. The create page can also create an initial AD link, an
initial local password login, or both in the same transaction. Use this path for
users who should sign in through a known AD identity or through the built-in
`lpwd` provider. Existing OMP users can still get additional AD links or a local
login later from `/admin/users/edit/{userId}`. The edit page can remove AD links
and local logins, and it can reset the password for an existing local login.
Removing all authentication links is allowed, but the user cannot sign in again
until another link is added.

The Portal role editor currently exposes `OmpUser` and `ADUser` as addable
principal types. Existing `ADGroup` assignments still resolve, but adding new AD
group assignments is disabled in the editor until group selection has its own
workflow.

For broad app access, prefer adding permissions to the built-in `Everyone` or
`AuthenticatedUsers` roles instead of creating one role per app. These built-in
roles behave like baseline permission buckets: their permissions are added to
the user's active role at runtime, so a Portal admin can keep `PortalAdmins` as
the active role and still inherit general app access from `AuthenticatedUsers`.

`AuthenticatedUsers` defaults to any authenticated principal. To restrict it to
specific Windows account prefixes, set the `rbac/authenticatedUsersWindowsDomains`
configuration value from `/admin/configsettings`, for example `VGREGION` or
`VGREGION;OTHERDOMAIN`. Empty or `*` means no domain/workgroup/computer prefix
restriction.

The built-in auth app is mounted at `/auth`. AD sign-in goes through `/auth/ad`, and local password sign-in goes through `/auth/login`.

For the full authentication and RBAC model, see [`AUTHENTICATION_AND_RBAC.md`](AUTHENTICATION_AND_RBAC.md).

### Instance display names

The visible platform and portal names are installation settings, not literals in
the UI. The shared web layer reads the allowed setting keys from
`omp.config_setting_definitions` and the installation-specific values from
`omp.config_settings`:

- `branding/platformName` defaults to `OMP`
- `branding/portalName` defaults to `Portal`

Deployment configs can seed or update them through `ConfigSettings`. VGR uses
`EMP` as the platform name, while the local developer install can use `LOMP` to
make branding substitutions easy to verify. Technical identifiers, permission
names, schemas, cookies, and assembly names keep their stable OMP names.

Portal administrators can maintain the concrete values from
`/admin/configsettings`. The page intentionally does not allow editing the list
of available category/setting combinations; that list belongs to OMP SQL
upgrades so the UI only exposes settings that the runtime code understands.

### Portal dashboard

The Portal start page includes a user-customizable dashboard. The dashboard is
defined in the Portal schema and is intentionally separate from the global
Portal navigation entries:

- `omp_portal.widgets` stores reusable widget definitions
- `omp_portal.widget_permissions` optionally limits a widget to a role or
  permission; widgets without rows in this table are available to every signed-in
  OMP user
- `omp_portal.user_active_widgets` stores one user's placed widgets, including
  position, size, z-order, optional title, and small typed values
- `omp_portal.user_active_widget_data` is reserved for larger per-widget user
  data when a future widget needs more than the inline int/string fields

The current dashboard renderer supports Portal-owned widgets. `payload` selects
which server-rendered widget body is used; for example, `admin-overview` renders
the Portal administration metric cards and is restricted by
`OMP.Portal.Admin`. The client script only persists user layout state. Widget
definitions, permissions, and seeded widgets are owned by Portal SQL/module
definition upgrades.

Adding or removing a widget is saved immediately. Moving and resizing widgets is
saved when the user leaves dashboard edit mode with the Done button. Resetting
the dashboard removes the current user's active widget rows and does not change
the global widget definitions.

Portal administrators can import and export widget definitions from
`/admin/dashboardwidgets`. This lets module-specific widgets live beside the
module that owns them, including modules maintained in private repositories.
The portable JSON format is:

```json
{
  "format": "omp.portal.dashboard.widgets",
  "formatVersion": 1,
  "moduleKey": "example_module",
  "author": "Example module",
  "widgets": [
    {
      "widgetKey": "example:status",
      "title": "Example status",
      "widgetType": "portal",
      "payload": "example-status",
      "permissionNames": [ "Example.View" ],
      "roleNames": []
    }
  ]
}
```

`widgetKey` is the stable portable identity for a widget and must be globally
unique within one OMP installation. `moduleKey` is optional ownership metadata
used for filtering and export; it is not part of the widget identity. Re-importing
the same `widgetKey` updates the existing definition and replaces its permission
restrictions, even when the widget moves from module-owned to global or the
other way around. Use empty `permissionNames` and `roleNames` arrays only when
the widget should be available to every signed-in OMP user.

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

#### Uploading one artifact zip

Portal admins can upload a new artifact from `/admin/artifactupload`.
The page is intentionally scoped to one immutable artifact at a time. It
unpacks the zip into the configured central artifact store and creates the
matching `omp.Artifacts` row with the same directory-content SHA-256 that
HostAgent later verifies.

Configure the Portal with:

```json
"ArtifactUpload": {
  "ArtifactStoreRoot": "E:\\OMP\\ArtifactStore",
  "MaxUploadBytes": 536870912
}
```

`ArtifactUpload:ArtifactStoreRoot` should normally match
`HostAgent:CentralArtifactRoot`. The Portal refuses uploads when this root is
missing. The Portal runtime identity needs write access to this root; HostAgent
needs read access to the same files.

The supported filename metadata format is:

```text
moduleKey__appKey__packageType__targetName__version.zip
```

The separator is a double underscore because OMP keys commonly use single
underscores. If the uploaded filename matches this format, the form uses it to
pre-fill the app, package type, target, version, and default relative path. If
the filename does not match, the admin must fill the same fields manually.
Filename metadata is only a convenience; the form values are the values that
are saved.

The default relative path keeps deployment packages grouped by product root and
package-kind folder. Targets ending in `-web` or `-service` drop that suffix for
the root folder. Service targets ending in `-backend` use `backend` as the
package-kind folder, so a target such as `ikrock2-backend` defaults to
`ikrock2/backend/<version>`.

The upload page accepts both legacy artifact zips and the manifest-based OMP
artifact package format documented in [`ARTIFACT_PACKAGES.md`](ARTIFACT_PACKAGES.md).
Legacy zips are deployment-ready zips with files at the zip root. Subdirectories
are allowed, but paths must stay relative to the zip root:

- no rooted paths
- no `..` path segments
- no empty zip packages
- no wrapper-folder stripping
- no runtime configuration files such as `appsettings.json`,
  `appsettings.*.json`, or `odv.site.config.js`

Runtime configuration is intentionally outside immutable artifact payloads.
OMP-owned runtime files, such as database connection strings or app instance
identity settings, are written by the bootstrap/deployment layer. App-specific
files that must be deployment-owned, such as `odv.site.config.js`, belong in
`omp.ArtifactConfigurationFiles`. For normal releases, prefer an OMP artifact
package envelope with `omp-artifact-package.json`; it can carry both the
deployable payload and the configuration files with their relative deployment
paths. Manual configuration-file editing remains available for repair and
inspection.

The upload page blocks duplicate artifact content by comparing the extracted
directory-content SHA-256 with existing artifact rows. Zip metadata such as
timestamps and compression settings is intentionally ignored; two zip files
with the same deployable files count as the same artifact content.

If an uploaded zip targets the same app, package type, target, and version as
an existing artifact but the extracted content hash differs, the Portal upload
page requires an explicit replacement confirmation. That path is intended for
operator-controlled repair or correction of a bad artifact registration, not for
normal version progression. The automatic HostAgent import folder remains
stricter: invalid filenames, unknown app definitions, duplicate versions, and
content conflicts are moved to `failed` instead of asking questions.

When uploading a new version, the form can copy artifact-owned configuration
file rows from the latest previous artifact with the same app, package type and
target. This is intended for site-local runtime files such as ODV site config
that should normally follow the app across immutable artifact versions.
If the uploaded artifact package declares configuration files, those packaged
files are registered for the artifact and the previous-version copy is skipped.

The upload form also has an enabled-by-default option to use the uploaded
artifact immediately. When selected, Portal updates matching desired template
app rows and already materialized app rows to the new artifact. HostAgent then
provisions and deploys the version on its next cycle.

HostAgent can optionally import portable deployment objects from a local or
shared folder without a Portal upload. Configure this under
`HostAgent:ArtifactZipImport`; it is disabled by default. The import folder
accepts module definition JSON files, standard artifact package zips, and
module package zips that contain one module definition JSON plus one or more
artifact package zips for that module. Successful imports are moved to
`processed`, failed imports are moved to `failed` with an adjacent `.error.txt`
file, previous artifact configuration file rows are copied when available,
packaged configuration files are registered when the zip contains
`omp-artifact-package.json`, and matching apps are always set to use the
imported artifact.

Artifact-owned configuration files are managed from the artifact edit page.
These rows belong in `omp.ArtifactConfigurationFiles` and are optional. Use them
only for deployment-owned text files that should live beside the deployed app
but should not be packaged into the artifact zip. HostAgent writes enabled rows
after deployment and repairs files whose content differs from the database.
HostAgent expands OMP tokens in the stored text before writing the file, so a
single artifact configuration row can still write the correct runtime identity
for each host/app instance deployment. Use `{{Omp.AppInstanceId}}`,
`{{Omp.HostId}}`, or the JSON-safe forms such as
`{{Omp.Json.ConnectionStrings.OmpDb}}` when the value is inside a JSON string.
The edit page supports direct text editing, text-file upload, and zip upload.
Prefer zip upload in environments with strict request filtering that blocks
script-like text even in multipart file uploads. Zip uploads must contain
exactly one UTF-8 text file; Portal reads that file and stores its text content
in `omp.ArtifactConfigurationFiles`.

Typical examples:

- `odv.site.config.js` for OpenDocViewer site configuration
- deployment-specific JSON/XML/text files that are not part of the immutable app
  package

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
