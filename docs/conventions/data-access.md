# Data Access Convention

This document is the result of a read-only audit of the .NET repositories in the OMP+ODV ecosystem. It maps how each repository accesses SQL Server today, highlights divergences, and recommends a single standard pattern for future work.

> **Scope:** read-only investigation. No source code was changed. No version bumps.
> Repositories audited:
> `OpenModulePlatform`, `IbsPackager`, `LogSearch`, `EArkivChecker`, `Dokumentbibliotek`, `VajSkrivare`, `iKrock2`, `ODVGateway`.

---

## 1. Per-repo data access map

### OpenModulePlatform

| Concern | Pattern | Key locations |
|---|---|---|
| DB driver | ADO.NET raw (`Microsoft.Data.SqlClient` 7.0.2) | `Directory.Packages.props:12` |
| ORM | None | No Dapper / EF Core references found |
| Connection factory | Per-component `SqlConnectionFactory` singletons | `OpenModulePlatform.HostAgent.Runtime/Services/ISqlConnectionFactory.cs:9`, `SqlConnectionFactory.cs:6`; `OpenModulePlatform.Web.Shared/Services/SqlConnectionFactory.cs:14`; `OpenModulePlatform.WorkerManager.WindowsService/Services/SqlConnectionFactory.cs:10` |
| Connection string | `ConnectionStrings:OmpDb` from `IConfiguration` / `appsettings.json` | `OpenModulePlatform.Web.Shared/Services/SqlConnectionFactory.cs:45`; `OpenModulePlatform.Portal/appsettings.json:35`; `OpenModulePlatform.HostAgent.WindowsService/appsettings.json:3` |
| Pooling | Default ADO.NET pooling; no explicit settings | Every method creates/disposes a fresh connection |
| SQL location | Inline `const string sql = @"..."` in C# repositories; module `.sql` files for setup/initialize/validate | `OpenModulePlatform.Portal/Services/OmpAdminRepository.cs:43`; `OpenModulePlatform.Auth/Services/OmpAuthRepository.cs:436`; `OpenModulePlatform.Web.ContentWebAppModule/Services/ContentPageRepository.cs:37`; `sql/1-setup-openmoduleplatform.sql` |
| Stored procedures | Rare; only legacy/placeholder procs in core setup | `sql/1-setup-openmoduleplatform.sql:1089`, `:2511`, `:2807` |
| Repository pattern | Dedicated `*Repository` / `*Service` classes, injected via DI | `OpenModulePlatform.Portal/Services/OmpAdminRepository.cs:11`; `OpenModulePlatform.Web.Shared/Extensions/OmpWebHostingExtensions.cs:170` |
| Transactions | Explicit `SqlTransaction` only | `OpenModulePlatform.Auth/Services/OmpAuthRepository.cs:386`; `OpenModulePlatform.Web.ContentWebAppModule/Services/ContentPageRepository.cs:346`; `OpenModulePlatform.Portal/Services/OmpUserAdminRepository.cs:163` |
| Module DB pattern | Module-definition JSON owns `schemaName` and `sqlScripts`; scripts embedded as base64 with SHA-256 | `omp_core.module-definition.json:66`; `OpenModulePlatform.Portal/omp_portal.module-definition.json:59`; `OpenModulePlatform.Web.ContentWebAppModule/content_webapp.module-definition.json` |
| Core table relationship | Module schemas created separately; module tables FK into `omp.*` core tables; init scripts seed `omp.Modules`, `omp.Apps`, `omp.Permissions`, etc. | `OpenModulePlatform.Web.ContentWebAppModule/Sql/1-setup-content-webapp.sql:34`, `:92`; `examples/WebAppModule/Sql/2-initialize-example-webapp.sql:68`, `:102`, `:135` |

### IbsPackager

