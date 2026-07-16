# Dependency injection conventions for OMP+ODV

This document records the dependency-injection (DI) patterns found in the
eight OMP+ODV .NET repositories (source code only; `bin/`, `obj/`,
`artifacts/`, `node_modules/`, `dist/` and other build output were excluded
from the audit). It is the DI counterpart to `configuration.md`,
`logging.md` and `unit-testing.md`; options-binding details cross-reference
`configuration.md`, which this audit supersedes where the two disagree
(options validation has been added to several repos since that audit).

Audited repositories (all under the local workspace root, all .NET 10):

- OpenModulePlatform, IbsPackager, LogSearch, EArkivChecker,
  Dokumentbibliotek, VajSkrivare, iKrock2, ODVGateway

No repository uses a `Startup.cs`; every host uses top-level-statements
minimal hosting. No repository uses `TryAdd*` for application services, and
none uses a third-party DI container.

## 1. Per-repo DI map

### OpenModulePlatform (.NET 10, platform repo)

Two host families: ASP.NET Core Razor Pages apps composed through a shared
registration hub, and Generic-Host Windows Services/workers with inline
registrations.

**Web — shared hub (`OpenModulePlatform.Web.Shared`):**

- **Registration location:** nearly all shared registrations live in one
  extension file, `OpenModulePlatform.Web.Shared/Extensions/OmpWebHostingExtensions.cs`:
  `AddOmpWebDefaults<TAppResource>` (`:54-191`, main block),
  `AddOmpPushEventDispatcher` (`:193-209`), `AddOmpCookieAuthentication`
  (`:211-217`, delegating to private `ConfigureOmpAuthentication`
  `:849-906`), `UseOmpWebDefaults` (`:219`), `UseOmpSecurityHeaders`
  (`:737`). NLog wiring in `AddOmpWebLogging`
  (`OpenModulePlatform.Web.Shared/Extensions/OmpWebLoggingExtensions.cs:12`).
- **Lifetimes:** scoped for per-request data/services
  (`OmpConfigurationService`, `OmpBrandingService`, `RbacService`,
  `NotificationService`, `MessageService`, `BannerService`,
  `PortalTopBarService` — `OmpWebHostingExtensions.cs:182-188`); singleton
  for stateless infrastructure (`SqlConnectionFactory` `:170`,
  `IPushEventPublisher` factory `:173-178`, notification publishers
  `:179-181`, `IValidateOptions<WebAppOptions>` `:61`); the only transient
  is `IClaimsTransformation` (`:168`). No captive dependencies: singletons
  depend only on config, logging and other singletons.
- **Module/plugin pattern:** compile-time composition via project reference
  to Web.Shared; the contract is `AddOmpWebDefaults<TAppResource>` +
  `UseOmpWebDefaults`, enforced by Roslyn analyzer OMPWEB001/002
  (`OpenModulePlatform.Web.Shared.Analyzers/OmpWebDefaultsAnalyzer.cs:13`)
  and `scripts/omp/validate-webshared-contracts.ps1:244-252`.
- **Hosted services:** exactly one — `PushEventDispatcherHostedService :
  BackgroundService` (`OpenModulePlatform.Web.Shared/Notifications/PushEventDispatcherHostedService.cs:11`),
  registered conditionally at `OmpWebHostingExtensions.cs:205` behind
  `PushEvents:Dispatcher:Enabled` (`:202`).
- **Options:** `WebAppOptions` and `OmpAuthOptions` are fully validated —
  `AddOptions<WebAppOptions>().Bind(...).ValidateOnStart()` (`:63-65`) with
  `IValidateOptions<WebAppOptions>` (`:61`, implementation
  `OpenModulePlatform.Web.Shared/Options/WebAppOptionsValidator.cs:9`), and
  `AddOptions<OmpAuthOptions>().Bind(...).ValidateOnStart()` (`:860-862`)
  with `IValidateOptions<OmpAuthOptions>` (`:858`, implementation
  `OpenModulePlatform.Web.Shared/Options/OmpAuthOptionsValidator.cs:11`;
  OIDC rules apply only when `OmpAuth:Oidc:Enabled`). Unvalidated:
  `PushEventProducerOptions` (`Configure<>` `:171`),
  `PushEventDispatcherOptions` (`Configure<>` `:195`) — deliberately, since
  the dispatcher clamps out-of-range values via `Effective*` properties.
  `IOptionsMonitor<T>` is used where reload matters
  (`MigratingTopBarNotificationStatePublisher.cs:14`,
  `PushEventDispatcherHostedService.cs:23`).
- **HTTP clients:** none in web projects.

**Web — hosts:**

- `OpenModulePlatform.Portal/Program.cs:24-70` —
  `AddOmpWebDefaults<PortalResource>("Portal")` (`:24`),
  `AddOmpPushEventDispatcher()` (`:25`, the only host that enables the
  dispatcher), then ~20 inline registrations: scoped repositories/services
  (`:27-48`) plus singletons `LocalPasswordHasher` (`:28`) and
  `PortalDeploymentLockService` (`:46`);
  `AddOptions<ArtifactUploadOptions>().Bind(...).ValidateOnStart()` (`:52-54`,
  no rules — empty roots and a non-positive `MaxUploadBytes` are valid by
  design). No Portal-specific extension method — all registrations inline.
- `OpenModulePlatform.Web.ContentWebAppModule/Program.cs:10-26` —
  `AddOmpWebDefaults<ContentWebAppModuleResource>("Portal")` (`:10`),
  `AddOptions<ContentWebAppModuleOptions>().Bind(...).Validate(...).ValidateOnStart()`
  (`:11-15`, requires a non-empty `AppInstanceId`), six scoped services
  (`:21-26`).
