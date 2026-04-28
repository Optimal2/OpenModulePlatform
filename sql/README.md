# SQL layout

## Root scripts

Use these scripts for the modular installation flow:

1. `1-setup-openmoduleplatform.sql`
2. `2-initialize-openmoduleplatform.sql`

These scripts only create and seed objects in the `omp` schema. Portal,
iFrame, and example modules are initialized separately from their own module
SQL folders.

`2-initialize-openmoduleplatform.sql` contains a bootstrap administrator
principal placeholder. Replace `REPLACE_ME\UserOrGroup` before running the
script. The script intentionally stops with `THROW` while the placeholder is
unchanged.

## Dev scripts

For quick local and test environment setup use:

1. `dev/1-install-openmoduleplatform.sql`
2. `dev/2-install-openmoduleplatform-examples.sql`

These convenience scripts include the root OMP scripts plus the Portal, iFrame,
and example module scripts.

`dev/SQL_Install_OpenModulePlatform.sql` is the single-file dev installer. It
uses the same bootstrap administrator placeholder validation as the modular
initialization script.

## Schema names

- `omp` is the core OpenModulePlatform schema.
- `omp_portal` is the OMP Portal module schema.
- `omp_iframe` and `omp_example_*` schemas belong to optional/dev modules.

The legacy single-file dev installer now registers the `omp_portal` module with
`SchemaName = 'omp_portal'` to match the modular initialization flow.