| Concern | Pattern | Key locations |
|---|---|---|
| DB driver | ADO.NET raw (`Microsoft.Data.SqlClient` 7.0.2) | `Directory.Packages.props:7`; `IbsPackager.Runtime/IbsPackager.Runtime.csproj:10` |
| Connection factory | `IbsPackagerConnectionFactory` singleton | `IbsPackager.Runtime/Services/IbsPackagerConnectionFactory.cs:15-23` |
| Connection string | `ConnectionStrings:OmpDb`; empty in production, dev uses `(localdb)\MSSQLLocalDB` | `IbsPackager.Web/appsettings.json:3`; `IbsPackager.Web/appsettings.Development.json:9` |
| SQL location | Inline const SQL strings in one large repository; `.sql` files for module setup/init/validate | `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:26-73`, `:115-125`, `:429-470`; `sql/1-setup-ibspackager.sql` |
| Stored procedures | One setup-time proc: `usp_ReconcileChannelTypeArtifactRequirements`, called from code | `sql/1-setup-ibspackager.sql:209-294`; `IbsPackagerRepository.cs:1800-1807` |
| Repository pattern | Single `IbsPackagerRepository` (~4 000 lines, ~80 methods); one page bypasses it | `IbsPackager.Runtime/Services/IbsPackagerRepository.cs`; `IbsPackager.Web/Pages/Channels/Index.cshtml.cs:128-135` |
| Transactions | Mostly implicit; three methods use explicit `SqlTransaction`; some SQL batches contain `BEGIN TRANSACTION` | `IbsPackagerRepository.cs:472-501`, `:687-715`, `:3861-3881`; `:1360`, `:3673`, `:3768` (T-SQL transactions) |
| Module DB pattern | `omp_ibs_packager` schema owned by module; FKs into `omp.AppInstances`, `omp.WorkerInstances`, `omp.Artifacts`; seeds core catalog tables | `ibs_packager.module-definition.json:4`, `:8`, `:58-94`; `sql/1-setup-ibspackager.sql`; `sql/2-initialize-ibspackager.sql` |

### LogSearch

| Concern | Pattern | Key locations |
|---|---|---|
| DB driver | ADO.NET raw (`Microsoft.Data.SqlClient` 7.0.2) | `Directory.Packages.props:7`; `LogSearch.Runtime/LogSearch.Runtime.csproj:3` |
| Connection factory | `LogSearchConnectionFactory` / `ILogSearchConnectionFactory`; supports OMP DB and external source DBs | `LogSearch.Runtime/LogSearchConnectionFactory.cs:7`, `:19`, `:32` |
| Connection string | OMP from `ConnectionStrings:OmpDb`; source DBs from template or `SqlConnectionStringBuilder` clone | `LogSearch.Web/appsettings.json:14-16`; `LogSearchConnectionFactory.cs:40-69`; guards against credentials at `:71-94` |
| SQL location | Inline raw-string SQL in C# (OMP and source DB queries); `.sql` files for module setup/init/validate | `LogSearch.Runtime/LogSearchRepository.cs:44-48`, `:820-878`; `LogSearch.Runtime/LogSearchJobProcessor.cs:233-251`, `:535-563`; `sql/1-setup-log-search.sql` |
| Stored procedures | None | — |
| Repository pattern | Dedicated `LogSearchRepository`; `LogSearchJobProcessor` also queries source DBs directly | `LogSearch.Runtime/LogSearchRepository.cs:7`; `LogSearchJobProcessor.cs` |
| Transactions | Mostly implicit; three methods use explicit `SqlTransaction`; `SqlBulkCopy` paths are not transaction-wrapped | `LogSearchRepository.cs:201-228`, `:284-298`, `:777-811` |
| Module DB pattern | `omp_log_search` schema; no FKs into `omp.*`; linkage is application-level via init script | `log_search.module-definition.json:5`, `:9`, `:50-86`; `sql/1-setup-log-search.sql`; `sql/2-initialize-log-search.sql` |

### EArkivChecker