- `OpenModulePlatform.Web.iFrameWebAppModule/Program.cs:7-8` —
  `AddOmpWebDefaults<IFrameWebAppModuleResource>("Portal")` (`:7`), one
  scoped repository (`:8`).
- `OpenModulePlatform.Auth/Program.cs:22-67` — **deliberate exception:**
  does not call `AddOmpWebDefaults`; manually composes a subset
  (`AddOmpWebLogging` `:22`, localization `:23-25`, `AddHttpContextAccessor`
  `:26`, `AddMemoryCache` `:27`) plus own services (singletons `:28,32-35`,
  scoped `:29-31,36-37`), then `AddOmpCookieAuthentication` (`:38`) and
  `AddOmpOidcAuthentication` (`:39`, extension declared at
  `OpenModulePlatform.Auth/Services/OmpOidcAuthenticationExtensions.cs:13`,
  which also registers an instance-singleton `IStartupFilter` `:32-33`).
  `ValidateOnStart` coverage comes from `AddOmpCookieAuthentication` →
  `ConfigureOmpAuthentication` (`OmpAuthOptions`).

**Services, workers and libraries:**

- `OpenModulePlatform.WorkerManager.WindowsService/Program.cs:11-32` —
  `Host.CreateDefaultBuilder` + `UseWindowsService` (`:12-15`),
  registrations inline in `ConfigureServices` (`:22-32`): all singleton
  (`:25-30`) plus `AddHostedService<WorkerManagerHostedService>` (`:31`,
  implementation `Services/WorkerManagerHostedService.cs:19`).
  `Configure<WorkerManagerSettings>` (`:24`) consumed as
  `IOptionsMonitor<WorkerManagerSettings>`; manual `Validate()` calls
  instead of `ValidateOnStart` (`WorkerManagerHostedService.cs:253,768`,
  `Models/WorkerManagerSettings.cs:54`).
- `OpenModulePlatform.WorkerProcessHost/Program.cs:11-23` — console child
  host (no `UseWindowsService`): singletons `WorkerModuleLoader` (`:20`),
  `WorkerRuntimeContextFactory` (`:21`),
  `AddHostedService<WorkerProcessHostedService>` (`:22`).
  `Configure<WorkerProcessSettings>` (`:19`) consumed as `IOptions<T>`
  (not monitor — `Services/WorkerProcessHostedService.cs:31`). This is the
  **worker plugin mechanism**: `Plugins/WorkerModuleLoader.cs:12` loads
  plugin assemblies into a collectible `AssemblyLoadContext` and finds
  `IWorkerModuleFactory` implementations; the hosted service creates a DI
  scope per plugin activation (`Services/WorkerProcessHostedService.cs:98-100`)
  and hands `scope.ServiceProvider` to the factory (contract
  `OpenModulePlatform.Worker.Abstractions/Contracts/IWorkerModuleFactory.cs:7`).
- `OpenModulePlatform.HostAgent.WindowsService/Program.cs:20-67` — the
  largest service registration block (`:33-67`): all singleton, using a
  **concrete-first + interface-forwarding** idiom so both resolve the same
  instance (`SqlConnectionFactory`/`ISqlConnectionFactory` `:37-38`,
  `OmpHostArtifactRepository`/`IOmpHostArtifactRepository` `:39-40`),
  `TimeProvider.System` (`:59`), three hosted services
  (`HostAgentHostedService` `:61`, `MaintenanceScanScheduler` `:62` —
  defined in `OpenModulePlatform.HostAgent.Runtime/Services/MaintenanceScanScheduler.cs:12` —
  and Windows-only `HostAgentRpcHostedService` `:63-66`).
  `Configure<HostAgentSettings>` (`:35`) with manual `Validate()` at first
  use; `IOptionsMonitor<HostAgentSettings>` everywhere. Contains the repo's
  only `AddHttpClient`
  calls: named clients `"PortalHealth"` (`:48`) and
  `"PortalHealthAllowInvalidTls"` with a custom handler (`:49-53`), consumed
  via `IHttpClientFactory` in
  `OpenModulePlatform.HostAgent.Runtime/Services/WebAppHealthMonitor.cs:15,202-204`.
- `OpenModulePlatform.Bootstrapper` — **no DI at all**: static `Program`
  partials (`Program.cs:17`), plain library usage with manual
  `settings.Validate()` (`Program.cs:4462,4474`).
- `OpenModulePlatform.Artifacts`, `OpenModulePlatform.Worker.Abstractions`,
  `OpenModulePlatform.EventPublisher.Abstractions` — pure libraries, no DI
  registrations. `OpenModulePlatform.EventPublisher.Sql` is DI-friendly by
  constructor (`SqlPushEventPublisher.cs:14-20`) but registered only by
  Web.Shared (`OmpWebHostingExtensions.cs:173-178`).
- `examples/` — four example web modules all follow the module shape:
  `AddOmpWebDefaults<TResource>("Portal")` + `Configure<OpenDocViewerExampleOptions>`
  + one scoped admin repository (e.g.
  `examples/WebAppModule/WebApp/Program.cs:11-14`,
  `examples/ServiceAppModule/WebApp/Program.cs:10-14`);
  `examples/ServiceAppModule/ServiceApp/Program.cs:9-31` mirrors the
  WorkerManager shape (all singleton + `AddHostedService` + `UseWindowsService`);
  `examples/WorkerAppModule/WorkerApp/ExampleWorkerAppModuleFactory.cs:13`
  is the reference `IWorkerModuleFactory` plugin implementation.

