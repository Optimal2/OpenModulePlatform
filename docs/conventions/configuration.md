# Configuration conventions for OMP+ODV

This document records the configuration patterns found in the ten OMP+ODV
repositories (source code only; `bin/`, `obj/`, `artifacts/`, `node_modules/`,
`dist/`, `release/` and other build output were excluded from the audit).
It is the configuration counterpart to `logging.md` and `unit-testing.md`.

Audited repositories (all under the local workspace root):

- .NET (8): OpenModulePlatform, IbsPackager, LogSearch, EArkivChecker,
  Dokumentbibliotek, VajSkrivare, iKrock2, ODVGateway
- JS/npm (2): OpenDocViewer, AgentDocMap

## 1. Per-repo configuration map

### OpenModulePlatform (.NET 10, platform repo)

- **Config sources:** `appsettings.json` + `appsettings.Development.json` in
  every app (e.g. `OpenModulePlatform.Portal/appsettings.json`); NLog embedded
  as a JSON section (`OpenModulePlatform.Portal/appsettings.json:52-88`);
  `OpenModulePlatform.Portal/web.config` (IIS/ANCM only); installer bootstrap
  config with `{SqlConnectionString}` tokens
  (`installer/hosts/sample/bootstrap.json:85-92`); PSD1 installer config
  (`scripts/deployment/hostagent-first.config.sample.psd1`); DB-stored config
  overlays applied by HostAgent (`docs/CONFIG_OVERLAYS.md:9-80`); CLI args for
  HostAgent (`OpenModulePlatform.HostAgent.WindowsService/Program.cs:10-18`).
  No `.env` files.
- **IOptions usage:** strongly typed everywhere. Web apps register through
  `AddOptions<T>().Bind(...).ValidateOnStart()` in
  `OpenModulePlatform.Web.Shared/Extensions/OmpWebHostingExtensions.cs:63-65`
  (`WebAppOptions`), `:858-859` (`OmpAuthOptions`), `:171,195` (push events);
  services use `services.Configure<T>(GetSection(...))`
  (`OpenModulePlatform.HostAgent.WindowsService/Program.cs:35`,
  `OpenModulePlatform.WorkerManager.WindowsService/Program.cs:24`).
  `IOptions<T>` dominant; `IOptionsMonitor<T>` in long-lived hosted services
  (`OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerManagerHostedService.cs:29`).
- **Validation:** only `WebAppOptions` is validated, via
  `IValidateOptions<WebAppOptions>`
  (`OpenModulePlatform.Web.Shared/Options/WebAppOptionsValidator.cs:9`,
  registered `OmpWebHostingExtensions.cs:61`). `HostAgentSettings`,
  `WorkerManagerSettings` and `OmpAuthOptions` have no validation.
- **Connection strings:** `ConnectionStrings:OmpDb` in each appsettings
  (`OpenModulePlatform.Portal/appsettings.json:34-36`); empty string in
  committed service appsettings
  (`OpenModulePlatform.HostAgent.WindowsService/appsettings.json:3`). Resolved
  centrally through per-project `SqlConnectionFactory` wrappers
  (`OpenModulePlatform.Web.Shared/Services/SqlConnectionFactory.cs:45`,
  `OpenModulePlatform.HostAgent.Runtime/Services/SqlConnectionFactory.cs:17`).
  Deploy-time materialization via Bootstrapper token replacement
  (`OpenModulePlatform.Bootstrapper/Program.cs:4172`) and HostAgent-generated
  appsettings (`OpenModulePlatform.HostAgent.Runtime/Services/ArtifactConfigurationFileWriter.cs:16-43`).
- **Secrets:** no `UserSecretsId` anywhere; no inline secrets in committed
  files (placeholders only, e.g. `OpenModulePlatform.Auth/appsettings.json:17`).
  Encrypted bootstrap secrets use the `enc:aesgcm:v1:` scheme
  (`OpenModulePlatform.Bootstrapper/Program.cs:4569-4621`,
  `scripts/protect-bootstrap-config-secrets.ps1`); service-account secrets use
  a DPAPI store (`OpenModulePlatform.HostAgent.Runtime/Services/HostAgentCredentialStoreService.cs:14`).
- **Options classes:** `XxxOptions` for web/shared (`WebAppOptions`,
  `OmpAuthOptions`, `OmpOidcOptions`, `ArtifactUploadOptions`,
  `PushEventProducerOptions`, ...); `XxxSettings` for Windows
  services/workers (`HostAgentSettings`, `WorkerManagerSettings`,
  `WorkerProcessSettings`).
- **Overlay relationship:** this is the overlay platform itself —
  `omp-components.json:1-237`, `omp_core.module-definition.json`, and per-app
  module definitions. Overlay model documented in `docs/CONFIG_OVERLAYS.md`;
  applied at deploy by HostAgent (`ArtifactConfigurationFileWriter.cs`,
  `WebAppDeploymentService.cs`).