| Concern | Pattern | Key locations |
|---|---|---|
| DB driver | ADO.NET raw (`Microsoft.Data.SqlClient` 7.0.2) | `Directory.Packages.props:7`; `EArkivChecker.Runtime/EArkivChecker.Runtime.csproj:5` |
| Connection factory | `EArkivCheckerConnectionFactory` singleton with `OpenOmpConnectionAsync` and `CreateConnection` | `EArkivChecker.Runtime/EArkivCheckerConnectionFactory.cs:5`, `:19`, `:26` |
| Connection string | `ConnectionStrings:OmpDb` from `appsettings.json` | `EArkivChecker.Web/appsettings.json:3-4`; `EArkivChecker.Service/appsettings.json:2-3` |
| SQL location | Inline SQL strings in `EArkivCheckerRepository`; `.sql` files for module setup/init/validate/seed | `EArkivCheckerRepository.cs:24-29`, `:111-130`, `:369-374`; `sql/1-setup-earkiv-checker.sql` |
| Stored procedures | None | — |
| Repository pattern | Dedicated `EArkivCheckerRepository` / `IEArkivCheckerRepository`; no inline DB code in pages/services | `EArkivChecker.Runtime/EArkivCheckerRepository.cs:8`; `EArkivChecker.Web/Pages/Index.cshtml.cs:12`; `EArkivChecker.Runtime/EArkivCheckerScanProcessor.cs:13` |
| Transactions | Mostly implicit; one explicit `SqlTransaction` in `SaveTargetScanResultAsync` | `EArkivCheckerRepository.cs:508-586` |
| Module DB pattern | `omp_earkiv_checker` schema; depends on `omp.users`, `omp.notifications`; seeds core catalog tables | `earkiv_checker.module-definition.json:5`, `:9`, `:50-99`; `sql/1-setup-earkiv-checker.sql:10`; `sql/2-initialize-earkiv-checker.sql` |

### Dokumentbibliotek

| Concern | Pattern | Key locations |
|---|---|---|
| DB driver | ADO.NET raw (`Microsoft.Data.SqlClient` 7.0.2), inherited via `OpenModulePlatform.Web.Shared` | `OpenModulePlatform.Web.Shared/OpenModulePlatform.Web.Shared.csproj:11`; `OpenModulePlatform/Directory.Packages.props:12` |
| Connection factory | Uses shared `OpenModulePlatform.Web.Shared.SqlConnectionFactory`; local `DocumentLibraryDataStore` can switch to legacy DB | `OpenModulePlatform.Web.Shared/Services/SqlConnectionFactory.cs:14`; `Services/DocumentLibraryDataStore.cs:8`, `:42-45` |
| Connection string | `ConnectionStrings:OmpDb` and optional `ConnectionStrings:DokumentBibliotekDb` | `appsettings.json:20-22`; `DocumentLibraryDataStore.cs:23` |
| SQL location | Inline SQL strings in services; `.sql` files for module setup/init; legacy migrations in `database/migrations/*.sql` | `Services/DocumentLibraryDocumentService.cs:219-264`; `Services/DocumentLibrarySettingsService.cs:25`, `:54`; `Sql/01_setup_earkiv_dokumentbibliotek.sql` |
| Stored procedures | None called from C# | — |
| Repository pattern | Services act as repositories (`DocumentLibrary*Service`); no separate `Repository` namespace | `RazorPages/Program.cs:44-51`; `Services/DocumentLibraryDataStore.cs:8` |
| Transactions | Explicit `SqlTransaction` in write services; child helpers pass `SqlConnection` + `SqlTransaction` | `Services/DocumentLibraryImageService.cs:323-345`; `Services/DocumentLibraryFormService.cs:341-381`; `Services/DocumentLibraryDocumentService.cs:448-487` |
| Module DB pattern | `omp_earkiv_dokumentbibliotek` schema; dual legacy/OMP mode via `UseLegacyDataStore` | `earkiv_dokumentbibliotek.module-definition.json:4`, `:8`, `:44-81`, `:87-159`; `DocumentLibraryDataStore.cs:32-36`; `DatabaseMigrationService.cs:23-65` |

### VajSkrivare