### IbsPackager (.NET 10, private OMP module)

- **Registration location:** single host `IbsPackager.Web/Program.cs`
  (18 lines): `AddOmpWebDefaults<IbsPackagerResource>("WebApp")` (`:8`),
  `Configure<OpenDocViewerOptions>` (`:9`), three singletons
  (`SqlConnectionFactory` `:10`, `IbsPackagerRepository` `:11`,
  `ConfigSchemaValidator` `:12`). **No `IServiceCollection` extension
  methods exist in the repo.**
- **Lifetimes:** all singleton, no scoped/transient. Safe because the
  repository is stateless with per-call connections
  (`IbsPackager.Runtime/Services/IbsPackagerRepository.cs:16-19`), but it
  diverges from the OMP scoped-data-services convention. Flag: a module-own
  `SqlConnectionFactory` (`IbsPackager.Runtime/Services/SqlConnectionFactory.cs:10`)
  collides in name with the OMP shared one — both singletons, both reading
  `ConnectionStrings:OmpDb`.
- **Module/plugin pattern:** web side uses only the OMP shared defaults.
  Worker side is an OMP plugin: `IbsPackagerWorkerFactory :
  IWorkerModuleFactory` (`IbsPackager.Worker/IbsPackagerWorkerFactory.cs:11`)
  manually news up the whole object graph (`:15-38`) — the host owns the
  container. A second plugin level loads channel types from DB-driven
  metadata through a custom `AssemblyLoadContext`
  (`IbsPackager.Runtime/Services/ChannelTypeLoader.cs:17-63`), with
  factories receiving `IServiceProvider`
  (`IbsPackager.Abstractions/Contracts/IIbsChannelTypeFactory.cs:16`).
- **Hosted services:** none (background work runs as the OMP worker plugin).
- **Options:** `WebAppOptions` validated via OMP defaults;
  `OpenDocViewerOptions` is plain `Configure<T>` with no validation;
  `HostAgentRpcOptions` bypasses the options pattern entirely (manual
  section binding in `IbsPackagerWorkerFactory.cs:40-56`, manual
  `Validate()` at `IbsPackager.Runtime/Models/HostAgentRpcOptions.cs:27-33`).
- **HTTP clients:** none (outbound RPC is named-pipe based,
  `IbsPackager.Runtime/Services/HostAgentRpcClient.cs:15`).

### LogSearch (.NET 10, OMP module)

- **Registration location:** inline per host, plus one shared options
  extension. Web: `LogSearch.Web/Program.cs:5-10`
  (`AddOmpWebDefaults<LogSearchResource>("WebApp")` `:7`,
  `AddLogSearchOptions` `:8`, singletons `LogSearchConnectionFactory` `:9`
  and `LogSearchRepository` `:10`) plus a post-build startup seeder
  (`:18`). Service: `LogSearch.Service/Program.cs:5-19` (all singleton
  `:8-13`, `AddHostedService<LogSearchWorker>` `:14`,
  `AddWindowsService("OMP.LogSearch")` `:16-19`).
- **Lifetimes:** all singleton in both hosts. The Service host registers
  concrete + interface with forwarding factories (`:9`, `:11`); the Web
  host registers concretes only and pages inject the concrete
  `LogSearchRepository` (e.g. `LogSearch.Web/Pages/Index.cshtml.cs:22`) —
  an intra-repo inconsistency. Interfaces exist primarily for test fakes
  (`LogSearch.Tests/Fakes/`).
- **Module/plugin pattern:** `AddOmpWebDefaults` only; no runtime plugin
  loading (module packaging is declarative via
  `log_search.module-definition.json`).
- **Extension methods:** one, options-only, shared by both hosts:
  `AddLogSearchOptions` returning `OptionsBuilder<T>`
  (`LogSearch.Runtime/LogSearchOptionsServiceCollectionExtensions.cs:9`).
  Naming convention: method `Add<Feature>Options`, file
  `<Feature>OptionsServiceCollectionExtensions.cs`, placed in the Runtime
  library.
- **Hosted services:** `LogSearchWorker : BackgroundService`
  (`LogSearch.Service/LogSearchWorker.cs:7`), registered
  `LogSearch.Service/Program.cs:14`.
- **Options:** exemplary — `AddOptions<LogSearchOptions>().Bind(...).ValidateOnStart()`
  with singleton `IValidateOptions<LogSearchOptions>`
  (`LogSearchOptionsServiceCollectionExtensions.cs:13-18`, validator
  `LogSearch.Runtime/LogSearchOptionsValidator.cs:5`); consumed uniformly
  as `IOptions<T>`.
- **HTTP clients:** none. Tests bypass DI (`Options.Create`, e.g.
  `LogSearch.Tests/LogSearchConnectionFactoryTests.cs:12`).

### EArkivChecker (.NET 10, private OMP module)

- **Registration location:** inline in both hosts, with the same
  registration block **duplicated verbatim** between
  `EArkivChecker.Web/Program.cs:12-15` and
  `EArkivChecker.Service/Program.cs:12-14,21` — the clearest candidate for
  a shared extension. Web adds `AddOmpWebDefaults<EArkivCheckerResource>("WebApp")`
  (`:10`) and `AddOmpPushEventDispatcher()` (`:11`); Service adds
  `AddHostedService<EArkivCheckerWorker>` (`:26`) and
  `AddWindowsService("OMP.EArkivChecker")` (`:28-31`).
