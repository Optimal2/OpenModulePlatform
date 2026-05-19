# Content Web App Module

`OpenModulePlatform.Web.ContentWebAppModule` is a small first-party OMP web module for database-backed informational pages.

See `USAGE.md` for operator-facing instructions about content pages, server-side JSON reports, shortcodes, report file locations, and allowed report databases.

The first CMS iteration intentionally keeps the surface narrow:

- `/content` lists enabled pages the active role can read.
- `/content/{slug}` renders an enabled page when the active role has `can_read`.
- `/content/admin` lists pages the active role can write, while content managers can see all pages.
- `/content/admin/create` and `/content/admin/edit/{contentId}` create and edit pages without rebuilding or redeploying the module.
- `/content/admin` can load missing file-backed pages from configured HTML and server-report directories.
- Page access is stored in `omp_content.content_role_access` with `can_read` and `can_write` per OMP role.

Content is stored in `omp_content.contents` as `markdown`, `html`, or `server_report`. Markdown is rendered server-side with Markdig. HTML is rendered as trusted raw HTML; this module assumes editors are trusted and does not implement a sanitizer in this iteration. The admin UI also offers an HTML file mode, stored as an HTML row with only a file key, so existing installations do not need a schema migration.

Server reports are JSON definitions stored in the whitelisted server directory configured by `ContentWebAppModule:ServerReportsPath`, defaulting to `App_Data/ContentReports` under the web app content root. When the configured directory is a `Data/ContentReports` folder, the module also checks the parent `Data` folder so shared deployments can keep report JSON and HTML files side by side. The module also reads packaged report definitions from `ContentReports` under the web app content root so immutable Host Agent artifacts can ship report JSON while preserving runtime `App_Data` overrides. A content page stores only `server_report_key`, for example `module-status`, which maps to `module-status.json`. The admin UI can select an existing report key but cannot edit SQL or browse arbitrary file paths.

HTML file pages work the same way for trusted page bodies. `ContentWebAppModule:HtmlFilesPath` defaults to `App_Data/ContentPages`. When the configured directory is a `Data/ContentPages` folder, the module also checks the parent `Data` folder. Packaged fallback files are loaded from `ContentPages` under the web app content root. This lets multi-server deployments point both servers at the same shared HTML directory while Host Agent continues deploying immutable app artifacts.

The admin page scans these directories on each request and shows how many HTML and JSON report files are currently available. The `Load content files` action creates missing Content rows for discovered files. HTML files are imported before JSON reports, so a matching `example.html` and `example.json` pair becomes one HTML file page where the HTML can embed the report with a shortcode.

Server report JSON may contain one or more read-like SQL queries. A report can optionally set a top-level `database` property, or a query-level `database` property, to run against another database on the same SQL Server. Query-level `database` wins over the report-level value. Only database names listed in `ContentWebAppModule:AllowedServerReportDatabases` are accepted. The first version renders each query as a simple HTML table, blocks obvious data-changing SQL commands and `USE`, enforces a query timeout, and limits rendered rows with `maxRows` plus the configured defaults. Report files can be changed on the IIS server without rebuilding the module.

Markdown and HTML content can embed a server report with the shortcode `[DB_JSON="module-status"]`. The key uses the same whitelist and file mapping as server report content pages.

HTML content can also embed a report as JavaScript data with `[DB_JSON_SCRIPT="module-status"]`. The renderer writes `window.module_status` as a flat array of row objects and `window.module_statusReport` as the full report metadata object. Hyphens and other non-identifier characters in the JSON file key are converted to underscores for the default JavaScript variable name. Use `variable="customName"` when the page needs an explicit global name.

The web application must be configured with `ContentWebAppModule:AppInstanceId`. The default setup script seeds a standard app instance at route path `content`.

The setup script intentionally does not create default content pages. Use
`scripts/dev/seed-content-webapp-test-pages.ps1` when a local environment needs
sample pages for testing editors, shortcodes, HTML files, server reports, and
HostAgent file mirroring.

Permissions:

- `ContentWebAppModule.Manage` allows full content administration, including creating pages, changing role access, enabling, and disabling pages.
- Ordinary editors can edit existing pages through page-level `can_write`.
- Ordinary readers can view enabled pages through page-level `can_read`.

The editor page chooses the editor from the page content type. Markdown uses TOAST UI Editor 3.2.2 from the TOAST CDN with visual and source tabs, HTML uses a trusted visual editor with an HTML source tab, `html_file` uses an HTML file-key picker, and `server_report` uses a report-key picker. If the CDN asset cannot load, Markdown falls back to the raw textarea.

Not in scope for this iteration: SQL editing in the browser, arbitrary file browsing, JSON uploads, version history, publishing workflow, media library, uploads, block editing, comments, self-service, AD autocomplete, charts, or advanced HTML sanitization.
