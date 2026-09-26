# Module Definitions

OMP artifact zips are immutable runtime packages. They must not be expected to
change OMP metadata, module-owned schemas, user-setting definitions, portal
entries, permissions, or other database state. Those database changes belong to
a versioned module definition document.

Modules with channel types must follow the [channel-type reconciliation contract](CHANNEL_TYPE_RECONCILE.md), including its module-definition integrity checklist.

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
- which versioned SQL steps release the module's own rows when the platform
  removes a host, an artifact or an app instance (`runtimeMaintenance`)

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

## Module SQL safety guard

Module SQL owns module metadata. Artifact registration, artifact selection, and
configuration continuity are owned by the platform. The three
`ValidateSafeModuleDefinitionSql` entry points (HostAgent zip/folder import,
Portal repair/import, and Bootstrapper) enforce the same configuration rule
using the source-linked `shared/ModuleDefinitionSqlOwnership.cs`.

**Rule ID: `OMP-MODULE-SQL-CONFIG-OWNERSHIP`.** Module SQL must not write:

- `omp.ArtifactConfigurationFiles`
- `omp.ConfigOverlayDocuments`
- `omp.ConfigOverlayConfigurationFiles`

The rule uses Microsoft ScriptDom syntax trees and decoded identifiers. It
covers INSERT (including positional/default INSERT), UPDATE, DELETE and MERGE,
alias targets anywhere in a FROM/JOIN, chained CTE targets, OUTPUT INTO, and
SELECT INTO. BULK INSERT and the ALTER TABLE family are also checked, including
both the source and destination of SWITCH. Statement-form ENABLE/DISABLE TRIGGER,
ALTER INDEX, DROP INDEX (including legacy syntax), UPDATE STATISTICS and CREATE
STATISTICS also check the target table. DBCC DBREINDEX, CLEANTABLE, CHECKTABLE,
CHECKCONSTRAINTS and SHOW_STATISTICS check their literal table argument using the
same identifier rules. Nonliteral targets (including variables and numeric object
IDs) are rejected as unresolved dynamic SQL, as with sp_rename. DBCC CHECKDB and
SHRINKFILE remain outside this object-ownership rule because they have no table
target. EXEC/EXECUTE sp_updatestats and sp_createstats are blocked because their
global statistics maintenance affects platform-owned tables; qualified, delimited
and mixed-case procedure names are checked too. These checks also apply inside
constant EXEC strings, sp_executesql and stored procedure bodies.
Comments and ordinary string values are not
table identifiers. GO repeat counts are normalized from SQL tokens, preserving
source positions. Reads and
bounded schema maintenance on module-owned tables remain allowed.

Payload validation is fail-closed: unknown content encodings, invalid base64 or
UTF-8, missing or conflicting payload fields, malformed script entries,
unparseable SQL and unresolved dynamic SQL are blocked. `base64-utf8` content
is strictly decoded before analysis. A document is checked before its scripts
are selected or executed; errors name the module, script, rule, table and SQL
location when that location can be resolved.

Constant dynamic SQL is parsed recursively, including `sp_executesql`.
A single-assignment constant SQL variable is supported; reassignments make it
unresolved. Unknown values may only represent QUOTENAME-protected constraint
names in a parsed ALTER TABLE DROP CONSTRAINT statement with a fixed table.
Unknown dynamic DML targets are rejected. Configuration writes in procedure,
trigger and function definitions are checked too, including definitions in
constant dynamic SQL. There is no module-key or core-module exemption.

CREATE INDEX (including UNIQUE) is deliberately allowed because core bootstrap creates additive indexes through this same gate; blocking it requires moving bootstrap DDL to the compiled migration path first.

The existing artifact and pointer rules still block writes to `omp.Artifacts`,
`omp.AppInstances.ArtifactId` and
`omp.InstanceTemplateAppInstances.DesiredArtifactId`. Their legacy text
scanner and stored-body exceptions remain separate: for example,
`omp.MaterializeInstanceTemplate` legitimately writes artifact pointers.
The import fills artifact pointers through auto-apply. HostAgent and Portal
register artifacts before module SQL; Bootstrapper first installation registers
them afterwards, so module-owned metadata must tolerate absent artifact rows.