- **Lifetimes:** all singleton, no scoped/transient anywhere — safe
  because every service is stateless by design (per-call connections,
  `EArkivChecker.Runtime/EArkivCheckerRepository.cs:23`). The connection
  factory is a pre-built instance registered with a fail-fast guard
  (`EArkivChecker.Web/Program.cs:13-14`,
  `EArkivChecker.Runtime/EArkivCheckerConnectionFactory.cs:11-14`).
  Service registers concrete + interface forwarding (`Program.cs:21-24`);
  Web registers concretes only (same intra-repo split as LogSearch).
- **Module/plugin pattern:** `AddOmpWebDefaults`/`UseOmpWebDefaults`
  (`EArkivChecker.Web/Program.cs:10,19`); no runtime plugin loading
  (manifest-driven packaging, `omp-components.json:5-46`).
- **Extension methods:** one, options-only:
  `AddEArkivCheckerOptions(this IServiceCollection, IConfiguration)`
  returning `OptionsBuilder<T>`
  (`EArkivChecker.Runtime/EArkivCheckerOptionsServiceCollectionExtensions.cs:9`)
  — same naming convention as LogSearch.
- **Hosted services:** `EArkivCheckerWorker : BackgroundService`
  (`EArkivChecker.Service/EArkivCheckerWorker.cs:7`), registered
  `EArkivChecker.Service/Program.cs:26`.
- **Options:** `AddOptions<EArkivCheckerOptions>().Bind(...).ValidateOnStart()`
  + singleton `IValidateOptions<EArkivCheckerOptions>`
  (`EArkivCheckerOptionsServiceCollectionExtensions.cs:13-18`, manual rule
  accumulation in `EArkivChecker.Runtime/EArkivCheckerOptionsValidator.cs:5-26`).
  Caveat: consumers snapshot `options.Value` in constructors (e.g.
  `EArkivChecker.Service/EArkivCheckerWorker.cs:20`), so
  `reloadOnChange: true` on the JSON overlays never propagates; no
  `IOptionsMonitor<T>` anywhere.
- **HTTP clients:** none. Tests bypass DI with `Options.Create`.

### Dokumentbibliotek (.NET 10, single web app, OMP module)

- **Registration location:** single composition root
  `RazorPages/Program.cs`: `AddOmpWebDefaults<eArkivDokumentbibliotekResource>("Portal")`
  (`:11`), conditional CORS (`:25-35`), `AddControllers` (`:37`), options
  + services (`:39-54`). No `IServiceCollection` extensions in the repo;
  one `WebApplication` extension for endpoints
  (`MapDocumentLibraryRuntimeEndpoints`,
  `RazorPages/Infrastructure/DocumentLibraryEndpointMapping.cs:11`, called
  at `Program.cs:82`).
- **Lifetimes:** the OMP-web-style mix — scoped data services
  (`DocumentLibraryDataStore` `:44`, six `IDocumentLibrary*`/
  `IDatabaseMigrationService` interface registrations `:46-51`) and
  singleton infrastructure (`DocumentLibrarySchemaCache` `:45`,
  `DocumentLibraryPathMapper` `:52`, `IOpenDocViewerBundleBuilder` `:53`,
  `FileExtensionContentTypeProvider` `:54`). Textbook scope bridging:
  singleton `DocumentLibrarySchemaCache` injects `IServiceProvider` and
  creates a scope per cache refresh
  (`Services/DocumentLibrarySchemaCache.cs:53-54`) — no captive
  dependency. Minor redundancy: `AddMemoryCache()` is called both by
  `AddOmpWebDefaults` and at `Program.cs:38`.
- **Module/plugin pattern:** `AddOmpWebDefaults`/`UseOmpWebDefaults`
  (`Program.cs:11,75`); no runtime plugin loading.
- **Hosted services:** none; the startup DB migration is imperative code
  with a manual scope before `app.Run()` (`Program.cs:58-73`), gated by
  `RunMigrationsOnStartup`.
- **Options:** fully validated — singleton
  `IValidateOptions<DokumentBibliotekOptions>` (`Program.cs:39`,
  implementation `Services/DokumentBibliotekOptionsValidator.cs:11-44`)
  plus `AddOptions<DokumentBibliotekOptions>().Bind(...).ValidateOnStart()`
  (`Program.cs:40-43`). Mixed consumption by design:
  `IOptionsMonitor<DokumentBibliotekOptions>` in the singleton
  `DocumentLibraryPathMapper` (`Services/DocumentLibraryPathMapper.cs:8-13`),
  `IOptions<T>` elsewhere. (Note: `configuration.md` predates this
  validation; it is present now.)
- **HTTP clients:** none.

### VajSkrivare (.NET 10, single web app, OMP module "Skrivarkoppling")

- **Registration location:** single file
  `src/Skrivarkoppling.Web/Program.cs`:
  `AddOmpWebDefaults<SkrivarkopplingResource>("Portal")` (`:33`), options
  (`:36-40`, `:51-57`) and eight service registrations (`:42-60`) inline;
  `UseOmpWebDefaults` at `:177`. No extension methods declared in the
  repo.
- **Lifetimes:** consistently interface-based and layered (Pages →
  Application services → Infrastructure repositories →
  `IDbConnectionFactory`): scoped for the data-access layer
  (`IDbConnectionFactory` `:43`, Dapper repositories `:45-46`, application
  services `:48-49,60`); singleton for stateful/cross-request state
  (`IPrinterDatabaseCatalog` `:42`, `IZebraConfigStore` `:59` — the latter
  holds a process-wide `SemaphoreSlim` and is `IDisposable`,
  `Infrastructure/Zebra/JsonZebraConfigStore.cs:15-33`). No captive
  dependencies; singletons take only options and logging.