| Concern | Pattern | Key locations |
|---|---|---|
| DB driver | Dapper over `Microsoft.Data.SqlClient` 7.0.2 | `src/Skrivarkoppling.Web/Skrivarkoppling.Web.csproj:12-13` |
| Connection factory | `SqlConnectionFactory` scoped; resolves connection string via configurable database catalog | `src/Skrivarkoppling.Web/Infrastructure/Data/SqlConnectionFactory.cs:7-24`; `src/Skrivarkoppling.Web/Infrastructure/Data/IDbConnectionFactory.cs:5-8` |
| Connection string | `ConnectionStrings:PrinterDb_*` names mapped through `PrinterDatabases:Items` | `src/Skrivarkoppling.Web/appsettings.json:61-74`, `:84-86` |
| SQL location | Inline SQL strings in Dapper repositories; table names interpolated from validated config | `src/Skrivarkoppling.Web/Infrastructure/Printers/DapperPrinterRepository.cs:16-31`; `Infrastructure/PrinterConnections/DapperPrinterConnectionRepository.cs:16-33` |
| Stored procedures | None | — |
| Repository pattern | `Dapper{Entity}Repository` implementing `I{Entity}Repository`, orchestrated by `{Entity}Service` | `DapperPrinterRepository.cs:9-11`; `Application/Printers/IPrinterRepository.cs:5-19`; `Application/Printers/PrinterService.cs:12-16` |
| Transactions | **None** — no `TransactionScope`, `BeginTransaction`, `Commit`, or `Rollback` found | — |
| Module DB pattern | Declares `schemaName: external`; no module-owned tables in OMP; runtime data lives in external printer databases | `vajskrivare.module-definition.json:8`, `:43-72`, `:166-188`; `Sql/01_initialize_vajskrivare_metadata.sql` |

### iKrock2

| Concern | Pattern | Key locations |
|---|---|---|
| DB driver | ADO.NET raw (`Microsoft.Data.SqlClient` 7.0.2) | `Directory.Packages.props:8`; `iKrock2.Application/iKrock2.Application.csproj:10` |
| Connection factories | Two singleton factories: `SqlConnectionFactory` (legacy source DBs) and `OmpConnectionFactory` (OMP DB) | `iKrock2.Application/Services/SqlConnectionFactory.cs:7`; `iKrock2.Application/Services/OmpConnectionFactory.cs:7` |
| Connection string | OMP from `ConnectionStrings:OmpDb`; legacy DBs from `SqlServer:HostConnectionString` + `PrimaryDatabase`/`KsDatabase` | `iKrock2.Web/appsettings.json:17-29`; `iKrock2.Application/DependencyInjection/IKrock2ApplicationServiceCollectionExtensions.cs:14-54` |
| SQL location | Inline SQL strings in repositories; `.sql` files for module setup/init/remove/validate | `iKrock2.Application/Services/DashboardRepository.cs:23-87`; `iKrock2.Application/Services/WorkOrderRepository.cs:21-23`, `:354-418`; `sql/1-setup-ikrock2.sql` |
| Stored procedures | Only SQL Server app-lock procs (`sp_getapplock`, `sp_releaseapplock`) | `WorkOrderRepository.cs:319`, `:971` |
| Repository pattern | Dedicated `*Repository` classes in `Application/Services`; web-layer `IKrock2DataService` facade | `DashboardRepository.cs:8`; `WorkOrderRepository.cs:9`; `iKrock2.Web/Services/IKrock2DataService.cs:8`; `iKrock2.Web/Services/IKrock2UserNameResolver.cs:9` (direct DB access) |
| Transactions | Explicit `SqlTransaction.Commit/Rollback` in write paths | `RegistrationWriteService.cs:84-114`, `:124-170`, `:180-211`; `WorkOrderRepository.cs:502-611` |
| Module DB pattern | `omp_ikrock` schema; seeds `omp.Modules`, `omp.Apps`, `omp.Permissions`, `omp.Artifacts`, etc.; runtime joins `omp.user_auth` | `ikrock.module-definition.json:4`, `:8`, `:49-85`; `sql/2-initialize-ikrock2.sql:46-510`; `IKrock2UserNameResolver.cs:53-69` |