- **Divergences:** every app binds section `"Portal"`
  (`OpenModulePlatform.Portal/Program.cs:24`) although
  `WebAppOptions.DefaultSectionName = "WebApp"`
  (`OpenModulePlatform.Web.Shared/Options/WebAppOptions.cs:75`); a second,
  parallel runtime-settings layer exists in the database (`omp.config_settings`
  via `OpenModulePlatform.Web.Shared/Services/OmpConfigurationService.cs:8`);
  the Bootstrapper keeps its own non-IConfiguration `BootstrapConfig` model.

### IbsPackager (.NET 10, private OMP module)

- **Config sources:** `IbsPackager.Web/appsettings.json:2-27` (empty `OmpDb`)
  + `appsettings.Development.json`; dev worker-host config
  (`.dev/worker-host/appsettings.json:11-20`); PSD1 installer configs —
  committed sample `scripts/deployment/ibspackager.config.sample.psd1`, real
  `*.local.psd1` gitignored (`.gitignore:8-10`); install-generated
  `appsettings.Production.json` (`scripts/deployment/install-ibspackager.ps1:799`);
  CLI-arg overrides to OMP WorkerProcessHost (`.dev/run-worker.ps1:67-73`);
  channel configuration as JSON stored in the database
  (`IbsPackager.Web/Pages/Channels/Edit.cshtml.cs:288`).
- **IOptions usage:** `builder.AddOmpWebDefaults<IbsPackagerResource>("WebApp")`
  (`IbsPackager.Web/Program.cs:8`); `services.Configure<OpenDocViewerOptions>`
  (`IbsPackager.Web/Program.cs:9`); `IOptions<T>` in page models. The worker
  uses **manual section binding** instead of the options pattern
  (`IbsPackager.Worker/IbsPackagerWorkerFactory.cs:42-55`).
- **Validation:** none — no `ValidateOnStart`, no `IValidateOptions<T>`;
  manual `Validate()` methods only.
- **Connection strings:** `ConnectionStrings:OmpDb` resolved centrally
  (`IbsPackager.Runtime/Services/SqlConnectionFactory.cs:15-24`, throws when
  empty); built from PSD1 values at install time
  (`scripts/deployment/install-ibspackager.ps1:749-767`).
- **Secrets:** none committed; sample psd1 carries empty placeholders
  (`scripts/deployment/ibspackager.config.sample.psd1:32-38`); installer warns
  on plaintext `RunAsPassword` (`install-ibspackager.ps1:357-362`).
- **Options classes:** `OpenDocViewerOptions`, `HostAgentRpcOptions`,
  `FileDropChannelOptions`; naming outlier `IbsPackagerRuntimeSettings`.
- **Overlay relationship:** full OMP module (`omp-components.json`,
  `ibs_packager.module-definition.json`); **produces** config overlays during
  packaging (`scripts/omp/build-repository-objects.ps1:587,773`) but its own
  runtime config arrives via install-generated `appsettings.Production.json`,
  not HostAgent overlays.
- **Divergences:** multiple competing config patterns in one repo (IOptions,
  manual binding, direct `GetConnectionString`, JSON-from-DB, PSD1, CLI args).

### LogSearch (.NET 10, OMP module)

- **Config sources:** `LogSearch.Service/appsettings.json:1-67` (incl. inline
  NLog) + Development variant; `LogSearch.Web/appsettings.json:1-42` +
  Development variant; OMP config overlays generate the deployed
  `appsettings.json` for both apps (see below). No `.env`, no psd1.
- **IOptions usage:** single extension point
  `services.AddLogSearchOptions(configuration)` →
  `AddOptions<LogSearchOptions>().Bind(...).ValidateOnStart()` with an
  `IValidateOptions<LogSearchOptions>` singleton
  (`LogSearch.Runtime/LogSearchOptionsServiceCollectionExtensions.cs:13-18`,
  validator `LogSearch.Runtime/LogSearchOptionsValidator.cs:5-75`).
  `IOptions<T>` injected uniformly; `WebAppOptions` via
  `AddOmpWebDefaults("WebApp")` (`LogSearch.Web/Program.cs:7`).
- **Connection strings:** `ConnectionStrings:OmpDb` resolved in
  `LogSearch.Runtime/LogSearchConnectionFactory.cs:21-25` with a three-tier
  source-database indirection: DB row override →
  `LogSearch:SourceConnectionStringTemplate` with `{server}`/`{database}`
  placeholders (`LogSearchConnectionFactory.cs:50-53`) → derived from `OmpDb`
  via `SqlConnectionStringBuilder` (`:62-68`). Guardrails reject SQL
  credentials in overrides/templates (`:71-95`). Overlays carry HostAgent
  tokens such as `{{Omp.Json.ConnectionStrings.OmpDb}}`
  (`scripts/omp/build-host-profile-objects.ps1:269`).
