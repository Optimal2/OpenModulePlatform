# Module Definitions

OMP artifact zips are immutable runtime packages. They must not be expected to
change OMP metadata, module-owned schemas, user-setting definitions, portal
entries, permissions, or other database state. Those database changes belong to
a versioned module definition document.

## Purpose

A module definition document describes the database and metadata contract that a
module version expects. It is separate from deployable artifacts because one
module can contain several independently versioned apps, services, workers, or
plugins.

Applying a module definition must be useful even before runtime code has been
uploaded. The definition should be able to register the module, its apps,
permissions, setting definitions, portal entries, desired template rows, and
artifact slots in OMP. The actual `omp.Artifacts` rows are created later when
matching artifact zip files are imported.

The document answers these questions:

- which module key and apps are defined
- which SQL scripts are required, if any, and in what order
- which artifact slots are valid for this definition version
- which runtime configuration files are outside immutable artifacts
- which permissions, portal entries, app worker definitions, and setting
  definitions the module owns
- which other module definition versions must already be applied for this
  module to be valid
- which schemas, tables, and seed rows are required for integrity, and which
  SQL seed data is intentionally only sample/demo data

## Storage

OMP stores uploaded or generated definition documents in:

- `omp.ModuleDefinitionDocuments`
- `omp.ModuleDefinitionArtifactCompatibility`

`ModuleDefinitionDocuments.DefinitionJson` stores the normalized JSON document
and `DefinitionSha256` stores the SHA-256 hash of that normalized JSON. The
compatibility table stores the artifact-version ranges and relative artifact
path templates that Portal and HostAgent need to query without parsing JSON.

The first table is intentionally keyed by `ModuleKey` and `DefinitionVersion`
instead of `ModuleId`. A definition document may be uploaded before the module
row exists; applying the definition is the step that creates or updates module
metadata.

## Execution Ownership

Applying module definitions is a database-wide operation and should be
centralized. Do not let every HostAgent service on every server independently
execute module SQL. In a multi-host installation that creates race conditions,
unclear audit trails, and unnecessary database DDL privileges for host-local
agents.

Current ownership:

1. Portal or the bootstrapper uploads and validates the module definition.
2. Portal can validate the embedded SQL scripts and display which scripts have
   missing database objects, failed execution history, or no successful
   execution record for the current script hash.
3. The module-definition list shows an integrity matrix across all active
   definitions. The matrix checks OMP module/app metadata, required database
   objects, SQL execution/repair state, declared module dependencies, and
   current artifact compatibility.
4. A Portal administrator can then explicitly run the detected repairs. Portal
   only runs embedded scripts marked as `idempotent`, records the result in
   `omp.ModuleDefinitionSqlExecutions`, and blocks scripts that contain broad
   destructive operations such as `DROP TABLE`, `DROP SCHEMA`, `DROP DATABASE`,
   `TRUNCATE TABLE`, or `DELETE FROM` without a `WHERE` clause.
5. HostAgent consumes the resulting desired state and deploys artifacts.

Each module definition file lives at the module root and is listed in that
repository's `omp-components.json`. For example, the Content web app module owns
`OpenModulePlatform.Web.ContentWebAppModule/content_webapp.module-definition.json`
next to its module source, and a standalone module repository can keep
`my_module.module-definition.json` directly in the repository root.

The HostAgent-first bootstrap package still imports JSON files from its
package-local `module-definitions` folder after SQL initialization. That folder
is a generated import payload, not the source layout. The package script copies
definition files from `omp-components.json` into the package-local folder.
Protected/customer package configs can add module-definition files from module
repositories through `HostAgentFirst.AdditionalModuleDefinitionFiles`.

HostAgent may later help move module definition files into the database, but SQL
execution should still be guarded by a database lock or a central controller so
only one applier can run a definition version. Portal currently uses
`sp_getapplock` before executing module-definition repair SQL.

## JSON Shape

The document shape is versioned. Version 1 uses these top-level fields:

```json
{
  "formatVersion": 1,
  "moduleKey": "example_module",
  "definitionVersion": "1.2.3",
  "module": {
    "displayName": "Example Module",
    "moduleType": "WebApp",
    "schemaName": "example_module",
    "description": "Example module.",
    "sortOrder": 100,
    "isEnabled": true
  },
  "apps": [
    {
      "appKey": "example_module_web",
      "displayName": "Example Module Web",
      "appType": "WebApp",
      "description": "Example web app.",
      "sortOrder": 10,
      "allowMultipleActiveInstances": false,
      "isEnabled": true
    }
  ],
  "moduleDependencies": [
    {
      "moduleKey": "opendocviewer",
      "minDefinitionVersion": "2.0.3",
      "maxDefinitionVersion": null,
      "required": true,
      "reason": "This module consumes OpenDocViewer integration metadata."
    }
  ],
  "compatibleArtifacts": [
    {
      "appKey": "example_module_web",
      "packageType": "web-app",
      "targetName": "example-module-web",
      "relativePathTemplate": "example-module/web/{version}",
      "minVersion": "1.2.3",
      "maxVersion": null
    }
  ],
  "artifactConfigurationFiles": [
    {
      "appKey": "example_module_web",
      "packageType": "web-app",
      "targetName": "example-module-web",
      "relativePath": "appsettings.json",
      "contentSource": "host-agent-generated",
      "purpose": "Runtime configuration generated when a matching artifact is imported or deployed."
    }
  ],
  "sqlScripts": [
    {
      "key": "setup",
      "phase": "setup",
      "scope": "module",
      "order": 10,
      "path": "ExampleModule/Sql/1-setup-example-module.sql",
      "execution": "idempotent",
      "inlineSql": null,
      "contentEncoding": "base64-utf8",
      "content": "...",
      "sha256": "..."
    }
  ],
  "integrity": {
    "source": "Derived from the repository SQL scripts listed in sqlScripts.",
    "requiredSchemas": [ "example_module" ],
    "requiredTables": [
      {
        "schema": "example_module",
        "name": "Configurations",
        "source": "ExampleModule/Sql/1-setup-example-module.sql",
        "purpose": "Stores versioned module configuration."
      }
    ],
    "requiredOmpRows": {
      "permissions": [
        {
          "name": "ExampleModule.Admin",
          "description": "Administrative access to the example module."
        }
      ],
      "modules": [
        {
          "moduleKey": "example_module",
          "schemaName": "example_module"
        }
      ],
      "apps": [
        {
          "appKey": "example_module_web",
          "appType": "WebApp"
        }
      ],
      "appPermissions": [
        {
          "appKey": "example_module_web",
          "permissionName": "ExampleModule.Admin",
          "requireAll": false
        }
      ],
      "rolePermissions": [
        {
          "roleName": "PortalAdmins",
          "permissionName": "ExampleModule.Admin",
          "scope": "bootstrap-default-admin"
        }
      ],
      "moduleInstances": [
        {
          "moduleInstanceKey": "example_module",
          "instanceKey": "default"
        }
      ],
      "appInstances": [
        {
          "appInstanceKey": "example_module_web",
          "moduleInstanceKey": "example_module",
          "routePath": "example",
          "installationName": "example-module"
        }
      ]
    },
    "requiredModuleRows": {
      "configurationDefinitions": [],
      "portalEntries": []
    },
    "excludedSeedData": [
      {
        "source": "ExampleModule/Sql/2-initialize-example-module.sql",
        "reason": "Demo rows are useful for local smoke tests but are not required for module integrity."
      }
    ]
  }
}
```

Module definition documents should be portable. Keep normal `.sql` files in the
repository for reviewability, but embed the same script content in the JSON
document before it is uploaded or packaged. The recommended portable form is
`contentEncoding: "base64-utf8"` plus `content` and `sha256`. `path` remains as
traceability back to the source repository file. The standalone module
definition editor in `tools/module-definition-editor/index.html` exposes the
module app list and decodes SQL entries to clear text for editing. It writes
SQL back as base64 when the JSON is exported. Portal exposes the same
client-side editor for
administrators from `/admin/moduledefinitioneditor`. Portal can also download
the stored normalized JSON from each module-definition edit page so an applied
definition can be moved from one OMP installation to another.

Use `scripts/dev/embed-module-definition-sql.ps1` to refresh embedded SQL from
the source `.sql` files.

The `compatibleArtifacts` array defines artifact slots, not already-installed
artifact rows. An artifact zip import uses these rows to validate
`appKey`/`packageType`/`targetName`/version and to resolve
`relativePathTemplate`. Applying only the module definition should not require
the zip payload or create a fake artifact version.

