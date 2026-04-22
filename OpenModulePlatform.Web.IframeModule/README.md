# OpenModulePlatform.Web.IframeModule

Proof-of-concept OMP web module that wraps a small set of database-controlled URLs in an iframe while reusing the shared OMP topbar, RBAC and hosting defaults.

The module reads the first three enabled rows from `omp_iframe_module.urls`, filters them by the current active OMP role when `AllowedRoles` is populated, and exposes them as buttons above the iframe.

For development, see `sql/SQL_Install_OpenModulePlatform_Examples.sql` in the repository root to create the schema, seed sample rows and register the module in OMP.