- **Module/plugin pattern:** `AddOmpWebDefaults` only; no plugin loading
  (packaged via `omp-components.json:5-25`,
  `vajskrivare.module-definition.json`).
- **Hosted services:** none.
- **Options:** both module options use the full chain
  `AddOptions<T>().Bind(...).Validate(...).ValidateOnStart()` with
  delegate rules and `SectionName` consts
  (`Program.cs:36-40` `PrinterDatabaseCatalogOptions`,
  `Domain/Configuration/PrinterDatabaseCatalogOptions.cs:5`;
  `Program.cs:51-57` `ZebraConfigOptions`,
  `Domain/Zebra/ZebraConfigOptions.cs:5`). Only plain `IOptions<T>`
  consumption. One non-DI read for startup logging
  (`Program.cs:75`).
- **HTTP clients:** none. Tests use `WebApplicationFactory` with a scoped
  fake override (`tests/Skrivarkoppling.Web.Tests/ApiAnonymityTests.cs:36-39`).

### iKrock2 (.NET 10, OMP module)

- **Registration location:** the only consumer repo with a **module-level
  registration extension**: `AddIKrock2Application(this IServiceCollection,
  IConfiguration)`
  (`iKrock2.Application/DependencyInjection/IKrock2ApplicationServiceCollectionExtensions.cs:10-57`,
  private service block `:59-75`), called by both hosts —
  `iKrock2.Web/Program.cs:28` (after `AddOmpWebDefaults<IKrock2Resource>("Portal")`
  `:13`) and `iKrock2.Backend/Program.cs` (`Host.CreateDefaultBuilder` +
  `UseWindowsService` + `UseNLog`, `ConfigureServices` `:9-24`). Web adds
  two scoped services (`IKrock2DataService` `:45`,
  `IKrock2UserNameResolver` `:46`).
- **Lifetimes:** twelve singletons in the extension (`:61-72`) — safe
  because repositories are stateless with per-call connections
  (`iKrock2.Application/Services/SqlConnectionFactory.cs:35-40`) — plus
  the two scoped web services (a deliberate per-request choice; both
  depend only on singletons). No captive dependencies.
- **Module/plugin pattern:** `AddOmpWebDefaults`/`UseOmpWebDefaults`
  (`iKrock2.Web/Program.cs:13,56`) + the module extension; no runtime
  plugin loading.
- **Extension methods:** exactly one — convention: file
  `{X}ServiceCollectionExtensions.cs` in a `DependencyInjection/` folder,
  method `Add{Product}Application`, returns `IServiceCollection`.
- **Hosted services:** `WorkOrderBackgroundService : BackgroundService`
  (`iKrock2.Backend/Services/WorkOrderBackgroundService.cs:11`),
  registered `iKrock2.Backend/Program.cs:23`.
- **Options:** `SqlServerOptions`, `WorkOrderOptions` and
  `OmpDatabaseOptions` all use
  `AddOptions<T>().Bind(...).Validate(...).ValidateOnStart()`
  (`IKrock2ApplicationServiceCollectionExtensions.cs:14-54` — note
  `OmpDatabaseOptions` binds via a `Configure` lambda reading
  `GetConnectionString("OmpDb")` `:47-50` instead of a section);
  `MLLPerformanceOptions` validated in `iKrock2.Web/Program.cs:29-44`.
  **Divergence:** `BackendOptions` is `Configure<T>` only, unvalidated,
  and appears dead — no `IOptions<BackendOptions>` consumer exists
  (`iKrock2.Backend/Program.cs:21`,
  `iKrock2.Backend/Options/BackendOptions.cs:3`). (Note:
  `configuration.md` predates the validation chains; they are present
  now.)
- **HTTP clients:** none. One deliberate service-locator exception:
  `HttpContext.RequestServices.GetRequiredService<IKrock2UserNameResolver>()`
  in the page-model base (`iKrock2.Web/Pages/IKrock2PageModel.cs:36-39`).

### ODVGateway (.NET 10 minimal API, OMP module by packaging only, no database)

- **Registration location:** fully inline in one top-level
  `src/ODVGateway/Program.cs` (1355 lines): options materialized pre-build
  for validation (`:34-38`), all registrations at `:40-51`, framework
  options at `:53-65`. No extension methods, no `Startup.cs`, **no OMP
  project/package reference** — the only audited app that does not call
  `AddOmpWebDefaults`.
- **Lifetimes:** nine singletons (`:42-50`), nothing else — coherent for
  a scoped-resource-free app (stateful session store and
  `SemaphoreSlim` limiter must be singletons). No captive dependencies.
- **Module/plugin pattern:** none in DI; OMP packaging is declarative
  (`odvgateway.module-definition.json`).
- **Hosted services:** none (session TTL expiry is lazy, inside
  `Services/GatewaySessionStore.cs`).
- **Options:** single root `ODVGatewayOptions` with nested types
  (`src/ODVGateway/Options/ODVGatewayOptions.cs:3`, `SectionName` const
  `:5`), registered via plain `Configure<ODVGatewayOptions>`
  (`Program.cs:40-41`) — **no `ValidateOnStart`/`IValidateOptions<T>`**;
  instead a manual pre-build validation throws on invalid trusted roots
  (`Program.cs:38`, implementation `:1226-1255`), while numeric limits are
  silently clamped at use sites (`:856-875`). Mixed consumption:
  `IOptionsMonitor<ODVGatewayOptions>` in six services (e.g.
  `Services/GatewaySessionStore.cs:31`) but `IOptions<ODVGatewayOptions>`
  in `Services/ContentTypeMapper.cs:10` — inconsistent.