The offline analyzer does not query database metadata. It cannot discover
pre-existing views, synonyms or stored procedure implementations outside the
payload. Direct calls to named procedures retain their existing behavior.
Module SQL must not use such indirection to write platform-owned configuration.
Database-principal ownership options are described in
`docs/adr/0005-artifact-ownership-database-principal.md`.

The core overlay-history migration lives in compiled
`shared/PlatformConfigurationMigration.cs`, outside portable module SQL.
It also adds missing `PackageFileContent` and `MergeMode` columns to existing
configuration tables without changing their content. Fresh databases create
these columns directly in the table definitions.
Before core schema setup, each executor preserves the highest semantic overlay
version (then latest update and highest row id), disables duplicate enabled
siblings, and retains all historical rows. Fresh databases need no row
migration. The portable setup script then creates the filtered unique index.
Upgrade the HostAgent, Portal and Bootstrapper together with the new core
definition; running the schema SQL directly on an old database with duplicate
enabled overlays requires the platform-owned migration first.

`scripts/omp/Test-ModuleSqlGuards.ps1` runs the production validator through
`tools/ModuleSqlGuard`. By default it checks every embedded script declared
in `omp-components.json`; `-Path` accepts SQL or module-definition JSON.
The SQL source header is normalized exactly as during embedding, while source
line numbers are preserved. Analysis needs no database, VPN or network; normal
SDK/NuGet restoration is needed when building the tool on a fresh machine.

This is a mandatory gate beside module-definition and component-version
validation in `scripts/local-ci.ps1`, the pre-push hook and GitHub CI.
Universal export and HostAgent-first packaging also run the SQL and version
validators, including when reusing existing artifacts. The three
`ModuleDefinitionSqlSafetyTests` suites carry the same configuration matrix;
an initial run recorded 53 failures and four passing controls in each suite
before implementation. The PowerShell probe initially accepted a configuration
UPDATE and now rejects it with `OMP-MODULE-SQL-CONFIG-OWNERSHIP`.

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

The optional `consistentArtifactSets` array declares groups of artifacts that
must be deployed together at the same version. HostAgent evaluates these sets
after resolving the desired artifacts for a host and before deploying them.
Each set is scoped to one module instance and checked against the currently
applied module definition for that instance.

```json
"consistentArtifactSets": [
  {
    "setKey": "default",
    "description": "Mutually consistent web and service artifacts for this module.",
    "expectedArtifacts": [
      { "appKey": "example_web", "packageType": "web-app", "targetName": "example-web" },
      { "appKey": "example_service", "packageType": "service-app", "targetName": "example-service" }
    ],
    "versionMatchRule": "exact"
  }
]
```

`versionMatchRule` currently supports only `exact`: every matched set member
selected for the module instance must have the same version. HostAgent logs a
high-severity warning when a mismatch is detected. When `HostAgent:DeploySetConsistencyMode`
is set to `Block`, HostAgent refuses to deploy the inconsistent set. The default
mode is `Warn`. A module definition without `consistentArtifactSets` keeps the
previous behavior and performs no set check.

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
and therefore must provide every required section (based on a reference
consumer module):

