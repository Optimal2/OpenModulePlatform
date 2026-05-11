# OpenModulePlatform.Web.iFrameWebAppModule

First-party Razor module for OpenModulePlatform that renders the shared OMP top
bar, a module-local URL selector in the module header, and an iframe whose
source is resolved from `omp_iframe.urls` through a named URL set in
`omp_iframe.url_sets` / `omp_iframe.url_set_urls`.

Use this module when a web application should be available from OMP but cannot
be integrated as a native OMP web app. The module is deployable functionality,
not an example project or template.