- **HTTP clients:** one named client,
  `AddHttpClient("ODVGateway.RemoteInline")` (`Program.cs:51`) with **no
  configuration delegate** (no BaseAddress/timeout/handler); consumed via
  `IHttpClientFactory` in minimal-API handlers (`Program.cs:271,349`),
  per-request timeouts via linked `CancellationTokenSource`
  (`:723-724,1036-1037`).

## 2. Comparison table and divergences

| Repo | Registration location | Lifetimes | Extension methods | Hosted services | Options pattern | HTTP clients |
|---|---|---|---|---|---|---|
| OpenModulePlatform | Hub extension (`OmpWebHostingExtensions`) + inline per host | Scoped web services + singleton infra; services all-singleton; 1 transient (`IClaimsTransformation`) | `AddOmp*`/`UseOmp*` on `WebApplicationBuilder`/`IServiceCollection` in `Extensions/` | 5 `BackgroundService` (push dispatcher, WorkerManager, WorkerProcess, HostAgent ×2, MaintenanceScan) | `ValidateOnStart` only for `WebAppOptions`; services `Configure<T>` + manual `Validate()`; `IOptionsMonitor` in long-lived services | Named clients in HostAgent only (`"PortalHealth"` ×2) |
| IbsPackager | Inline `Program.cs` only | All singleton (3) | None in repo | None | OMP-validated `WebAppOptions`; own `Configure<T>` unvalidated; worker manual binding | None |
| LogSearch | Inline per host + shared options extension | All singleton; concrete+interface forwarding in Service only | `AddLogSearchOptions` (`*OptionsServiceCollectionExtensions.cs`) | 1 (`LogSearchWorker`) | `AddOptions+Bind+ValidateOnStart` + `IValidateOptions<T>` — exemplary | None |
| EArkivChecker | Inline, **duplicated block** across 2 hosts | All singleton; concrete+interface forwarding in Service only | `AddEArkivCheckerOptions` (same convention as LogSearch) | 1 (`EArkivCheckerWorker`) | `AddOptions+Bind+ValidateOnStart` + `IValidateOptions<T>`; eager `.Value` snapshots defeat `reloadOnChange` | None |
| Dokumentbibliotek | Inline `Program.cs` | Scoped data services + singleton infra (OMP-web mix) | None (endpoint-mapping ext only) | None (imperative startup migration) | `AddOptions+Bind+ValidateOnStart` + `IValidateOptions<T>`; deliberate `IOptions`/`IOptionsMonitor` split | None |
| VajSkrivare | Inline `Program.cs` | Interface-based; scoped data layer + singleton stores | None in repo | None | `AddOptions+Bind+Validate(...)+ValidateOnStart` (delegates) for both module options | None |
| iKrock2 | **Module extension** `AddIKrock2Application` + small inline per host | 12 singletons (extension) + 2 scoped (Web) | `AddIKrock2Application` (`DependencyInjection/{X}ServiceCollectionExtensions.cs`) | 1 (`WorkOrderBackgroundService`) | `ValidateOnStart` for 4 types; `BackendOptions` unvalidated **and dead** | None |
| ODVGateway | Inline `Program.cs` only | All singleton (9) | None | None | `Configure<T>` + manual pre-build validation; no `ValidateOnStart`; mixed `IOptions`/`IOptionsMonitor` | 1 named client, unconfigured |

Key divergences:

1. **Three registration styles coexist.** (a) Centralized extension:
   OMP Web.Shared (`AddOmpWebDefaults`), iKrock2 (`AddIKrock2Application`),
   LogSearch/EArkivChecker (options-only extensions). (b) Fully inline
   `Program.cs`: IbsPackager, Dokumentbibliotek, VajSkrivare, ODVGateway,
   and all OMP service hosts. (c) Duplicated inline blocks across sibling
   hosts: EArkivChecker (Web/Service share an identical block).
2. **Lifetime philosophy splits the fleet.** Scoped per-request data
   access (OMP web, Dokumentbibliotek, VajSkrivare) vs all-singleton with
   stateless services and per-call connections (IbsPackager, LogSearch,
   EArkivChecker, iKrock2 Application, ODVGateway, OMP services). Both are
   internally consistent and free of captive dependencies, but a module
   copying patterns across repos can pick the wrong one for its shape.
3. **Concrete vs interface registration is inconsistent even within one
   repo.** LogSearch and EArkivChecker register interfaces (with
   forwarding) only in their Service hosts while their Web hosts inject
   concretes; IbsPackager/iKrock2 inject concretes everywhere;
   VajSkrivare/Dokumentbibliotek are interface-based throughout.
4. **Options validation coverage is the widest gap.** `ValidateOnStart` is
   present for: OMP `WebAppOptions`, `OmpAuthOptions`,
   `ArtifactUploadOptions`, `ContentWebAppModuleOptions`, LogSearch,
   EArkivChecker, Dokumentbibliotek, VajSkrivare, iKrock2 (4 of 5 types).
   Missing for: OMP push-event options (deliberate — out-of-range values
   are clamped via `Effective*` properties), all OMP service settings
   (`HostAgentSettings`, `WorkerManagerSettings`, `WorkerProcessSettings` —
   manual `Validate()` at first use instead), IbsPackager's own options,
   ODVGateway (manual pre-build check for trusted roots only; silent clamps
   elsewhere).
