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
   - Reseed-safe by default: repair/heal reruns against an operational install never re-enable rows an operator has retired, never rewrite operator-edited host fields, and never (re)create the packaged baseline artifact rows once other registered versions exist for the same artifact target. Set `@AllowBootstrapReseed = 1` in a manually edited copy only for a deliberate reseed; the override is announced with a loud `PRINT` in the execution log.

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

Each module owns its own setup and initialization scripts. The expected pattern
mirrors the root flow above, numbered prefix included:

0. `0-validate-<module>.sql`
   - Verifies that the module-owned schema, tables, and required columns exist.
     Every first-party module and every example module ships one; the only
     module without it is `OpenModulePlatform.Auth`, which creates no schema of
     its own (see below).

1. `1-setup-<module>.sql`
   - Creates only the module-owned schema and tables.

2. `2-initialize-<module>.sql`
   - Registers module/app definitions and seeds optional local/default data for that module.

Higher-numbered scripts are additive migrations applied in numeric order after
`2-initialize-*`. The Portal module is the one that currently has them:
`3-sync-omp-portal-entries.sql`, `4-ensure-topbar-hover-user-setting.sql`,
`5-ensure-dashboard-widgets.sql`.

First-party modules in the repository root and examples under `examples` follow
the same pattern under each module's own SQL folder. The folder name is not
uniform: `OpenModulePlatform.Portal/sql` and `OpenModulePlatform.Auth/sql` are
lowercase, while `OpenModulePlatform.Web.ContentWebAppModule/Sql`,
`OpenModulePlatform.Web.iFrameWebAppModule/Sql` and the four `examples/*/Sql`
folders are capitalized. Match the folder that already exists rather than
assuming one casing.

`OpenModulePlatform.Auth` is the exception to the pattern. It is platform
infrastructure rather than a user-facing module, so it owns no schema and has
only `2-initialize-omp-auth.sql`, which seeds the `/auth` web-app registration
rows into the core `omp` schema. It requires the root `1-setup-*` and
`2-initialize-*` scripts to have run first, and it is wired into installs
through `OpenModulePlatform.Auth/omp_auth.module-definition.json` and the
HostAgent-first packager (`scripts/deployment/package-hostagent-first.ps1`).

## Schema names

- `omp` is the core OpenModulePlatform schema. `OpenModulePlatform.Auth` also
  seeds into it rather than owning a schema.
- `omp_portal` is the OMP Portal module schema.
- `omp_content` is the first-party content module schema.
- `omp_iframe` is the first-party iframe module schema.
- `omp_example_*` schemas belong to optional example modules — currently
  `omp_example_serviceapp`, `omp_example_webapp`, `omp_example_webapp_blazor`
  and `omp_example_workerapp`.

Modules outside this repository own their schemas in their own repositories and
follow the same numbered pattern there (verified 2026-08-27):
`omp_ibs_packager` (IbsPackager), `omp_ikrock` (iKrock2), `omp_log_search`
(LogSearch), `omp_earkiv_checker` (EArkivChecker) and
`omp_earkiv_dokumentbibliotek` (Dokumentbibliotek). VajSkrivare is the second
exception alongside `OpenModulePlatform.Auth`: it owns no schema and its only
OMP script, `Sql/01_initialize_vajskrivare_metadata.sql`, seeds permissions into
`omp` — note that it also uses a different file-naming convention (`01_…`) than
the numbered pattern above.

## Core configuration settings

`omp.config_setting_definitions` stores the allowed setting keys. OMP upgrades
seed this table; it is not meant to be edited from the Portal admin UI. Each
definition can also provide `ValidationRegex` and `ExampleValues` metadata so
the Portal admin UI can reject clearly invalid values and show compact examples
near the value editor.

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
