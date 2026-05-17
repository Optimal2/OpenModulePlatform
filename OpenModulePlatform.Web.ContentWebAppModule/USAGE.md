# Content Module Usage

This guide describes how to use the Content Web App module, with extra detail for server-side JSON reports.

## Routes

- `/content` lists enabled pages the current role can read.
- `/content/{slug}` renders one content page by slug.
- `/content/admin` lists editable content pages.
- `/content/admin/create` creates a new page.
- `/content/admin/edit/{contentId}` edits an existing page.

The admin UI supports four content modes:

- `markdown`
- `html`
- `html_file`
- `server_report`

Markdown and raw HTML content is stored in the database. HTML file pages are stored as compatible HTML rows with a file key. Server reports are stored as JSON files on the server and referenced by key.

## Content Type Editors

Choose the page content type before editing the body:

- `markdown` uses visual and Markdown-source tabs. If the editor CDN cannot
  load, the raw textarea remains available and the page still saves Markdown.
- `html` uses visual and HTML-source tabs. HTML is trusted editor content and is
  rendered without sanitization.
- `html_file` stores only a file key in the database. The trusted HTML body is
  read from the configured HTML file directory or packaged ContentPages folder.
- `server_report` hides the body editor and shows only the report-key picker.

Content type is locked after a page has been created. Create a replacement page
when content needs to move between Markdown, inline HTML, HTML file, and server
report modes.

Use `html_file` when a deployment gateway blocks posting raw HTML or JavaScript
through the admin form, or when the same content file should be shared by several
web servers.

## Permissions And Roles

Content visibility and editing rights use the shared OMP RBAC model. The
current user's active role is resolved from the shared authentication cookie and
role selector, and the module then checks the permissions configured for the
current app instance.

The built-in routes normally map to these permission intents:

- `/content`: read published content pages.
- `/content/admin`: edit and administer content pages.
- `server_report`: read access to the content page still controls whether the
  report can be rendered.

Roles, role membership, and app-instance permissions are managed in the Portal
admin UI and seeded by the OMP SQL/install scripts. See
`../docs/AUTHENTICATION_AND_RBAC.md` for the shared authentication and RBAC model.

## Server Report Files

Server report files are JSON files in the configured report directory.

Default source location:

```text
OpenModulePlatform.Web.ContentWebAppModule/App_Data/ContentReports
```

Default IIS/runtime location:

```text
C:\OMP\WebApps\content\App_Data\ContentReports
```

The configured directory is controlled by:

```json
ContentWebAppModule:ServerReportsPath
```

The default value is:

```text
App_Data/ContentReports
```

The module can also load packaged report definitions from this deployable
application folder:

```text
ContentReports
```

This folder is useful for immutable app artifacts managed by Host Agent, because
Host Agent normally preserves `App_Data` as runtime data during web-app
deployments. If the same report key exists in both locations, the configured
runtime directory wins so operators can override a packaged report without
modifying the artifact.

HTML file pages use the same two-location model.

Configured runtime/shared directory:

```json
ContentWebAppModule:HtmlFilesPath
```

Default value:

```text
App_Data/ContentPages
```

Packaged fallback folder:

```text
ContentPages
```

When running several web servers behind a load balancer, set both
`ServerReportsPath` and `HtmlFilesPath` to shared absolute paths so every server
loads the same JSON and HTML files.

Report keys map directly to JSON filenames:

```text
module-status -> module-status.json
alfons-test   -> alfons-test.json
```

Keys may only contain letters, numbers, underscores, and hyphens.
Examples of invalid keys are `my report.json`, `report@123`, `../report`, and
`folder/report`.

## Adding A Report File

For a local persistent development change, add the JSON file under:

```text
OpenModulePlatform.Web.ContentWebAppModule/App_Data/ContentReports
```

For an app-artifact packaged report, include the JSON file under:

```text
ContentReports
```

Then publish/install the suite or deploy the app artifact so the file is copied
to the matching runtime folder:

```text
C:\OMP\WebApps\content\App_Data\ContentReports
C:\OMP\WebApps\content\ContentReports
```

For a quick runtime-only test, place the JSON file directly under:

```text
C:\OMP\WebApps\content\App_Data\ContentReports
```

Runtime-only files can be changed without rebuilding the module, but they may be overwritten by the next publish/install if they are not also present in the source/package.

## Report JSON Format

Minimal example:

```json
{
  "title": "Module status",
  "queries": [
    {
      "name": "modules",
      "title": "Modules",
      "database": "OpenModulePlatform",
      "sql": "select top 100 ModuleId, ModuleKey, DisplayName from omp.Modules order by DisplayName",
      "renderer": "table",
      "maxRows": 100
    }
  ]
}
```

Supported report fields:

- `title`: optional report title.
- `database`: optional default database for all queries in the report.
- `queries`: one or more query objects.

Supported query fields:

- `name`: stable query name.
- `title`: display title above the rendered table.
- `database`: optional database for this query. This wins over report-level `database`.
- `sql`: SQL query text.
- `renderer`: currently only `table`.
- `maxRows`: maximum rendered rows for this query.

If neither report-level nor query-level `database` is set, the module uses the default `ConnectionStrings:OmpDb` database.
This connection string is configured in the app's `appsettings*.json` files at
runtime. For suite installs, it is written from
`scripts/deployment/omp-suite.local.psd1` into the deployed
`appsettings.Production.json`; verify the deployed file or the generated IIS
runtime folder when troubleshooting environment-specific database access.

## Allowed Databases

Server reports may only select databases that are explicitly allowlisted.

Local install config:

```text
scripts/deployment/omp-suite.local.psd1
```

Sample config:

```text
scripts/deployment/omp-suite.config.sample.psd1
```