`artifactConfigurationFiles` describes runtime files that belong to an artifact
slot but should not live inside the immutable zip. These descriptors are also
applied when a concrete artifact exists; they are not proof that the artifact
payload has already been imported.

`moduleDependencies` is optional. Use it when a module needs another module's
metadata, schemas, or runtime integration contract to be present. For example,
a document packaging module can require an OpenDocViewer definition version
when it produces links or payloads that the viewer must understand. Dependencies
are directional: the dependent module lists what it needs; the referenced module
does not need to list its consumers.

`allowMultipleActiveInstances` is optional and defaults to `false`. Keep the
default for ordinary portal/web apps where one active host-neutral or
host-specific row should describe the deployed app. Set it to `true` only for
definitions that intentionally create several active runtime rows for the same
app definition, such as one worker row per configured channel.

`sqlScripts` is optional. Autonomous modules can use an empty array when all
they need is OMP metadata plus runtime configuration supplied through normal
configuration-file mechanisms.

The `integrity` object is declarative. It describes the minimum database
contract that a later validator/repair tool can check without rereading every
SQL script. It should not try to serialize every column definition or every
environment-specific row. Keep it focused on durable requirements:

- module-owned schemas and tables
- OMP module, app, permission, worker definition, instance template, and desired
  runtime rows that make the module deployable
- module-owned setting definitions, channel type metadata, portal entry rows,
  or other seed rows that application code expects to exist
- explicit exclusions for sample jobs, local-only channels, smoke-test pages,
  and other data that normal installations can safely omit

Do not list `omp.Artifacts` rows as required integrity rows unless the specific
artifact version has already been imported. Module definitions describe which
artifact slots are allowed; artifact imports describe which immutable payloads
are present.

`relativePathTemplate` and similar template fields are preferred over fixed
versions. This keeps the definition stable across artifact patches while still
letting the artifact import resolve the concrete storage path.

Platform core definitions may use `"definitionType": "platform-core"` and omit
the `module` and `apps` sections when the document describes the neutral `omp`
schema itself rather than an installable module row.

## Compatibility Policy

Compatibility ranges are not release automation. They are guard rails for
operators and admin UI:

- Uploading a new artifact version does not imply the database definition is
  compatible.
- Applying a module definition does not automatically deploy a new artifact.
- Selecting a desired artifact should be allowed only when its module definition
  version is present and marked applied.

Portal artifact upload and HostAgent import-folder processing both validate
incoming artifact metadata against the latest applied definition for the module.
An artifact version that needs newer module SQL or metadata should therefore be
rejected until that module definition has been imported and marked applied.

## Two-Step Installation Model

The long-term installation model is intentionally split:

1. Apply the module definition. This creates or repairs OMP metadata and runs
   any required schema/module SQL, but it does not require runtime code to be
   available yet.
2. Import artifact zip files. The import validates each zip against the module
   definition's artifact slots, creates or updates `omp.Artifacts`, and allows
   desired app/template rows to point to the imported versions.

With this split, a base OMP installation can be limited to the core `omp`
schema, Portal, and HostAgent. Remaining modules can then be added from Portal,
from a controlled installer, or from an import folder without coupling every
module to the base installer.

## Portal Administration

Portal admins can upload module definition JSON files from
`/admin/moduledefinitionupload`. Uploaded definitions are stored as versioned
rows in `omp.ModuleDefinitionDocuments`; older versions remain available for
review. Applying a definition makes it the active contract for that module,
updates the module/app metadata described by the JSON, and refreshes the
compatibility rows used by artifact import.

Applying a definition can temporarily make currently selected artifacts
incompatible. Portal blocks that by default and shows the affected references.
The admin can explicitly allow the temporary mismatch when the next operational
step is to upload or select the matching artifact versions.

## Compatibility Example

Portal artifact `0.3.17` introduced the user preference
`Portal/TopbarDropdownsOpenOnHover`. Installing only that Portal artifact zip
updated application code but did not seed `omp_portal.user_setting_definitions`.
With module-definition compatibility in place, a Portal artifact that requires
that setting should be accepted only after the matching Portal module definition
has been imported and marked applied.