```json
"artifactConfigurationFiles": [
  {
    "appKey": "example_module_web",
    "packageType": "web-app",
    "targetName": "example-module-web",
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

## Runtime maintenance steps

Some platform operations remove rows that module-owned tables reference: an
orphan host is deleted, an artifact is deleted, or an app instance is deleted.
The platform does not know a module's tables, so a module that keeps such
references declares how its own rows let go of them. The optional
`runtimeMaintenance` section holds versioned SQL steps per platform event.
HostAgent and Portal run the declared steps generically, in the same
transaction as the platform delete and before the platform rows are removed.
When no applied definition declares a step for an event, nothing
module-specific runs; there is no built-in fallback.

The steps are versioned with the definition: they live in the definition
document, are stored with it in `omp.ModuleDefinitionDocuments`, and take effect
only when a definition with a newer `definitionVersion` is applied. The platform
uses the latest applied definition per module, the same one the artifact
compatibility checks use.

### Events

| Event | Parameter | Execution | Run by |
| --- | --- | --- | --- |
| `host-removed` | `@HostId uniqueidentifier` | `idempotent` | HostAgent orphan-host cleanup, before the host's `omp.WorkerInstances`, `omp.AppInstances` and `omp.Hosts` rows are deleted. |
| `artifact-removed` | `@ArtifactId int` | `idempotent` | Portal artifact delete, before the `omp.Artifacts` row is deleted. |
| `app-instance-blocking-count` | `@AppInstanceId uniqueidentifier` | `read-only` | Portal app-instance delete, first. Reports module rows that cannot be unlinked; any count above zero refuses the delete. |
| `app-instance-removed` | `@AppInstanceId uniqueidentifier` | `idempotent` | Portal app-instance delete, after every blocking count was zero and before the instance's `omp.WorkerInstances` and `omp.AppInstances` rows are deleted. |

The platform binds exactly the one parameter of the event. Each step is sent
as a parameterized command, which SqlClient executes through `sp_executesql`;
step SQL never receives concatenated values. An `app-instance-blocking-count`
step returns one row with a `BlockingCount` column and may add a
`Description` column holding a text constant, which Portal uses to name the
rows in the refusal message ("2 example runtime binding(s)").

Steps run ordered by module key, then `order`, then `key`. A failing step fails
the platform operation and rolls the whole transaction back.

### JSON shape

```json
"runtimeMaintenance": {
  "steps": [
    {
      "key": "release-leases-on-host-removed",
      "event": "host-removed",
      "order": 10,
      "execution": "idempotent",
      "path": "examples/WebAppModule/Sql/runtime-maintenance/host-removed.sql",
      "inlineSql": null,
      "contentEncoding": "base64-utf8",
      "content": "...",
      "sha256": "..."
    }
  ]
}
```

| Field | Rule |
| --- | --- |
| `steps` | Required array when the section is present. The section also accepts an optional `description`; any other property is rejected. |
| `key` | Required, unique within the definition. |
| `event` | Required, one of the four events above. |
| `execution` | Required, must equal the event's execution (`idempotent` or `read-only`). |
| `order` | Optional integer, default `0`. |
| `description` | Optional text. |
| `path`, `source`, `inlineSql`, `contentEncoding`, `content`, `sha256` | Same meaning and decoding as in `sqlScripts`. `scripts/dev/embed-module-definition-sql.ps1` embeds the file named by `path`, and a package import resolves `path` into `inlineSql` exactly as for `sqlScripts`. |

Any other step property is rejected, so a misspelled field fails the import
instead of being ignored.

### Module schema

The schema that runtime maintenance steps may write is derived by the
platform from the module key and cannot be declared freely: it is always
`omp_<moduleKey>` (for `example_webapp`, `omp_example_webapp`). A definition
with `runtimeMaintenance` must declare `module.schemaName` equal to that
schema; any other value, including another module's schema or a platform
schema such as `omp_portal`, fails the import. The rule also requires:

- `moduleKey` of letters, digits and underscores, starting with a letter.
- A key that does not derive the schema of a platform-shipped module
  (`omp_core`, `omp_portal`, `omp_auth`, `omp_content`, `omp_iframe`).
- `moduleKey`, `module`, `module.schemaName`, `runtimeMaintenance` and the
  step properties each appear once. A repeated JSON property would be read
  differently by the import gate and by SQL `JSON_VALUE`.

When the steps run, the executor also checks the platform's registration of
the module. The stored document's `moduleKey` must equal its
`omp.ModuleDefinitionDocuments` row's key exactly. `omp.Modules` must register
the derived schema for that module, and no other module may register or be
named as that schema or differ from its key only in letter case. If any check
fails, the event is refused and no step runs.

### Runtime maintenance step grammar

Rule ID: `OMP-MODULE-RUNTIME-MAINTENANCE`. The shared validator
(`shared/ModuleRuntimeMaintenance.cs`) runs in every gate that already checks
`sqlScripts` - HostAgent zip/folder import, Portal apply/repair and the
Bootstrapper - through `ModuleDefinitionSqlOwnership.ValidateDocument`, in
`scripts/omp/Test-ModuleSqlGuards.ps1`, and again when a step is loaded for
execution, so a definition row edited in the database after import is refused
rather than run.

Step SQL is parsed with Microsoft ScriptDom (`TSql170Parser`) and checked
against an allow-list: every statement, clause and expression must be one of
the forms below, and anything the grammar does not name is refused. A second
pass refuses any syntax node whose type is not used by the grammar, so a
clause the structural check does not inspect cannot carry SQL through. The
check is the same at import and at execution.

```text
step      := guard { guard }                      -- one batch, no GO
guard     := IF OBJECT_ID(N'<schema>.<table>', N'U') IS NOT NULL body
                                                  -- exactly this condition; no ELSE