- **Secrets:** none committed; credential-free policy enforced in code
  (`LogSearchConnectionFactory.cs:74-77`); `Password=` strings in
  `LogSearch.Tests/LogSearchConnectionFactoryTests.cs:25,39` are deliberate
  negative test fixtures.
- **Options classes:** `LogSearchOptions` (`SectionName` const,
  `LogSearch.Runtime/LogSearchOptions.cs:5`) with nested
  `LogSearchSourceDatabaseOptions` (`LogSearchOptions.cs:89-104`).
- **Overlay relationship:** full OMP module and an exemplary **overlay
  producer**: `scripts/omp/build-host-profile-objects.ps1:321-355` converts
  host-profile data into `omp-config-overlay` documents whose
  `configurationFiles[].relativePath` is `appsettings.json`; overlay version is
  a sha256 fingerprint of inputs (`build-host-profile-objects.ps1:429`).
- **Divergences:** defaults are triplicated (options consts
  `LogSearchOptions.cs:7-44`, both appsettings files, overlay generator
  `build-host-profile-objects.ps1:253-264`); the overlay emits an unused
  `Worker` section (`build-host-profile-objects.ps1:295-300`) and both
  `Portal` and `WebApp` sections although only `WebApp` is bound.

### EArkivChecker (.NET 10, private OMP module)

- **Config sources:** `EArkivChecker.Web/appsettings.json` +
  `EArkivChecker.Service/appsettings.json:1-50` (inline NLog `:13-49`), each
  with a Development variant; `appsettings.Local.json` documented as a
  personal overlay (`docs/DEV-SETUP.md:128`, gitignored `.gitignore:368`) and
  loaded in both hosts right after `appsettings.{Environment}.json`
  (`EArkivChecker.Web/Program.cs:8`, `EArkivChecker.Service/Program.cs:10`);
  monitored folders are DB rows, not config.
- **IOptions usage:** `AddEArkivCheckerOptions(...)` extension
  (`EArkivChecker.Runtime/EArkivCheckerOptionsServiceCollectionExtensions.cs:9-19`)
  with `.Bind(...)` in both hosts (`EArkivChecker.Web/Program.cs:12`,
  `EArkivChecker.Service/Program.cs:12`); `IOptions<T>` injection;
  `WebAppOptions` via `AddOmpWebDefaults("WebApp")`
  (`EArkivChecker.Web/Program.cs:10`).
- **Validation:** `IValidateOptions<EArkivCheckerOptions>`
  (`EArkivChecker.Runtime/EArkivCheckerOptionsValidator.cs`) registered by
  `AddEArkivCheckerOptions(...)` with `.ValidateOnStart()` — fail-fast at
  startup in both hosts.
- **Connection strings:** `ConnectionStrings:OmpDb` resolved eagerly at
  startup; both hosts construct `EArkivCheckerConnectionFactory` from
  `builder.Configuration.GetConnectionString("OmpDb")` and the factory takes
  the resolved string with a missing-value guard
  (`EArkivChecker.Runtime/EArkivCheckerConnectionFactory.cs:9-17`) — no
  `IConfiguration` in the Runtime layer. Real values arrive only via
  HostAgent overlay of `appsettings.json`.
- **Secrets:** none committed; Integrated Security only.
- **Options classes:** `EArkivCheckerOptions`
  (`EArkivChecker.Runtime/EArkivCheckerOptions.cs:3-5`, `SectionName` const).
- **Overlay relationship:** consumer-side only; overlays are deliberately not
  committed (`scripts/omp/README.md:94-97`); packaging accepts
  `-ConfigOverlayFile` (`scripts/omp/build-repository-objects.ps1:39,587,773`).
- **Divergences:** the `EArkivChecker` section is duplicated across both
  appsettings files with overlapping keys.

### Dokumentbibliotek (.NET 10, single web project, OMP module)

- **Config sources:** `appsettings.json` at **repo root**, linked into the
  project (`RazorPages/OpenModulePlatform.Web.eArkivDokumentbibliotek.RazorPages.csproj:11`);
  `RazorPages/appsettings.Development.json`; gitignored
  `appsettings.Local.json` (`.gitignore:14`). No env vars are read in repo
  code; no site-config JS.
- **IOptions usage:** `services.Configure<DokumentBibliotekOptions>(GetSection("DokumentBibliotek"))`
  (`RazorPages/Program.cs:39`); `WebAppOptions` via
  `AddOmpWebDefaults("Portal")` (`RazorPages/Program.cs:11`).
  `IOptions<T>` in most consumers but `IOptionsMonitor<DokumentBibliotekOptions>`
  in a singleton (`Services/DocumentLibraryPathMapper.cs:8-22`) — two
  consumption patterns for one options type.