5. **`IOptions<T>` vs `IOptionsMonitor<T>` drift.** Lone `IOptions<T>`
   consumers in monitor-based services (`ContentTypeMapper.cs:10` in
   ODVGateway); `WorkerProcessHostedService.cs:31` keeps `IOptions<T>`
   deliberately (settings are static for the child-process lifetime);
   EArkivChecker snapshots `.Value` everywhere despite
   `reloadOnChange: true` on its JSON overlays.
6. **HTTP clients barely exist.** Only OMP HostAgent (two named clients,
   one with a custom TLS handler) and ODVGateway (one named client with no
   configuration). Every other repo has no outbound HTTP.
7. **Extension naming has three local conventions** — `AddOmp*`
   (platform), `Add<Feature>Options` in
   `<Feature>OptionsServiceCollectionExtensions.cs` (LogSearch,
   EArkivChecker), `Add{Product}Application` in
   `DependencyInjection/{X}ServiceCollectionExtensions.cs` (iKrock2) —
   plus repos with no extensions at all.
8. **Notable single-instance smells:** IbsPackager's `SqlConnectionFactory`
   name collision with OMP shared; iKrock2's dead `BackendOptions`
   registration; Dokumentbibliotek's duplicated `AddMemoryCache()`;
   ODVGateway's unconfigured named HttpClient.

## 3. Recommended standard pattern

The recommended pattern generalizes what OMP Web.Shared, LogSearch,
EArkivChecker (options) and iKrock2 (module services) already do. It
optimizes for: one obvious place to look per host, no duplicated
registration blocks, fail-fast configuration, and lifetimes that match the
host shape.

### 3.1 Where to register

- **Web module app:** `Program.cs` contains exactly three registration
  steps:
  1. `builder.AddOmpWebDefaults<TAppResource>(optionsSectionName)` —
  platform services, localization, auth, `WebAppOptions` validation.
  2. `builder.Services.Add{Module}Services(builder.Configuration)` — **one
     module extension** that registers every module-owned service and
     options type. For very small modules (≤ ~3 registrations) inline
     registration in `Program.cs` remains acceptable (iFrame module
     precedent).
  3. `app.UseOmpWebDefaults(optionsSectionName, mapRazorPages: ...)` —
     middleware.
- **Windows Service / worker:** `Host.CreateDefaultBuilder` (or
  `Host.CreateApplicationBuilder`) + `UseWindowsService`/`AddWindowsService`,
  with `ConfigureServices` calling the **same** `Add{Module}Services`
  extension from the shared Runtime/Application library, plus
  `AddHostedService<{Module}Worker>()`. Both hosts of a module must share
  the extension so registration blocks are never duplicated
  (EArkivChecker is the counter-example today).
- **Extension placement and naming:** put extensions in the module's
  Runtime/Application library (so every host can call them), in a file
  named `{Module}ServiceCollectionExtensions.cs` (optionally under a
  `DependencyInjection/` folder for larger modules, iKrock2 style).
  Methods: `Add{Module}Services` for the service block,
  `Add{Module}Options` for options-only composition (LogSearch style);
  return `IServiceCollection`/`OptionsBuilder<T>` for chaining. Platform
  cross-cutting extensions keep the `AddOmp*`/`UseOmp*` prefix on
  `WebApplicationBuilder` in `OpenModulePlatform.Web.Shared/Extensions/`.

### 3.2 Lifetime conventions

- **Scoped** — default for per-request data-access and business services
  in web apps (OMP Web.Shared `:182-188`, Dokumentbibliotek, VajSkrivare
  model). Razor Pages, minimal-API handlers and controllers are scoped
  consumers.
- **Singleton** — stateless infrastructure (connection factories,
  validators, hashers), caches and process-wide coordination state
  (`DocumentLibrarySchemaCache`, `JsonZebraConfigStore`), and **everything
  in plain worker/Windows-Service hosts** where there are no requests and
  services are stateless with per-call connections (WorkerManager,
  HostAgent, LogSearch.Service model).
- **Transient** — rare; framework contracts that require it
  (`IClaimsTransformation`) and short-lived per-resolution objects.
- **Never let a singleton capture a scoped service.** Bridge with
  `IServiceProvider`/`IServiceScopeFactory` + `CreateScope()` at the point
  of use (`DocumentLibrarySchemaCache.cs:53-54` is the reference).
- When both a concrete type and its interface must resolve to one
  instance, register the concrete first and forward the interface:
  `AddSingleton<Foo>(); AddSingleton<IFoo>(sp => sp.GetRequiredService<Foo>())`
  (HostAgent `Program.cs:37-40` idiom). Pick concrete-only *or*
  interface-based per repo and apply it in **all** hosts of that repo.

### 3.3 Options pattern

One options class per configuration section with a
`public const string SectionName`; register with

```csharp
services.AddOptions<TOptions>()
    .Bind(configuration.GetSection(TOptions.SectionName))
    .Validate(...simple delegate rules...)   // or an IValidateOptions<TOptions>
    .ValidateOnStart();
```

- Prefer an `IValidateOptions<TOptions>` implementation (registered as
  singleton) for cross-field or non-trivial rules (LogSearch/EArkivChecker
  model); inline `Validate(...)` delegates are fine for simple rules
  (VajSkrivare/iKrock2 model). `ValidateOnStart()` is **required** — fail
  at startup, not at first use.
- Consume `IOptions<T>` by default. Use `IOptionsMonitor<T>` only in
  singletons that must observe reloads — and then use it consistently
  within the repo (the `ContentTypeMapper` lone-`IOptions` case is the
  anti-pattern). If nothing observes reloads,
  do not set `reloadOnChange: true` on the JSON source.
