# SQL layout

## Root OpenModulePlatform scripts

Use these scripts for the neutral OMP core installation flow:

1. `1-setup-openmoduleplatform.sql`
   - Creates the neutral `omp` schema and core platform tables.

2. `2-initialize-openmoduleplatform.sql`
   - Seeds the default OMP instance, host/template baseline, RBAC baseline, and bootstrap administrator principal.

`2-initialize-openmoduleplatform.sql` contains a bootstrap administrator principal placeholder. Replace `REPLACE_ME\UserOrGroup` before running the script. The script intentionally stops with `THROW` while the placeholder is unchanged.

## Module-owned SQL

Each module owns its own setup and initialization scripts. The expected pattern is:

1. `1-setup-<module>.sql`
   - Creates only the module-owned schema and tables.

2. `2-initialize-<module>.sql`
   - Registers module/app definitions and seeds optional local/default data for that module.

Examples in this repository follow the same pattern under each module's own `sql` folder. The Portal module keeps its SQL in `OpenModulePlatform.Portal/sql`.

## Schema names

- `omp` is the core OpenModulePlatform schema.
- `omp_portal` is the OMP Portal module schema.
- `omp_iframe` and `omp_example_*` schemas belong to optional/example modules.
