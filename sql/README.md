# SQL layout

## Root scripts

Use these scripts for the modular installation flow:

1. `1-setup-openmoduleplatform.sql`
2. `2-initialize-openmoduleplatform.sql`

These scripts only create and seed objects in the `omp` schema.
Portal, iframe, and example modules are installed from their own `sql/`
folders inside each project.

## Dev scripts

For quick local and test environment setup use:

1. `dev/1-install-openmoduleplatform.sql`
2. `dev/2-install-openmoduleplatform-examples.sql`

These convenience scripts include the root OMP scripts plus the Portal,
iFrame, and example module scripts.
