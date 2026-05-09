# Content Web App Module

`OpenModulePlatform.Web.ContentWebAppModule` is a small first-party OMP web module for simple informational pages.

The module intentionally keeps the CMS surface narrow:

- Markdown or sanitized HTML content
- Page tree by normalized slug inside one OMP `AppInstanceId`
- Draft/published state
- Revision history per save
- Manage UI for create/edit/publish/unpublish/delete

The web application must be configured with `ContentWebAppModule:AppInstanceId`. This is the OMP app instance that owns the pages rendered by this IIS application. The default setup script seeds a standard app instance at route path `content`.

Permissions:

- `ContentWebAppModule.View` allows published page rendering.
- `ContentWebAppModule.Manage` allows page administration and preview.

The renderer sanitizes all generated HTML before output. This module is not a generic CMS platform and deliberately has no media library, block editor, workflow engine or headless API in v1.