body      := statement
           | BEGIN { statement } END
statement := guard | delete | update | count

delete    := DELETE [FROM] <schema>.<table> WHERE where
update    := UPDATE <schema>.<table>
             SET <column> = constant { , <column> = constant }
             WHERE where
count     := SELECT COUNT(*) AS BlockingCount [ , N'<text>' AS Description ]
             FROM <schema>.<table> WHERE where    -- read-only event only

where     := conjunct { AND conjunct }            -- parentheses allowed;
                                                  -- at least one conjunct is a binding
binding   := <column> = @Param                    -- either operand order
           | <column> IN ( SELECT <a>.<column> FROM <schema>.<table2> <a>
                           WHERE inner )          -- one level
inner     := like where, with every column written <a>.<column>,
             at least one <a>.<column> = @Param, and no further subquery
filter    := operand <op> operand                 -- <op>: = <> != < > <= >=
           | <column> IS [NOT] NULL
           | <column> IN ( constant { , constant } )
conjunct  := binding | filter
operand   := <column> | constant
constant  := <number> | -<number> | N'<text>' | '<text>' | NULL
           | GETUTCDATE() | SYSUTCDATETIME()
```

- `<schema>` is always the module schema `omp_<moduleKey>` (see "Module
  schema"). Every table a statement names, including `<table2>` in a
  subquery, must be named by an enclosing guard; guards nest.
- `@Param` is the event's one parameter (`@HostId`, `@ArtifactId` or
  `@AppInstanceId`). It may appear only as the right- or left-hand side of a
  binding. It can never be assigned, compared with `<>`, or used in a `SET`.
- Outer columns are unqualified and belong to the written or counted table,
  which has no alias. Subquery columns are qualified by the subquery table's
  alias or name, which must differ from the written table's name, so a column
  missing from the inner table cannot silently resolve to the outer one.
- An `idempotent` event (`host-removed`, `artifact-removed`,
  `app-instance-removed`) contains at least one `DELETE` or `UPDATE` and no
  `SELECT`. The `read-only` event (`app-instance-blocking-count`) is exactly
  one `count` statement. `Description` is the only optional extra column,
  because Portal uses it to name the rows in the refusal message.
- Comments must not contain `@`: a parameter name in a comment reads like a
  binding to a reviewer but binds nothing.
- The configuration-ownership rule (`OMP-MODULE-SQL-CONFIG-OWNERSHIP`) applies
  as for every other module SQL.

Allowed, for example:

```sql
IF OBJECT_ID(N'omp_my_module.Leases', N'U') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'omp_my_module.Workers', N'U') IS NOT NULL
        UPDATE omp_my_module.Leases
        SET WorkerId = NULL, ReleasedUtc = SYSUTCDATETIME()
        WHERE WorkerId IN
        (
            SELECT w.WorkerId
            FROM omp_my_module.Workers w
            WHERE w.HostId = @HostId AND w.IsActive = 1
        );

    DELETE FROM omp_my_module.Leases
    WHERE HostId = @HostId AND ExpiresUtc < GETUTCDATE();