- **Validation:** none for `DokumentBibliotekOptions`.
- **Connection strings:** `ConnectionStrings:OmpDb` via the OMP shared
  `SqlConnectionFactory`; optional legacy `DokumentBibliotekDb` read directly
  with a fail-fast guard (`Services/DocumentLibraryDataStore.cs:23-27`).
- **Secrets:** none committed; Integrated Security only.
- **Options classes:** `DokumentBibliotekOptions` (`Models/AppSetting.cs:15`);
  note that `Models/AppSetting.cs:5` is a DTO for **DB-stored settings**
  (`Services/DocumentLibrarySettingsService.cs:16-61`), so the app has two
  config layers (JSON + database).
- **Overlay relationship:** the module definition documents the runtime
  configuration contract HostAgent overlays must satisfy
  (`earkiv_dokumentbibliotek.module-definition.json:240-277`); AGENTS.md
  codifies that host-specific config comes from overlays, not hardcoded values.
- **Divergences:** direct `IConfiguration` reads bypass the options class in
  `Services/DocumentLibraryImageService.cs:431-432` (`WebImageRootPrefix` is
  not even declared on the options class); three-layer image-root resolution
  (DB setting → config keys → hardcoded fallback `"Images"`,
  `DocumentLibraryImageService.cs:426-447`).

### VajSkrivare (.NET 10, OMP module "Skrivarkoppling")

- **Config sources:** `src/Skrivarkoppling.Web/appsettings.json`
  (environment-neutral template, inline NLog `:8-44`) +
  `appsettings.Development.json`; **no committed Production file** — the
  deployed `appsettings.json` is HostAgent-generated; `web.config` (IIS
  hosting only); `launchSettings.json`.
- **IOptions usage:** exemplary —
  `AddOptions<PrinterDatabaseCatalogOptions>().Bind(...).Validate(...).ValidateOnStart()`
  (`src/Skrivarkoppling.Web/Program.cs:36-40`) and the same for
  `ZebraConfigOptions` (`Program.cs:51-57`); `WebAppOptions` via
  `AddOmpWebDefaults("Portal")` (`Program.cs:33`). `IOptions<T>` everywhere.
- **Connection strings:** named indirection — each
  `PrinterDatabases:Items[]` entry points at a connection-string name
  (`Domain/Configuration/PrinterDatabaseDefinition.cs:9`), resolved via
  `configuration.GetConnectionString(database.ConnectionStringName)`
  (`Services/SqlConnectionFactory.cs:13-19`). `ConnectionStrings:OmpDb` is
  consumed by OMP shared components and supplied only by host config.
- **Secrets:** none committed; Integrated Security against localhost/LocalDB.
- **Options classes:** `PrinterDatabaseCatalogOptions`, `ZebraConfigOptions`
  (both with `public const string SectionName`);
  `PrinterDatabaseDefinition` as item type.
- **Overlay relationship:** the module definition declares
  `artifactConfigurationFiles` for `appsettings.json` with
  `"contentSource": "host-agent-generated"` and `requiredRootSections`
  (`vajskrivare.module-definition.json:33-50`) — the overlay replaces the
  whole file; module SQL disables legacy `appsettings.Production.json`
  overlays (`Sql/01_initialize_vajskrivare_metadata.sql:65-94`).
- **Divergences:** orphaned keys — `ZebraConfig:BackupRetentionCount/Days`
  (`appsettings.json:81-82`) have no matching properties on
  `ZebraConfigOptions`; one direct `GetSection("Portal").Get<WebAppOptions>()`
  for startup logging (`Program.cs:75`) bypasses DI.

### iKrock2 (.NET 10, OMP module)

- **Config sources:** `iKrock2.Web/appsettings.json` +
  `iKrock2.Backend/appsettings.json`, each with Development variants (inline
  NLog in both); install-generated `appsettings.Production.json`
  (`scripts/deployment/install-ikrock2.ps1:704-705`); committed sample psd1
  (`scripts/deployment/ikrock2.config.sample.psd1`) + gitignored
  `*.local.psd1` (`.gitignore:29`).
- **IOptions usage:** `services.Configure<T>` for `BackendOptions`
  (`iKrock2.Backend/Program.cs:21`), `BackendClientOptions`
  (`iKrock2.Web/Program.cs:32`), and `SqlServerOptions`/`WorkOrderOptions`/
  `OmpDatabaseOptions` (`iKrock2.Application/DependencyInjection/IKrock2ApplicationServiceCollectionExtensions.cs:14-19`);
  `WebAppOptions` via `AddOmpWebDefaults("Portal")`
  (`iKrock2.Web/Program.cs:17`). `IOptions<T>` only.
