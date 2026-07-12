# ADR 0001: Typed `ModuleConfigId` bridge with opt-in validation

## Status

Accepted (2026-07-12)

## Context

OMP stores a module-owned configuration selection on two core tables:

- `omp.AppInstances.ConfigId` (`int NULL`)
- `omp.InstanceTemplateAppInstances.DesiredConfigId` (`int NULL`)

`DesiredConfigId` is propagated to `ConfigId` entirely inside SQL via
`MaterializeInstanceTemplate`. The selected value is **not** a foreign key to a
core table; it is a bridge to a module-owned `Configurations` table (for example
`omp_example_serviceapp.Configurations`). Because the target table is
module-owned, the core schema cannot enforce a foreign key relationship.

The column name `ConfigId` collides with the global `omp.config_settings.ConfigId`
primary key. The two identifiers are unrelated:

- `config_settings.ConfigId` is a global OMP setting identifier.
- `AppInstances.ConfigId` / `InstanceTemplateAppInstances.DesiredConfigId` is a
  module-owned configuration selection for a specific app instance.

Before this ADR the bridge was represented as raw `int?` throughout the C# code,
which made the semantic distinction easy to miss and offered no typed hook for
modules to validate a selected value.

## Decision

1. Introduce a typed value object `ModuleConfigId` in
   `OpenModulePlatform.Web.Shared`.
   - Wraps a single `int`.
   - Zero and negative values are invalid (all `ConfigId` columns use
     `IDENTITY(1,1)`).
   - Provides `FromNullable(int?)`, `ToNullable()`, `TryParse(string?, out)`,
     and an implicit conversion to `int` for backward-compatible SQL parameter
     binding.
2. Keep the SQL columns as `int NULL`. No schema change, no foreign key, and no
   `definitionVersion` bump.
3. Make validation **opt-in per module** via `IModuleConfigIdValidator`.
   - A module registers an implementation in its DI container when it wants to
     validate that a selected `ModuleConfigId` exists in its own
     `Configurations` table.
   - If no validator is registered, the bridge behaves exactly as today: any
     positive integer (or `null`) is accepted.
4. Update all core and example call sites that use the bridge to the typed form.
   Leave the global `config_settings.ConfigId` paths untouched.

## Consequences

- The bridge is now explicit in the type system, reducing the risk of confusing
  it with the global `config_settings.ConfigId`.
- Modules can add validation without forcing it on modules that do not need it.
- SQL parameters remain `int NULL`, so existing stored data and the
  `MaterializeInstanceTemplate` procedure are unaffected.
- The change is binary-affecting for all components that consume
  `OpenModulePlatform.Web.Shared`, so component versions must be bumped in
  lockstep.

## References

- `OpenModulePlatform.Web.Shared/Configuration/ModuleConfigId.cs`
- `OpenModulePlatform.Web.Shared/Configuration/IModuleConfigIdValidator.cs`
- `OpenModulePlatform.Portal/Models/AdminEditModels.cs`
- `sql/1-setup-openmoduleplatform.sql` (lines 723, 1518, 2472-2745)