END;
```

Everything else is refused at import and at execution, including:

- `JOIN` or a second `FROM` on a write (`DELETE t FROM ... INNER JOIN ...`),
  and joins in any `SELECT`.
- `MERGE`, `INSERT`, `SELECT INTO`, `TRUNCATE`, DDL, `EXEC`, dynamic SQL,
  transaction control, `TRY`/`CATCH`, `SET` options (including
  `SET NOCOUNT`), `OUTPUT`, `TOP`, table and query hints.
- `DECLARE` of any variable, `SET @variable = ...` and
  `SELECT @variable = ...`, so the event parameter cannot be rebound.
- Common table expressions (`WITH ...`).
- `OR`, `NOT`, `NOT IN`, `EXISTS`, scalar subqueries and nested subqueries.
- Function calls other than `COUNT(*)` in the `count` form, `OBJECT_ID` in a
  guard, and `GETUTCDATE()`/`SYSUTCDATETIME()` as constants.
- Tables outside the module schema, including `omp` and `sys`, and
  cross-database or linked-server names.
- Several statements where any one of them is not an allowed form, for
  example a bound `DELETE` followed by an unbound one.

A module whose rows are keyed only by platform rows (for example a worker
instance id) cannot join `omp.WorkerInstances` to find them. Store the event's
key (`HostId`, `AppInstanceId`, `ArtifactId`) in the module's own table, or
keep a module-owned mapping table, and bind on that.

### Module key letter case

Two module keys that differ only in letter case derive the same physical
schema, because SQL Server schema names follow the database collation, and
under a case-insensitive collation the registration `MERGE` would update the
other module's `omp.Modules` row. Every import and registration path therefore
refuses a key that matches an existing `omp.Modules` or
`omp.ModuleDefinitionDocuments` key case-insensitively but not exactly (rule
`OMP-MODULE-KEY-CASE`, SQL error 53240): Portal definition save and apply,
Portal module create and edit, HostAgent definition import and apply, and the
Bootstrapper definition upsert and apply. At execution the stored document's
`moduleKey` must equal its row's key exactly, and a registered key that
differs only in case counts as another module claiming the schema.

### Failure isolation

The executor validates and binds each applied definition on its own. A
definition that cannot be read, validated or bound - whatever the error,
including a malformed `content` payload - is recorded as a failure of that
module. The event is then refused with one message that names every failed
module (`[<moduleKey>] <reason>`, separated by `|`), before any step runs, so
the platform delete that raised the event is aborted fail-closed and one
corrupt document never hides another module's failure. Nothing is known about
what a failed document declares, so it fails every event.

`scripts/omp/validate-module-definitions.ps1` and check 16 of
`scripts/omp/validate-component-versions.ps1` also require every step's
embedded content and `sha256` to match its `path` file, as for `sqlScripts`.

### Example

The example web app module (`examples/WebAppModule`) declares one step per
event against its own `RuntimeBindings` and `RuntimeLeases` tables. The SQL
files are under `examples/WebAppModule/Sql/runtime-maintenance/`:

```sql
-- host-removed.sql
IF OBJECT_ID(N'omp_example_webapp.RuntimeLeases', N'U') IS NOT NULL
    DELETE FROM omp_example_webapp.RuntimeLeases
    WHERE HostId = @HostId;

-- artifact-removed.sql
IF OBJECT_ID(N'omp_example_webapp.RuntimeBindings', N'U') IS NOT NULL
    UPDATE omp_example_webapp.RuntimeBindings
    SET ArtifactId = NULL
    WHERE ArtifactId = @ArtifactId;

-- app-instance-blocking-count.sql
IF OBJECT_ID(N'omp_example_webapp.RuntimeBindings', N'U') IS NOT NULL
    SELECT COUNT(*) AS BlockingCount,
           N'example runtime binding(s)' AS Description
    FROM omp_example_webapp.RuntimeBindings
    WHERE AppInstanceId = @AppInstanceId;

-- app-instance-removed.sql
IF OBJECT_ID(N'omp_example_webapp.RuntimeLeases', N'U') IS NOT NULL
    DELETE FROM omp_example_webapp.RuntimeLeases
    WHERE AppInstanceId = @AppInstanceId;
```

The tests `ModuleRuntimeMaintenanceTests` (validator matrix),
`OmpHostArtifactRepositoryRuntimeMaintenanceTests` (`host-removed`) and
`OmpAdminRepositoryRuntimeMaintenanceTests` (the three Portal events) run the
example definition against a local test database.

Some cleanup SQL for module tables is still hardcoded in the HostAgent and
Portal delete paths while modules move to declared steps. Those statements are
marked `transitional: removed once modules declare runtimeMaintenance` and
will be removed; new modules must use `runtimeMaintenance`.

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