- **Validation:** none — misconfiguration surfaces as
  `InvalidOperationException` at first connection
  (`iKrock2.Application/Services/SqlConnectionFactory.cs:25-33`).
- **Connection strings:** `ConnectionStrings:OmpDb` copied into
  `OmpDatabaseOptions` (`IKrock2ApplicationServiceCollectionExtensions.cs:18`);
  per-catalog strings composed from `SqlServer:HostConnectionString` via
  `SqlConnectionStringBuilder.InitialCatalog`
  (`iKrock2.Application/Services/SqlConnectionFactory.cs:23-41`); installer
  builds the production string from psd1 (`install-ikrock2.ps1:214`).
- **Secrets:** committed files clean; **risk on disk:** gitignored
  `scripts/deployment/ikrock2.vgr-prod.local.psd1:32` contains a plaintext
  production `RunAsPassword` and `:74` sets `IncludeConfigInPackage = $true`,
  which would bundle the psd1 (including the password) into a release zip.
- **Options classes:** `OmpDatabaseOptions`, `SqlServerOptions`,
  `WorkOrderOptions`, `BackendOptions`, `BackendClientOptions` — consistent
  `XxxOptions` naming, but section names do not mirror class names
  (`"Backend"` binds two different classes in the two apps).
- **Overlay relationship:** full OMP module (`omp-components.json:5-38`,
  `ikrock.module-definition.json`), but runtime config is delivered by the
  installer writing `appsettings.Production.json`, not by HostAgent overlays.
- **Divergences:** stale HTTP config — the backend Kestrel API was removed but
  `BackendClientOptions.BaseUrl = http://localhost:5088`
  (`iKrock2.Web/Options/BackendClientOptions.cs:5`) and dependent health
  checks remain; **hardcoded customer URLs and test data in C#**
  (`iKrock2.Web/Pages/MLLPerformance/Index.cshtml.cs:23-28,41-49`);
  `Configuration["key"]` read deep in a page model (`Index.cshtml.cs:118`);
  dead DI overload (`IKrock2ApplicationServiceCollectionExtensions.cs:24-33`).

### ODVGateway (.NET 10 minimal API, OMP module, no database)

- **Config sources:** `src/ODVGateway/appsettings.json:9-72` (root section
  `ODVGateway`) + `appsettings.Development.json`; generated (uncommitted)
  `appsettings.Smoke.json` for smoke tests (`scripts/smoke-test.ps1:96,259`);
  `web.config` (IIS hosting + security headers); standard env-var/CLI
  providers via `WebApplication.CreateBuilder(args)`
  (`src/ODVGateway/Program.cs:30`).
- **IOptions usage:** single root options class with nested options
  (`src/ODVGateway/Options/ODVGatewayOptions.cs:3`), registered via
  `services.Configure<ODVGatewayOptions>` (`Program.cs:40-41`). Two
  consumption styles: an eager startup snapshot
  (`.Get<ODVGatewayOptions>()`, `Program.cs:34-36`) for CSP/FormOptions, and
  `IOptionsMonitor<ODVGatewayOptions>` (hot-reload) in all singleton services
  (e.g. `Services/GatewaySessionStore.cs:32`).
- **Validation:** no `ValidateOnStart`/`IValidateOptions<T>`; manual startup
  validation for trusted roots (`Program.cs:1226-1255`) and silent runtime
  clamps for numeric limits.
- **Connection strings:** none — the app has no database
  (`odvgateway.module-definition.json:43-45`).
- **Secrets:** none committed; explicit no-secrets policy
  (`SECURITY.md:77`).
- **Overlay relationship:** full OMP module; declares `appsettings.json` as
  `contentSource: "site-local"` (`odvgateway.module-definition.json:33-42`);
  packaging scripts produce/consume OMP config overlays.
- **Divergences:** security headers duplicated in code (`Program.cs:79-81`)
  and `web.config:10-12`; committed production-default
  `AllowedHosts: "localhost;127.0.0.1"` (`appsettings.json:8`).

### OpenDocViewer (JavaScript, React 19 + Vite SPA, OMP module)

- **Config sources:** primary pattern is **executable runtime JS config**:
  `public/odv.config.js` (committed defaults, sets `window.__ODV_CONFIG__`,
  deep-merges `window.__ODV_SITE_CONFIG__`, freezes the result —
  `public/odv.config.js:39,99-100,800-807`) plus an optional per-site
  `odv.site.config.js` (gitignored `.gitignore:80`; sample
  `public/odv.site.config.sample.js`). Load order and cache-busting in
  `src/app/bootConfig.js:121-132` with optional SRI integrity. Build-time env
  via Vite (`vite.config.js:30-31,202-208`). The two Node log servers use
  dotenv + env vars (`server/system-log-server.js:11-18,36`,
  `server/user-log-server.js:17-22,37`). IIS proxy placeholders in
  `IIS-ODVProxyApp/web.config.template:27,33,40,46`.
