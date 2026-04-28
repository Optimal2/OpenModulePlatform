# SQL layout

## Root scripts

Use these scripts for the modular installation flow:

1. `1-setup-openmoduleplatform.sql`
2. `2-initialize-openmoduleplatform.sql`

These scripts only create and seed objects in the `omp` schema.
Portal, iframe, and example modules are initialized separately from their own
module sql folders.

`2-initialize-openmoduleplatform.sql` contains a bootstrap administrator
principal placeholder. Replace `REPLACE_ME\UserOrGroup` before running the
script. The script intentionally stops with `THROW` while the placeholder is
unchanged.

## Dev scripts

For quick local and test environment setup use:

1. `dev/1-install-openmoduleplatform.sql`
2. `dev/2-install-openmoduleplatform-examples.sql`

These convenience scripts include the root OMP scripts plus the Portal,
iFrame, and example module scripts.

`dev/SQL_Install_OpenModulePlatform.sql` is the single-file dev installer. It
uses the same bootstrap administrator placeholder validation as the modular
initialization script.