### ODVGateway

| Concern | Pattern | Key locations |
|---|---|---|
| DB driver / ORM | **None** | `src/ODVGateway/ODVGateway.csproj:1-14` |
| Connection strings | **None** | `src/ODVGateway/appsettings.json:1-72` |
| SQL location | **None** | No `.sql` files; no inline SQL |
| Data layer | In-memory `ConcurrentDictionary` services + file-system resolver | `src/ODVGateway/Services/GatewaySessionStore.cs:10-242`; `src/ODVGateway/Services/DirectSourceFileResolver.cs:8-292` |
| Transactions | **None** | — |
| Module DB pattern | Declares `schemaName: odvgateway` but `sqlScripts: []`; no module-owned tables | `odvgateway.module-definition.json:8`, `:43-47` |

---

## 2. Comparison matrix

| Repository | DB access method | Connection handling | SQL location | Repository pattern | Transactions | Module DB pattern |
|---|---|---|---|---|---|---|
| OpenModulePlatform | ADO.NET raw (`Microsoft.Data.SqlClient`) | `SqlConnectionFactory` singleton; per-request open/dispose; `ConnectionStrings:OmpDb` | Inline C# + module `.sql` files | Dedicated `*Repository` / `*Service` classes | Explicit `SqlTransaction` | Module-definition SQL; module schema + FKs into `omp.*` |
| IbsPackager | ADO.NET raw (`Microsoft.Data.SqlClient`) | `IbsPackagerConnectionFactory` singleton; per-request open/dispose | Inline C# (one large repo) + module `.sql` files | Single `IbsPackagerRepository`; one page bypasses it | Mostly implicit; explicit `SqlTransaction` in 3 methods; some T-SQL transactions | Module-definition SQL; `omp_ibs_packager` schema + FKs into `omp.*` |
| LogSearch | ADO.NET raw (`Microsoft.Data.SqlClient`) | `LogSearchConnectionFactory`; OMP + external source DBs | Inline C# + module `.sql` files | `LogSearchRepository`; processor queries source DBs directly | Mostly implicit; explicit `SqlTransaction` in 3 methods | Module-definition SQL; `omp_log_search` schema; no FKs into `omp.*` |
| EArkivChecker | ADO.NET raw (`Microsoft.Data.SqlClient`) | `EArkivCheckerConnectionFactory` singleton; per-request open/dispose | Inline C# + module `.sql` files | Dedicated `EArkivCheckerRepository` | Mostly implicit; one explicit `SqlTransaction` | Module-definition SQL; `omp_earkiv_checker` schema; depends on `omp.users` / `omp.notifications` |
| Dokumentbibliotek | ADO.NET raw (`Microsoft.Data.SqlClient`) | Shared OMP `SqlConnectionFactory`; optional legacy DB | Inline C# + module `.sql` files + legacy migrations | Services act as repositories (`*Service`) | Explicit `SqlTransaction` in write paths | Module-definition SQL; `omp_earkiv_dokumentbibliotek` schema; dual legacy/OMP mode |
| VajSkrivare | **Dapper** over `Microsoft.Data.SqlClient` | `SqlConnectionFactory` scoped; database catalog maps keys to connection strings | Inline SQL; table names interpolated from validated config | `Dapper*Repository` + `{Entity}Service` | **None** | External-schema module; no OMP-owned tables |
| iKrock2 | ADO.NET raw (`Microsoft.Data.SqlClient`) | Two singleton factories (OMP + legacy source DBs) | Inline C# + module `.sql` files | Dedicated `*Repository` classes; web facade + one direct access helper | Explicit `SqlTransaction` | Module-definition SQL; `omp_ikrock` schema + core table seeds/joins |
| ODVGateway | **None** | **None** | **None** | In-memory / file-system services | **None** | Declares schema but `sqlScripts: []` |