- **Consumption:** central accessor `getRuntimeConfig()`
  (`src/utils/runtimeConfig.js:113-124`) plus typed normalizing getters
  (`runtimeConfig.js:136-532`); however ~10 modules still read
  `window.__ODV_CONFIG__` directly (e.g. `src/logging/systemLogger.js:81-108`,
  `src/i18n.js:122`).
- **Connection strings:** none (no database). Endpoint URLs live in
  `odv.config.js:434,795`.
- **Secrets:** no real secrets committed; the browser-visible log token is a
  placeholder (`public/odv.config.js:796`) and is deliberately low-assurance;
  the real `LOG_TOKEN` is injected as a service environment variable by NSSM
  (`scripts/Manage-ODV-LogServers.ps1:418-434`).
- **Overlay relationship:** full OMP module (`omp-components.json:12-25`,
  `opendocviewer.module-definition.json:3-11`); `odv.site.config.js` is
  delivered as an OMP artifact configuration file at package time
  (`scripts/omp/README.md:139`) and SRI hashes are injected into `index.html`
  (`scripts/omp/build-repository-objects.ps1:445-532`).
- **Divergences:** three competing logging-config channels (runtime config >
  meta tags > `import.meta.env.VITE_LOG_*`, `src/logging/systemLogger.js:115-157`);
  dual defaults layers (config file vs in-code getter defaults that must be
  aligned by hand, `public/odv.config.js:703-706`); executable-JS trust
  boundary mitigated by SRI; customer-specific site configs committed under
  `VGR/`.

### AgentDocMap (JavaScript/Node CLI, not an OMP module)

- **Config sources:** CLI arguments only (`src/cli.js:32-59`), validated
  manually (`src/cli.js:72-82`); npm script presets (`package.json:15`);
  target repo's `jsdoc.json` as input. No `.env`, no dotenv, no config files.
- **Consumption:** a single plain options object passed through
  `generateAgentDocs(options)` (`src/index.js:11-45`); `process.env` is used
  only for well-known system paths in a safety guard
  (`src/lib/outputGuard.js:25-31,51-52`).
- **Connection strings / secrets:** none; secret hygiene is a product feature
  (sensitive-file exclusion, `src/lib/fileInventory.js:9-31`).
- **Overlay relationship:** none — no module definition, no
  `omp-components.json`; org-level adjacency only (validates against the
  sibling OpenDocViewer checkout).
- **Divergences:** behavior knobs live as scattered module-level constants
  (`src/lib/writers.js:5-15`, `src/lib/fileInventory.js:5-7`); hardcoded
  sibling path `../OpenDocViewer` (`package.json:15`).

## 2. Comparison table and divergences

| Repo | Config sources | Options pattern | Startup validation | Connection strings | Secrets | Overlay role |
|---|---|---|---|---|---|---|
| OpenModulePlatform | appsettings + Dev, bootstrap.json, psd1, overlays | `XxxOptions` (web) / `XxxSettings` (services), `IOptions<T>`/`IOptionsMonitor<T>` | Only `WebAppOptions` (`IValidateOptions` + `ValidateOnStart`) | `OmpDb` via per-project `SqlConnectionFactory`; bootstrap token replacement | None committed; `enc:aesgcm:v1:` + DPAPI store | Platform (produces/applies overlays) |
| IbsPackager | appsettings + Dev, psd1 installer, generated Production.json, CLI args, DB channel JSON | Mixed: `IOptions<T>` (web) + manual binding (worker) | None | `OmpDb` via `SqlConnectionFactory`; installer-built | None committed; gitignored local psd1 | Produces overlays; consumes none |
| LogSearch | appsettings + Dev, generated overlay appsettings | `XxxOptions` + `IOptions<T>`, uniform | Yes — `IValidateOptions` + `ValidateOnStart` | `OmpDb` + 3-tier source-DB indirection with credential guardrails | None committed; policy-enforced | Exemplary producer of generated appsettings overlays |
| EArkivChecker | appsettings + Dev + Local.json | `XxxOptions` + `IOptions<T>` | Yes — `IValidateOptions` + `ValidateOnStart` | `OmpDb` via factory taking the resolved string | None committed | Consumer (overlays not committed) |
| Dokumentbibliotek | root-linked appsettings + Dev + Local.json | `XxxOptions`; mixed `IOptions<T>`/`IOptionsMonitor<T>` | None | `OmpDb` shared factory + legacy direct read | None committed | Consumer; contract documented in module definition |
| VajSkrivare | appsettings template + Dev; HostAgent-generated production file | `XxxOptions` + `SectionName` consts + `IOptions<T>` | Yes — `Validate(...)` + `ValidateOnStart` | Named indirection (`ConnectionStringName`) | None committed | Full overlay replacement of appsettings.json |
| iKrock2 | appsettings + Dev, psd1, generated Production.json | `XxxOptions` + `IOptions<T>` | None | `OmpDb` → options; per-catalog composition | Plaintext password in gitignored prod psd1 that `IncludeConfigInPackage=$true` may bundle | None at runtime (installer-written config) |
| ODVGateway | appsettings + Dev + generated Smoke | Single root `XxxOptions` + nested; startup snapshot + `IOptionsMonitor<T>` | Manual (trusted roots only) | None (no DB) | None committed | Site-local appsettings via overlays |
| OpenDocViewer | executable JS config + site config merge, dotenv (log servers), vite env, URL params | Central accessor + typed getters (JS analog) | Normalizing getters with clamps | None (endpoint URLs instead) | Placeholder token only; real token via service env | Receives `odv.site.config.js` as artifact config file |
| AgentDocMap | CLI args only | Plain options object (JS) | Manual CLI validation | None | None | None |

