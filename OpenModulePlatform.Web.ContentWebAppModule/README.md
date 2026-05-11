# Content Web App Module

`OpenModulePlatform.Web.ContentWebAppModule` is a small first-party OMP web module for database-backed informational pages.

The first CMS iteration intentionally keeps the surface narrow:

- `/content` lists enabled pages the active role can read.
- `/content/{slug}` renders an enabled page when the active role has `can_read`.
- `/content/admin` lists pages the active role can write, while content managers can see all pages.
- `/content/admin/create` and `/content/admin/edit/{contentId}` create and edit pages without rebuilding or redeploying the module.
- Page access is stored in `omp_content.content_role_access` with `can_read` and `can_write` per OMP role.

Content is stored in `omp_content.contents` as either `markdown` or `html`. Markdown is rendered server-side with Markdig. HTML is rendered as trusted raw HTML; this module assumes editors are trusted and does not implement a sanitizer in this iteration.

The web application must be configured with `ContentWebAppModule:AppInstanceId`. The default setup script seeds a standard app instance at route path `content`.

Permissions:

- `ContentWebAppModule.Manage` allows full content administration, including creating pages, changing role access, enabling, and disabling pages.
- Ordinary editors can edit existing pages through page-level `can_write`.
- Ordinary readers can view enabled pages through page-level `can_read`.

The editor page uses TOAST UI Editor 3.2.2 from the TOAST CDN for the first version. If the CDN asset cannot load, the raw textarea remains usable.

Not in scope for this iteration: version history, publishing workflow, media library, uploads, block editing, comments, self-service, AD autocomplete, or advanced HTML sanitization.
