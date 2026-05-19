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

The document answers these questions:

- which module key and apps are defined
- which SQL scripts are required, and in what order
- which artifact versions are compatible with this definition version
- which runtime configuration files are outside immutable artifacts
- which permissions, portal entries, app worker definitions, and setting
  definitions the module owns

## Storage

OMP stores uploaded or generated definition documents in:

- `omp.ModuleDefinitionDocuments`
- `omp.ModuleDefinitionArtifactCompatibility`

`ModuleDefinitionDocuments.DefinitionJson` stores the normalized JSON document
and `DefinitionSha256` stores the SHA-256 hash of that normalized JSON. The
compatibility table stores the artifact-version ranges that need to be queried
without parsing JSON.

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

Preferred ownership:

1. Portal or the bootstrapper uploads and validates the module definition.
2. A single central apply step runs the SQL scripts with a database identity that
   is allowed to change schema and metadata.
3. HostAgent consumes the resulting desired state and deploys artifacts.

HostAgent may later help move module definition files into the database, but SQL
execution should still be guarded by a database lock or a central controller so
only one applier can run a definition version.

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
      "appType": "web",
      "description": "Example web app.",
      "sortOrder": 10,
      "isEnabled": true
    }
  ],
  "compatibleArtifacts": [
    {
      "appKey": "example_module_web",
      "packageType": "web-app",
      "targetName": "example-module-web",
      "minVersion": "1.2.3",
      "maxVersion": null
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
      "inlineSql": null
    }
  ]
}
```

`inlineSql` may be used when the definition document must be fully
self-contained in one JSON file. Repository manifests should usually keep SQL in
normal `.sql` files and reference them by path so SQL remains readable and
reviewable.

## Compatibility Policy

Compatibility ranges are not release automation. They are guard rails for
operators and admin UI:

- Uploading a new artifact version does not imply the database definition is
  compatible.
- Applying a module definition does not automatically deploy a new artifact.
- Selecting a desired artifact should be allowed only when its module definition
  version is present and marked applied.

The current Portal and HostAgent flows do not enforce all of this yet. Until the
apply pipeline exists, any artifact update that depends on schema or metadata
changes must be accompanied by the relevant SQL patch or setup script rerun.

## Current Example

Portal artifact `0.3.17` introduced the user preference
`Portal/TopbarDropdownsOpenOnHover`. Installing only the Portal artifact zip
updates application code but does not seed `omp_portal.user_setting_definitions`.
Run `OpenModulePlatform.Portal/sql/4-ensure-topbar-hover-user-setting.sql` on
installations that received the Portal artifact without rerunning Portal setup
SQL.