Key divergences:

1. **Validation coverage is the widest gap.** Only OMP Web.Shared
   (`WebAppOptions`), LogSearch, EArkivChecker and VajSkrivare validate
   options at startup. IbsPackager, Dokumentbibliotek, iKrock2 and ODVGateway
   fail late (first use / first connection) instead of at startup.
2. **Connection-string style is consistent in shape** (`ConnectionStrings:OmpDb`
   + factory) **but delivery differs:** HostAgent overlay-generated appsettings
   (LogSearch, VajSkrivare, Dokumentbibliotek, EArkivChecker) vs
   installer-written `appsettings.Production.json` (IbsPackager, iKrock2, OMP
   services via Bootstrapper).
3. **Raw `IConfiguration` reads deep in services** bypass the options pattern
   in Dokumentbibliotek (`DocumentLibraryImageService.cs:431-432`) and iKrock2
   (`Pages/MLLPerformance/Index.cshtml.cs:118`).
4. **iKrock2 is the largest outlier:** stale backend HTTP config, hardcoded
   customer URLs/test data in C#, and a password-bearing psd1 that may be
   bundled into packages.
5. **Naming split is deliberate in OMP** (`XxxOptions` web vs `XxxSettings`
   services) but accidental elsewhere (`IbsPackagerRuntimeSettings` outlier).
6. **The two JS repos use entirely different models** (executable merged JS
   config vs CLI-args-only) — appropriate to their shapes (browser SPA vs CLI).

## 3. Recommended standard

### .NET standard

The dominant — and recommended — pattern, already implemented by OMP
Web.Shared, LogSearch and VajSkrivare:

1. **Sources:** committed `appsettings.json` holds environment-neutral
   defaults (localhost/LocalDB dev connection strings or empty placeholders);
   `appsettings.Development.json` holds personal dev overrides. NLog stays as
   an embedded JSON section. No `.env` files; no user-secrets.
2. **Options classes:** one `XxxOptions` class per config section with a
   `public const string SectionName`; nested option types for lists/children.
   `XxxSettings` remains acceptable for OMP-style Windows services/workers.
3. **Registration:** `AddOptions<T>().Bind(GetSection(T.SectionName))` +
   `ValidateOnStart()`, with either an `IValidateOptions<T>` implementation
   (LogSearch model — preferred for cross-field rules) or inline
   `Validate(...)` delegates (VajSkrivare model — fine for simple rules).
4. **Consumption:** inject `IOptions<T>` (transient/scoped consumers) or
   `IOptionsMonitor<T>` (singletons that should observe reloads). No
   `Configuration["key"]` reads outside `Program.cs` and factory classes.
5. **Connection strings:** `ConnectionStrings:OmpDb`, resolved once in a
   single factory per app; named indirection (`ConnectionStringName`) when an
   app talks to multiple databases (VajSkrivare model).
6. **Host-specific values:** never committed. They arrive as OMP HostAgent
   config overlays / host configurations (`docs/CONFIG_OVERLAYS.md`), with
   placeholder tokens such as `{{Omp.Json.ConnectionStrings.OmpDb}}` resolved
   by HostAgent at apply time (LogSearch model). Installer-written
   `appsettings.Production.json` remains acceptable for Bootstrapper-managed
   services, but overlay-generated appsettings is the target for module apps.
7. **Secrets:** none in committed files; installer/overlay injection,
   `enc:aesgcm:v1:` for bootstrap config, DPAPI for service-account secrets.

Motivation: this combination fails fast at startup, keeps secrets out of git,
matches the HostAgent overlay pipeline that operations already uses, and is
the pattern new OMP modules (examples, shared helpers) already teach.

### JS/npm standard

1. **Browser SPAs (OpenDocViewer model):** committed executable default
   config + gitignored/deployed site config merged and frozen at boot; a
   single central accessor (`getRuntimeConfig()`) with typed normalizing
   getters as the only read path; SRI pinning for executable config; secrets
   never in browser-visible config (server-side env injection instead).