Relevant config section:

```powershell
ContentWebApp = @{
    ServerReportsPath = 'App_Data/ContentReports'
    HtmlFilesPath = 'App_Data/ContentPages'
    AllowedServerReportDatabases = @('OpenModulePlatform', 'alfons-test-db')
}
```

During publish/install, the deployment script writes the runtime config:

```text
C:\OMP\WebApps\content\appsettings.Production.json
```

Runtime config example:

```json
"ContentWebAppModule": {
  "ServerReportsPath": "App_Data/ContentReports",
  "HtmlFilesPath": "App_Data/ContentPages",
  "AllowedServerReportDatabases": [
    "OpenModulePlatform",
    "alfons-test-db"
  ]
}
```

Prefer changing `omp-suite.local.psd1` and then publishing/installing again. Editing `appsettings.Production.json` directly is useful for quick local tests, but that change can be overwritten by the next publish/install.

After changing runtime config directly, recycle the Content app pool so the app reloads options:

```powershell
%windir%\System32\inetsrv\appcmd.exe recycle apppool /apppool.name:OMP_ContentWebAppModule
```

## SQL Rules

Server reports are intended for read-like queries only.

Allowed query starts:

- `select`
- `with`

Blocked commands include:

- `insert`
- `update`
- `delete`
- `drop`
- `alter`
- `truncate`
- `exec`
- `execute`
- `merge`
- `create`
- `into`
- `use`

Only one SQL statement is allowed. A final terminal semicolon is allowed, but multiple statements are blocked.
When this rule is violated, the rendered report shows
`Multiple SQL statements are not allowed.`.

If a database, schema, or table name contains special characters, quote it correctly for SQL Server. For example, a table with a hyphen must use brackets:

```sql
select top 100 id from dbo.[example-table]
```

The database name itself can contain a hyphen when it is passed through the report `database` field and is present in `AllowedServerReportDatabases`.

## Rendering A Full Server Report Page

Create or edit a content page under:

```text
/content/admin/create
/content/admin/edit/{contentId}
```

Choose content type:

```text
Server report
```

Then choose a report key, for example:

```text
module-status
```

The page stores only the key in the database. The SQL remains in the server-side JSON file.

## Embedding A Server Report In Markdown Or HTML

Markdown and HTML pages can embed a server report with a shortcode:

```text
[DB_JSON="module-status"]
```

The key maps to:

```text
module-status.json
```

The same key validation and report directory rules apply as for full server report pages.

HTML pages can embed the same report as JavaScript data instead of an HTML table:

```text
[DB_JSON_SCRIPT="module-status"]
```

This writes two globals:

```javascript
window.module_status = [];
window.module_statusReport = {};
```

`window.module_status` is a flat array of row objects. When a report has several
queries, each row also includes `__queryName` and `__queryTitle` so page-level
HTML and JavaScript can filter the rows. `window.module_statusReport` contains
the full report title, query metadata, per-query rows, truncation flags, and
sanitized error messages.

The default variable name is derived from the JSON file key by replacing
non-identifier characters with underscores. Use an explicit variable name when
that is clearer:

```text
[DB_JSON_SCRIPT="module-status" variable="moduleStatusRows"]
```

Some Markdown editors escape underscores and save this as:

```text
[DB\_JSON="module-status"]
```

The renderer accepts escaped underscores for both shortcode names.

Examples:

```markdown
# Status

The current module status:

[DB_JSON="module-status"]
```

```html
<h1>Status</h1>
<p>The current module status:</p>

[DB_JSON="module-status"]
```

```html
<h1>Status</h1>

[DB_JSON_SCRIPT="module-status" variable="moduleStatusRows"]

<div id="status-count"></div>
<script>
  document.getElementById('status-count').textContent =
    `${window.moduleStatusRows.length} rows loaded`;
</script>
```

## Deployment Checklist

When adding a new report that must survive future publishes:

1. Add the JSON file under `OpenModulePlatform.Web.ContentWebAppModule/App_Data/ContentReports`.
2. Add any extra database names to `scripts/deployment/omp-suite.local.psd1`.
3. Add the same config shape to `scripts/deployment/omp-suite.config.sample.psd1` when the new option should be documented for future environments.
4. Run the local publish/install script.
5. Verify the file exists under `C:\OMP\WebApps\content\App_Data\ContentReports`.
6. Verify the runtime allowlist in `C:\OMP\WebApps\content\appsettings.Production.json`.
7. Open the content page or embedded shortcode page in the browser.

When changing only a runtime JSON query:

1. Edit the JSON file under `C:\OMP\WebApps\content\App_Data\ContentReports`.
2. Reload the page.
3. Recycle `OMP_ContentWebAppModule` only if configuration changed, not for ordinary JSON query edits.

## Troubleshooting

`The server report definition is not valid JSON.`

- Check missing commas between properties.
- Check trailing syntax around query objects.

`The report database is not allowed.`

- Add the database to `AllowedServerReportDatabases`.
- Make sure the runtime config was regenerated or manually updated.
- Recycle `OMP_ContentWebAppModule` after runtime config changes.

`The report query failed.`

- Run the SQL manually in SQL Server first.
- Check table/schema names.
- Quote names with special characters, for example `dbo.[example-table]`.
- Confirm the IIS app pool identity can access the target database.

`Multiple SQL statements are not allowed.`

- Keep the report query to one `SELECT` or `WITH` statement.
- Remove intermediate semicolons. A single final semicolon is allowed.

The shortcode is visible as text instead of a table.

- Confirm the shortcode key only contains letters, numbers, underscores, and hyphens.
- Use `[DB_JSON="report-key"]` or `[DB\_JSON="report-key"]`.
- Confirm the matching JSON file exists in the server report directory.
