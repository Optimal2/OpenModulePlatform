# SQL layout

## Root OpenModulePlatform scripts

Use these scripts for the neutral OMP core installation flow:

0. `0-validate-openmoduleplatform.sql`
   - Verifies that the core `omp` tables, required columns, and runtime artifact compatibility guards exist.
   - Reports any invalid runtime artifact bindings in `omp.AppInstances`, `omp.WorkerInstances`, or `omp.InstanceTemplateAppInstances`.

1. `1-setup-openmoduleplatform.sql`
   - Creates the neutral `omp` schema and core platform tables.
   - Creates and migrates `omp.config_setting_definitions` and
     `omp.config_settings`, the core configuration tables for global, user,
     permission, and role scoped settings.

2. `2-initialize-openmoduleplatform.sql`
   - Seeds the default OMP instance, host/template baseline, RBAC baseline, built-in baseline roles, and bootstrap administrator principal.

3. `3-initialize-opendocviewer.sql`
   - Registers OpenDocViewer as a host-neutral OMP web app artifact target so
     HostAgent can deploy it like other web apps.

`2-initialize-openmoduleplatform.sql` requires a bootstrap administrator principal. Prefer the HostAgent-first installer because it writes a temporary SQL file with the principal escaped safely:

```powershell
.\scripts\deployment\update-installer-runner-only.ps1 -PackageRoot .\installer
.\installer\OpenModulePlatform.Bootstrapper.exe
```

The bootstrap JSON supports the bootstrap administrator principal and related
principal type. Use one clear principal per profile.

For direct SQL execution, manually replace `__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__` inside the initialization script with a single-quote-escaped Windows user or group. Do not pass the principal through `sqlcmd -v`; SQLCMD variables are textual substitution before T-SQL parsing and cannot be safely validated by the script after substitution.

Core initialization also creates the built-in `Everyone` and
`AuthenticatedUsers` roles. They are bound through `OMPSystem` principals and
are used by the shared RBAC runtime as ambient baseline roles.

## Module-owned SQL

Each module owns its own setup and initialization scripts. The expected pattern is:

1. `1-setup-<module>.sql`
   - Creates only the module-owned schema and tables.

2. `2-initialize-<module>.sql`
   - Registers module/app definitions and seeds optional local/default data for that module.

First-party modules in the repository root and examples under `examples` follow
the same pattern under each module's own `Sql` folder. The Portal module keeps
its SQL in `OpenModulePlatform.Portal/sql`.

## Schema names

- `omp` is the core OpenModulePlatform schema.
- `omp_portal` is the OMP Portal module schema.
- `omp_content` is the first-party content module schema.
- `omp_iframe` is the first-party iframe module schema.
- `omp_example_*` schemas belong to optional example modules.

## Core configuration settings

`omp.config_setting_definitions` stores the allowed setting keys. OMP upgrades
seed this table; it is not meant to be edited from the Portal admin UI.

`omp.config_settings` stores installation-specific configuration values as text
so a setting can hold simple scalars such as `true` or `10`, or serialized
JSON/XML when a module needs a richer value.

The logical setting identity is:

- `ConfigSettingId`, which points to an allowed
  `omp.config_setting_definitions` row
- optional `ConfigUsr`
- optional `ConfigPermission`
- optional `ConfigRole`

The table enforces uniqueness for that full combination. `NULL` scope columns
mean that the row is the global/default value.

Consumers should resolve competing rows in this order:

1. user scoped rows
2. permission scoped rows
3. role scoped rows
4. global rows

`ConfigScopeRank` is a persisted computed column for that order. Higher
`ConfigPriority` wins when more than one matching permission or role row exists
for the same setting. `ConfigId` is the deterministic final tie-breaker.

## Portal user settings

Portal user preferences are intentionally row-based. The Portal schema uses:

- `omp_portal.user_setting_definitions` for allowed setting keys and defaults
- `omp_portal.user_setting_int_values` for high-volume numeric/boolean values
- `omp_portal.user_setting_string_values` for string values

Default values should normally live on the definition row. User value tables
should store only values that differ from the default, so common defaults do not
create unnecessary rows for every OMP user.

## Module definition documents

`omp.ModuleDefinitionDocuments` stores versioned JSON documents that describe a
module's metadata and SQL contract. `omp.ModuleDefinitionArtifactCompatibility`
stores queryable app/package/version compatibility extracted from those
documents. See `docs/MODULE_DEFINITIONS.md`.