2. **Node services:** dotenv + environment variables, `.env` gitignored,
   variables documented at the top of the entry file.
3. **CLI tools (AgentDocMap model):** CLI arguments with up-front manual
   validation; no hidden config files; knobs that must be shared belong in
   one exported constants module, not scattered literals.

## 4. Migration notes per diverging repo

### OpenModulePlatform (Medium)

- Current: only `WebAppOptions` validated; `"Portal"` section used everywhere
  although `DefaultSectionName = "WebApp"`; dual runtime-settings layer
  (`omp.config_settings`).
- Migration: extend `ValidateOnStart` coverage to `OmpAuthOptions`,
  `HostAgentSettings` and `WorkerManagerSettings`; document the
  `"Portal"` section name as the de-facto standard (or align the default);
  document when `omp.config_settings` should be preferred over appsettings.
- Priority: Medium.

### IbsPackager (Medium)

- Current: no options validation; worker uses manual section binding; runtime
  config only from installer-generated Production.json.
- Migration: convert worker binding to `AddOptions<T>().Bind(...).ValidateOnStart()`;
  add validation for `OpenDocViewerOptions`/`HostAgentRpcOptions`; long-term,
  move module appsettings delivery to HostAgent overlays like LogSearch.
- Priority: Medium.

### LogSearch (Low)

- Current: exemplary pattern, but defaults triplicated across options consts,
  appsettings and the overlay generator; overlay emits unused `Worker` and
  `Portal` sections.
- Migration: make the overlay generator the single source of defaults (or
  generate appsettings from it); drop unused sections from overlay output.
- Priority: Low.

### EArkivChecker (Done)

- Done (EArkivChecker `5b9e6e8`): `IValidateOptions<EArkivCheckerOptions>` +
  `ValidateOnStart()` via `AddEArkivCheckerOptions(...)` in both hosts;
  `appsettings.Local.json` now loaded in both hosts;
  `EArkivCheckerConnectionFactory` takes the resolved `OmpDb` connection
  string instead of `IConfiguration`.
- Remaining: the `EArkivChecker` section is still duplicated across both host
  appsettings files with overlapping keys.
- Migration: extract the shared defaults (for example into the Runtime layer
  or a generated overlay) so each host only carries host-specific overrides.
- Priority: Low.

### Dokumentbibliotek (Medium)

- Current: no validation; `WebImageRootPrefix` read via raw `IConfiguration`
  and absent from the options class; mixed `IOptions`/`IOptionsMonitor`.
- Migration: add `ValidateOnStart()` for `DokumentBibliotekOptions`; move all
  `DokumentBibliotek:*` keys onto the options class; standardize on
  `IOptionsMonitor` for singletons or `IOptions` elsewhere.
- Priority: Medium.

### VajSkrivare (Low)

- Current: near-exemplary; orphaned `ZebraConfig:BackupRetention*` keys;
  one direct `GetSection(...).Get<WebAppOptions>()` for startup logging.
- Migration: implement or remove the orphaned retention keys; route the
  startup-log read through injected options.
- Priority: Low.

### iKrock2 (High)

- Current: stale backend HTTP client config; hardcoded customer URLs and test
  data in C#; deep `Configuration["key"]` read; no validation; password-bearing
  prod psd1 combined with `IncludeConfigInPackage = $true`.
- Migration: remove the dead `BackendClientOptions`/health-check config; move
  MLL links/credentials and patient data into config sections bound to options
  and supplied by overlays (customer values out of C#); add
  `ValidateOnStart()`; delete the dead DI overload; ensure secret-bearing
  psd1 files are excluded from packages.
- Priority: High (customer data in source + packaging password risk).

### ODVGateway (Low)

- Current: manual validation only for trusted roots; security headers set in
  both code and web.config; committed localhost `AllowedHosts`.
- Migration: extend startup validation to numeric limits (fail fast instead of
  silent clamps); pick a single owner for security headers (web.config or
  middleware).
- Priority: Low.

### OpenDocViewer (Low–Medium)

- Current: ~10 modules bypass `getRuntimeConfig()`; three logging-config
  channels; dual defaults layers; customer site configs committed under `VGR/`.
- Migration: route all reads through the central accessor; collapse logging
  config to runtime-config only; generate `odv.config.js` defaults from the
  same source as getter defaults (or vice versa); move customer site configs
  to a private repository (already the direction per repo AGENTS.md).
- Priority: Low–Medium.

### AgentDocMap (None/Low)

- Current: CLI-args-only is consistent for a CLI; scattered constants.
- Migration: optional — centralize tunables into one exported constants
  module. No OMP alignment required (not a runtime component).
- Priority: None/Low.