### Divergences

1. **ORM inconsistency.** Seven repos use raw ADO.NET; `VajSkrivare` uses **Dapper**. This forces the ecosystem to maintain two query authoring styles and two parameter-passing conventions.
2. **Connection-factory shape.** Most factories expose `SqlConnection Create()`. `EArkivChecker` adds `OpenOmpConnectionAsync`. `LogSearch` adds source-DB support. `iKrock2` has two separate factories. `VajSkrivare` uses a catalog/key indirection. The DI lifetime varies (singleton vs scoped).
3. **Repository layer gaps.** `IbsPackager` concentrates almost all data access in a single 4 000-line repository and has one Razor Page that executes SQL directly (`Index.cshtml.cs:128-135`). `Dokumentbibliotek` uses `*Service` classes as repositories, blurring the data/business boundary. `iKrock2` has one web-layer class that queries `omp.user_auth` directly.
4. **Transaction handling inconsistency.** `VajSkrivare` has **no transactions** at all. `IbsPackager`, `LogSearch`, and `EArkivChecker` rely mostly on implicit/auto-commit transactions, with only a few explicit `SqlTransaction` paths. Some `IbsPackager` SQL batches contain server-side `BEGIN TRANSACTION` without a client-side transaction. This makes cross-operation consistency hard to reason about.
5. **Parameterization style.** Most repos use `cmd.Parameters.AddWithValue(...)`, which can cause plan-cache pollution and implicit type/size issues. `EArkivChecker` and parts of `OpenModulePlatform`/`iKrock2` use explicit `SqlParameter` with `SqlDbType`, which is safer.
6. **Module-schema ownership.** `LogSearch` and `VajSkrivare` do not declare FKs from module tables into `omp.*`. `Dokumentbibliotek` supports a legacy mode that bypasses the module-definition SQL entirely. `ODVGateway` declares a schema name but owns no database objects.
7. **Stored procedures.** Only `IbsPackager` has a runtime-called stored procedure; the rest use inline SQL. OpenModulePlatform core setup has a few placeholder procs, but they are not business-logic procs.

---

## 3. Recommended standard pattern

For all new code and refactors, use:

> **ADO.NET raw (`Microsoft.Data.SqlClient`) + `SqlConnectionFactory` + parameterized queries + explicit `SqlTransaction` + dedicated repository classes.**

### Why this standard

- **Consistency with the platform.** OpenModulePlatform itself, plus six of the eight audited repos, already use raw ADO.NET with a connection factory. Aligning on this removes the need for a second mental model.
- **Predictable performance and security.** Raw ADO.NET gives full control over command text, parameter types, execution plans, and transaction boundaries. It avoids Dapper's automatic query generation and EF Core's change-tracking overhead.
- **Simple dependency graph.** No extra NuGet packages beyond `Microsoft.Data.SqlClient`, which is already centrally versioned.
- **Fits the module-definition model.** Module-owned tables are created by versioned `.sql` scripts at OMP import time. Runtime code only queries those tables and the shared `omp.*` core tables.

### Required shape

1. **Factory.** A single `SqlConnectionFactory` registered as a singleton that reads `ConnectionStrings:OmpDb` and returns a new `SqlConnection` on each call. The factory must not open, hold, or pool connections itself.
2. **Connection usage.** Every data-access method opens and disposes its own connection:
   ```csharp
   await using var conn = _db.Create();
   await conn.OpenAsync(ct);
   ```
3. **Parameterization.** Use explicit `SqlParameter` with `SqlDbType` and size where possible. Avoid `AddWithValue` in hot paths.
4. **Repository layer.** All database access goes through a dedicated `I*Repository` / `*Repository` class. Pages, controllers, workers, and services must not issue SQL directly.
5. **Transactions.** Multi-statement write operations must use explicit `SqlTransaction`:
   ```csharp
   await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
   try { ...; await tx.CommitAsync(ct); }
   catch { await tx.RollbackAsync(CancellationToken.None); throw; }
   ```
   Pass the transaction object into helper methods rather than opening nested connections.