- Windows-Service settings may keep the `XxxSettings` name (OMP service
  convention) but should move from manual `Validate()` calls to the same
  `ValidateOnStart` chain.

### 3.4 Hosted services

Implement background work as `BackgroundService` and register with
`AddHostedService<T>()` next to the module's other registrations. Register
conditionally behind configuration when the service is optional
(`AddOmpPushEventDispatcher` + `PushEvents:Dispatcher:Enabled`,
`OmpWebHostingExtensions.cs:193-205`). Worker-style background work that
OMP orchestrates belongs in an `IWorkerModuleFactory` plugin rather than a
hosted service (`examples/WorkerAppModule` is the reference).

### 3.5 HTTP clients

Always go through `IHttpClientFactory`: `AddHttpClient("Name")` for
endpoint-keyed clients or `AddHttpClient<TClient>()` for typed clients,
configured (BaseAddress, timeout, handler) at the single registration
site. Never `new HttpClient()` in services. Timeouts belong on the client
registration; per-call cancellation stays on the call site.

Motivation: this combination keeps each host's `Program.cs` readable at a
glance, removes the duplicated-block failure mode, makes misconfiguration
a startup error instead of a first-request error, matches the lifetimes
the host shape actually needs, and stays 100 % within the patterns the
existing shared infrastructure (`OmpWebHostingExtensions`, worker plugin
SPI, HostAgent) already teaches.

## 4. Migration notes per diverging repo

### OpenModulePlatform (Medium)

- Current: `ValidateOnStart` for `WebAppOptions`, `OmpAuthOptions`,
  `ArtifactUploadOptions` and `ContentWebAppModuleOptions`; service hosts
  validate settings manually at first use; `WorkerProcessHostedService`
  keeps a deliberate `IOptions<T>` snapshot (settings are static for the
  child-process lifetime); Portal keeps ~20 inline registrations.
- Migration (high-level): convert `HostAgentSettings`,
  `WorkerManagerSettings`, `WorkerProcessSettings` from manual
  `Validate()` to the standard chain; optionally extract Portal's inline
  block into `AddPortalServices`.
- Priority: Medium (platform-wide blast radius; do in slices).

### IbsPackager (Medium)

- Current: no repo extension methods; all-singleton web app; own options
  unvalidated; worker options manually bound; `SqlConnectionFactory` name
  collision with OMP shared.
- Migration: add `AddIbsPackagerServices` + `AddIbsPackagerOptions` in
  `IbsPackager.Runtime`; move `OpenDocViewerOptions` to the
  `ValidateOnStart` chain; bind `HostAgentRpcOptions` through the options
  pattern in the worker factory (or keep manual binding but validate at
  startup); rename the module connection factory to remove the collision;
  decide scoped-vs-singleton for `IbsPackagerRepository` per §3.2.
- Priority: Medium.

### LogSearch (Low)

- Current: exemplary options pattern; only inconsistency is
  interface-forwarding in Service vs concrete-only injection in Web.
- Migration: pick one style (concrete-only is fine given fakes exist) and
  apply to both hosts; optionally fold the two singleton registrations
  into `AddLogSearchServices`.
- Priority: Low.

### EArkivChecker (Low)

- Current: identical registration block duplicated across Web and Service;
  eager `.Value` snapshots make `reloadOnChange` inert; same
  concrete/interface split as LogSearch.
- Migration: extract `AddEArkivCheckerRuntime` in `EArkivChecker.Runtime`
  and call it from both hosts; either switch long-lived consumers to
  `IOptionsMonitor<T>` or drop `reloadOnChange: true`; unify
  concrete/interface style.
- Priority: Low.

### Dokumentbibliotek (Low)

- Current: already close to the standard (validated options, scoped data
  services, correct scope bridging); registrations are a flat inline list;
  duplicated `AddMemoryCache()` call.
- Migration: extract `AddDokumentBibliotekServices`; remove the redundant
  `AddMemoryCache()`; keep the deliberate `IOptions`/`IOptionsMonitor`
  split but document it.
- Priority: Low.

### VajSkrivare (Low)

- Current: near-standard — interface-based scoped data layer, both options
  fully validated; only the missing module extension and one non-DI
  options read for startup logging.
- Migration: extract `AddSkrivarkopplingServices`; route the startup-log
  read through the bound options.
- Priority: Low.

### iKrock2 (Medium)

- Current: the model repo for module extensions (`AddIKrock2Application`);
  validation on 4 of 5 options types; `BackendOptions` is unvalidated and
  dead; scoped web services depend only on singletons (deliberate but
  unremarked).
- Migration: delete the dead `BackendOptions` registration (or wire and
  validate it); add `ValidateOnStart` if it is revived; document why
  `IKrock2DataService`/`IKrock2UserNameResolver` are scoped.
- Priority: Medium (dead config misleads operators; otherwise low).

### ODVGateway (Low)

- Current: manual pre-build validation covers only trusted roots; numeric
  limits silently clamp; one `IOptions<T>` island among
  `IOptionsMonitor<T>` consumers; named HttpClient registered without any
  configuration.
- Migration: move the manual validation into
  `IValidateOptions<ODVGatewayOptions>` + `ValidateOnStart`, and replace
  silent clamps with validation failures where a clamp could hide
  misconfiguration; align `ContentTypeMapper` on `IOptionsMonitor<T>`;
  configure the `"ODVGateway.RemoteInline"` client (timeout, handler
  limits) at registration.
- Priority: Low.
