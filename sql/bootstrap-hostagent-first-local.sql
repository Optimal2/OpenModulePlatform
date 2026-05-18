/*
OpenModulePlatform local HostAgent-first bootstrap.

Run this file through sqlcmd mode or through OpenModulePlatform.Bootstrapper.
The bootstrapper expands :r includes, replaces USE [OpenModulePlatform] with
the configured database, and injects the configured bootstrap portal admin.
*/

:r .\1-setup-openmoduleplatform.sql
:r .\2-initialize-openmoduleplatform.sql
:r ..\OpenModulePlatform.Portal\sql\1-setup-omp-portal.sql
:r ..\OpenModulePlatform.Portal\sql\2-initialize-omp-portal.sql
:r ..\OpenModulePlatform.Web.ContentWebAppModule\Sql\1-setup-content-webapp.sql
:r ..\OpenModulePlatform.Web.ContentWebAppModule\Sql\3-add-server-report-support.sql
:r ..\OpenModulePlatform.Web.ContentWebAppModule\Sql\2-initialize-content-webapp.sql
:r ..\OpenModulePlatform.Web.iFrameWebAppModule\Sql\1-setup-iframe-webapp.sql
:r ..\OpenModulePlatform.Web.iFrameWebAppModule\Sql\2-initialize-iframe-webapp.sql
:r .\3-initialize-opendocviewer.sql
:r ..\examples\WebAppModule\Sql\1-setup-example-webapp.sql
:r ..\examples\WebAppModule\Sql\2-initialize-example-webapp.sql
:r ..\examples\WebAppBlazorModule\Sql\1-setup-example-webapp-blazor.sql
:r ..\examples\WebAppBlazorModule\Sql\2-initialize-example-webapp-blazor.sql
:r ..\examples\ServiceAppModule\Sql\1-setup-example-serviceapp.sql
:r ..\examples\ServiceAppModule\Sql\2-initialize-example-serviceapp.sql
:r ..\examples\WorkerAppModule\Sql\1-setup-example-workerapp.sql
:r ..\examples\WorkerAppModule\Sql\2-initialize-example-workerapp.sql