6. **Module DB ownership.** Module-specific tables live in a module-owned schema (e.g. `omp_myfeature`). Schema and seed data are delivered through the module-definition JSON (`sqlScripts` with phases `validate`, `setup`, `initialize`, `patch`) and embedded as base64 with SHA-256. Module tables that logically depend on core entities should declare foreign keys into `omp.*` tables where feasible.
7. **No runtime DDL.** Do not execute `CREATE TABLE`, `ALTER TABLE`, or `DROP TABLE` from C# except through the module-definition repair infrastructure.
8. **No `TransactionScope`.** Use explicit `SqlTransaction` only. `TransactionScope` is not needed and is not used anywhere in the ecosystem today.

---

## 4. Migration notes per diverging repo

### VajSkrivare
- **Current state:** Dapper-based repositories, no transactions, external-schema module with no OMP-owned tables.
- **Migration:** Replace Dapper calls with raw ADO.NET `SqlCommand`; introduce a singleton `SqlConnectionFactory`; add explicit `SqlTransaction` around multi-step service operations; keep the external database catalog abstraction if needed, but route all access through repositories.
- **Priority:** High — Dapper and missing transactions are the two largest divergences.

### IbsPackager
- **Current state:** One oversized repository (~4 000 lines), one page with inline SQL, mixed implicit/explicit/T-SQL transactions, one runtime stored procedure.
- **Migration:** Split `IbsPackagerRepository` into focused repositories by aggregate (channels, jobs, manual review, etc.); move the inline page query into a repository method; replace T-SQL transactions with explicit client-side `SqlTransaction`; evaluate whether the stored procedure can be inlined or kept as a setup-time artifact.
- **Priority:** Medium-High — structural risk from the monolithic repository and transaction inconsistency.

### LogSearch
- **Current state:** Mostly implicit transactions; `SqlBulkCopy` paths are not wrapped; source-DB queries live in the processor, not the repository.
- **Migration:** Wrap multi-statement OMP writes in explicit `SqlTransaction`; consider wrapping bulk operations in a transaction or using `SqlBulkCopy` with an external transaction; centralize source-DB access behind a source repository if the processor grows.
- **Priority:** Medium.

### EArkivChecker
- **Current state:** Good repository separation and explicit parameter typing, but only one explicit transaction path; everything else is auto-commit.
- **Migration:** Review write paths that touch multiple tables (e.g. alarm subscription + observation inserts) and wrap them in explicit `SqlTransaction`.
- **Priority:** Low-Medium.

### Dokumentbibliotek
- **Current state:** Services double as repositories; dual legacy/OMP data-store mode; explicit transactions already present.
- **Migration:** Extract pure data access into `*Repository` classes and keep services for orchestration/business rules; eventually retire legacy mode so the module-definition SQL is the single source of schema truth.
- **Priority:** Medium (legacy mode complicates the module-DB model).

### iKrock2
- **Current state:** Two factories, one web-layer direct DB access helper, explicit transactions in write paths.
- **Migration:** Move `IKrock2UserNameResolver` into a repository or shared OMP service; unify the OMP and legacy factories behind a clearer abstraction if both connection types are still needed.
- **Priority:** Low-Medium.

### OpenModulePlatform
- **Current state:** Already the reference implementation. Minor inconsistencies exist (`AddWithValue` vs explicit parameters, dynamic IN-list generation).
- **Migration:** Prefer explicit `SqlParameter` with type/size in new/refactored code; keep dynamic IN-list parameterization but centralize the helper.
- **Priority:** Low.

### ODVGateway
- **Current state:** No database access by design.
- **Migration:** If future features need a database, follow this standard from day one rather than introducing Dapper or EF Core.
- **Priority:** N/A.

---

## Output

- **File created:** `docs/conventions/data-access.md`
- **Validation:** Document contains per-repo file:line citations, comparison matrix, divergences, recommended standard, and migration notes.
- **Commit hash + push status:** See git log / push output below.
