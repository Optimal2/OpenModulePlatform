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
permissions, setting definitions, portal entries, desired installation rows, and
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
   `TRUNCATE TABLE`, or executable `DELETE` statements without a `WHERE` clause.
   Referential-action clauses such as `ON DELETE CASCADE` are schema metadata
   and are not treated as executable `DELETE` statements by this guard.
5. HostAgent consumes the resulting desired state and deploys artifacts.

Each module definition file lives at the module root and is listed in that
repository's `omp-components.json`. For example, the Content web app module owns
`OpenModulePlatform.Web.ContentWebAppModule/content_webapp.module-definition.json`
next to its module source, and a standalone module repository can keep
`my_module.module-definition.json` directly in the repository root.

The HostAgent-first bootstrap package imports JSON files from
`data/global/module-definitions` after SQL initialization. That folder is a
generated package library, not the source layout. The package script copies
definition files from `omp-components.json` into the package library.
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
      "purpose": "Runtime configuration generated when a matching artifact is imported or deployed.",
      "requiredRootSections": ["Portal", "OmpAuth", "ConnectionStrings", "Logging"]
    }
  ],
  "sqlScripts": [
    {
      "key": "validate",
      "phase": "validate",
      "scope": "module",
      "order": 5,
      "path": "sql/validate.sql",
      "execution": "read-only",
      "inlineSql": null,
      "contentEncoding": null,
      "content": null,
      "sha256": "..."
    },
    {
      "key": "repair",
      "phase": "repair",
      "scope": "module",
      "order": 10,
      "path": "sql/repair.sql",
      "execution": "idempotent",
      "inlineSql": null,
      "contentEncoding": null,
      "content": null,
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

Module definition packages should be portable. The preferred source/package
shape is a zip containing one module definition JSON file and optional `.sql`
files referenced by `sqlScripts[].path`:

```text
example_module__module-definition__1.2.3.zip
  module-definition/example_module.module-definition.json
  sql/repair.sql
  sql/validate.sql
```

SQL files are optional. A module that only needs OMP module/app metadata and
artifact compatibility rows does not need a `sql` folder. When Portal or
HostAgent imports a package zip, each `sqlScripts` entry that has a `path` but
no embedded content is resolved inside the package, validated against `sha256`
when present, and normalized into `inlineSql` before the definition is stored in
`omp.ModuleDefinitionDocuments`. The source package stays reviewable while the
stored definition remains self-contained for later integrity checks and repairs.

Module-specific validation SQL is optional and should use `phase: "validate"`
or `execution: "validation"`. Validation SQL must be read-only and return one
result row. The first column, or a column named `IsHealthy`, is interpreted as
healthy when it is `1`, `true`, `ok`, `healthy`, `pass`, or `passed`; it is
interpreted as unhealthy when it is `0`, `false`, `error`, `unhealthy`, `fail`,
or `failed`. An optional `Message` column can explain the result in Portal.

Example validation script:

```sql
SELECT
    CAST(CASE
        WHEN EXISTS
        (
            SELECT 1
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = N'example_module'
              AND t.name = N'Configurations'
        )
        THEN 1
        ELSE 0
    END AS bit) AS IsHealthy,
    N'Example module schema check' AS Message;
```

If one or more module-specific validation scripts report unhealthy state,
Portal marks the module's non-validation idempotent SQL scripts as repairable.
HostAgent folder import uses the same rule: validation scripts decide whether
the idempotent repair/setup scripts need to run. Validation scripts are never
executed as repairs.

Standalone JSON uploads can still use `inlineSql`, `content`, or the historical
`contentEncoding: "base64-utf8"` form. Those forms remain supported for
backward compatibility, but new source packages should prefer external `.sql`
files. `path` remains as traceability back to the source package file. New
operator transport should use universal packages. Portal can download the stored
normalized JSON from each module-definition edit page so an applied definition
can be moved from one OMP installation to another.

The older `scripts/dev/embed-module-definition-sql.ps1` helper is retained only
for legacy JSON-only definitions. Portal executes repairs on the configured OMP
database connection, so module definition SQL must not switch databases.

The `compatibleArtifacts` array defines artifact slots, not already-installed
artifact rows. An artifact zip import uses these rows to validate
`appKey`/`packageType`/`targetName` and the optional minimum artifact version,
and to resolve `relativePathTemplate`. Do not set `maxVersion` to the latest
known build number as a release marker. Normal code-only artifact releases must
be importable and selectable without publishing a new module definition.
Use `maxVersion` only as a deliberate hard compatibility ceiling for a known
artifact line that must not move forward under the current module contract.
Applying only the module definition should not require the zip payload or create
a fake artifact version.

When a particular artifact build really requires a newer module definition
because SQL, OMP metadata, or another module contract changed, put that
requirement in the artifact package manifest as
`moduleDefinition.minVersion`. Portal and HostAgent then reject that artifact
until the required module definition has been applied, while ordinary artifact
releases remain independent.

Runtime binding rows are stricter than compatibility slots. `omp.AppInstances.ArtifactId`,
`omp.WorkerInstances.ArtifactId`, and `omp.InstanceTemplateAppInstances.DesiredArtifactId`
must point to an artifact owned by the same app and whose `PackageType` matches the app's
runtime `AppType` (`web-app` -> `Portal`/`WebApp`, `service-app` -> `ServiceApp`,
`worker` -> `Worker`, `host-agent` -> `HostAgent`, `worker-host` -> `WorkerHost`).
Metadata-only package types such as `channel-type` may still appear in `compatibleArtifacts`
and module-owned tables, but they are rejected for runtime bindings.

`artifactConfigurationFiles` describes runtime files that belong to an artifact
slot but should not live inside the immutable zip. These descriptors are also
applied when a concrete artifact exists; they are not proof that the artifact
payload has already been imported.

`requiredRootSections` is an optional array of JSON root keys that the final
resolved `appsettings.json` must contain after HostAgent has merged built-in
configuration and any overlay. When one or more of the listed sections are
missing, HostAgent still deploys the config but records a diagnostic warning
on the deployment result. The warning is persisted in
`omp.HostAppDeploymentStates.LastWarning` and shown as a yellow warning pill on
the Portal HostDeployments page so operators can fix the overlay before the app
fails at runtime.

Example for a module whose overlay replaces the entire `appsettings.json` file
and therefore must provide every required section (based on VajSkrivare):

```json
"artifactConfigurationFiles": [
  {
    "appKey": "vajskrivare_web",
    "packageType": "web-app",
    "targetName": "vajskrivare-web",
    "relativePath": "appsettings.json",
    "contentSource": "host-agent-generated",
    "purpose": "Complete runtime configuration generated or overlaid for the target host.",
    "requiredRootSections": [
      "Portal",
      "OmpAuth",
      "ConnectionStrings",
      "NLog",
      "PrinterDatabases",
      "ZebraConfig",
      "AuditLog"
    ]
  }
]
```

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
- OMP module, app, permission, worker definition, installation topology, and desired
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
   desired installation app rows to point to the imported versions.

With this split, a base OMP installation can be limited to the core `omp`
schema, Portal, and HostAgent. Remaining modules can then be added from Portal,
from a controlled installer, or from an import folder without coupling every
module to the base installer.

### Import is version-gated, not schema-gated

A common operator mistake is to assume that importing the latest artifact zip
automatically brings the database schema up to date. It does not. Artifact
import only creates or updates `omp.Artifacts` rows; it never executes
module-definition SQL. Schema changes are applied only when a module
definition with a **newer** `definitionVersion` is applied.

The version gate works like this:

- Portal and HostAgent record the latest applied `definitionVersion` per
  module. That version is the contract currently in force.
- When an artifact zip is imported, the import validates the artifact against
  the already-applied definition. It does not re-evaluate the module SQL
  scripts.
- If the SQL in the source repository has changed but `definitionVersion` was
  not bumped, the new SQL is present in the package but ignored, because the
  applied definition version is already equal or newer.

This means uploading a package with "all latest code" can still leave the
database schema behind if the definition version was already bumped in a
previous import. The safe operator mental model is:

> Uploading a package does NOT guarantee schema migration; it only runs SQL if
> the package's `definitionVersion` is newer than what is registered.

When schema is behind:

1. Verify the symptom in Portal (`/admin/moduledefinitions`) or with the
   module-specific validation script. Missing columns, unhealthy validation
   results, or runtime errors in module code can all indicate a stale schema.
2. Re-run the module's idempotent setup/repair SQL. For the OMP core schema
   this is `sql/1-setup-openmoduleplatform.sql`. Module-owned scripts are
   listed in the module definition's `sqlScripts` array.
3. After the SQL succeeds, refresh or re-apply the module definition so the
   applied version matches the intended contract and the integrity matrix
   shows green.

Because the setup script is idempotent, running it again is safe and is the
expected recovery path.

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
