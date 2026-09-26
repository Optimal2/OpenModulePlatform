# OMP+ODV conventions index

This directory collects the cross-repository convention audits. Each file
documents one discipline (code style, configuration, data access, dependency
injection, error handling, HTTP clients, logging, unit testing) and uses the
same fictional consumer names in every example.

## Fictional-consumer naming scheme

The OMP+ODV ecosystem includes private consumer repositories whose names,
paths, and per-repo configuration belong to the maintainers' private
companion repository. To keep the public documentation concrete and
internally consistent, every example below refers to one of seven fictitious
consumers. The mapping is fixed — pick the consumer that matches the
scenario, then use exactly the names in the corresponding column everywhere
(in a sentence, in a code block, in a file path, in a connection-string name,
in an options section name, in a module key, in a SQL schema name, in a
module-definition JSON filename). Do not mix consumers within one example.

| Field | Contoso | Fabrikam | Northwind | AdventureWorks | Tailwind | Globex | ODVGateway (real) |
|---|---|---|---|---|---|---|---|
| Repo directory | `<workspace>\Contoso` | `<workspace>\Fabrikam` | `<workspace>\Northwind` | `<workspace>\AdventureWorks` | `<workspace>\Tailwind` | `<workspace>\Globex` | `<workspace>\ODVGateway` |
| Solution | `Contoso.sln` | `Fabrikam.sln` | `Northwind.sln` | `AdventureWorks.sln` | `Tailwind.sln` | `Globex.sln` | `ODVGateway.sln` |
| Web project | `Contoso.Web/Contoso.Web.csproj` | `Fabrikam.Web/Fabrikam.Web.csproj` | `Northwind.Web/Northwind.Web.csproj` | `RazorPages/AdventureWorks.Web.RazorPages.csproj` | `src/Tailwind.Web/Tailwind.Web.csproj` | `Globex.Web/Globex.Web.csproj` | `src/ODVGateway/ODVGateway.csproj` |
| Worker project | `Contoso.Worker/Contoso.Worker.csproj` | `Fabrikam.Service/Fabrikam.Service.csproj` | `Northwind.Service/Northwind.Service.csproj` | — | — | `Globex.Backend/Globex.Backend.csproj` | — |
| Runtime library | `Contoso.Runtime/Contoso.Runtime.csproj` | `Fabrikam.Runtime/Fabrikam.Runtime.csproj` | `Northwind.Runtime/Northwind.Runtime.csproj` | — | — | `Globex.Application/Globex.Application.csproj` | — |
| Module key | `omp_contoso` | `omp_fabrikam` | `omp_northwind` | `omp_adventureworks` | `omp_tailwind` | `omp_globex` | `odvgateway` |
| Module-definition JSON | `contoso.module-definition.json` | `fabrikam.module-definition.json` | `northwind.module-definition.json` | `adventureworks.module-definition.json` | `tailwind.module-definition.json` | `globex.module-definition.json` | `odvgateway.module-definition.json` |
| SQL schema | `omp_contoso` | `omp_fabrikam` | `omp_northwind` | `omp_adventureworks` | external (no OMP-owned schema) | `omp_globex` | `odvgateway` (empty) |
| Setup SQL | `sql/1-setup-contoso.sql` | `sql/1-setup-fabrikam.sql` | `sql/1-setup-northwind.sql` | `Sql/01_setup_example_module.sql` | `Sql/01_initialize_tailwind_metadata.sql` | `sql/1-setup-globex.sql` | — |
| Resource class | `ContosoResource` | `FabrikamResource` | `NorthwindResource` | `AdventureWorksResource` | `TailwindResource` | `GlobexResource` | — |
| Options section | `WebApp` | `Fabrikam` | `Northwind` | `AdventureWorks` | `Tailwind` (PrinterDatabaseCatalog, ZebraConfig) | `Backend`, `WorkOrder`, `OmpDatabase`, `MLLPerformance` | `ODVGateway` |
| Options class | `OpenDocViewerOptions` | `FabrikamOptions` | `NorthwindOptions` | `AdventureWorksOptions` | `PrinterDatabaseCatalogOptions`, `ZebraConfigOptions` | `BackendOptions`, `WorkOrderOptions`, `OmpDatabaseOptions`, `SqlServerOptions`, `MLLPerformanceOptions` | `ODVGatewayOptions` |
| Connection string | `ConnectionStrings:OmpDb` | `ConnectionStrings:OmpDb` (+ `Fabrikam:SourceConnectionStringTemplate`) | `ConnectionStrings:OmpDb` | `ConnectionStrings:OmpDb` (+ optional legacy `ConnectionStrings:AdventureWorksDb`) | `ConnectionStrings:PrinterDb_*` (named, via `PrinterDatabases:Items`) | `ConnectionStrings:OmpDb` + `SqlServer:HostConnectionString` | — (no database) |
| Connection-factory class | `ContosoConnectionFactory` | `FabrikamConnectionFactory` | `NorthwindConnectionFactory` | shared OMP `SqlConnectionFactory` (with local `AdventureWorksDataStore`) | `SqlConnectionFactory` (scoped, catalog-keyed) | `OmpConnectionFactory` + `SqlConnectionFactory` | — |
| Worker factory | `ContosoWorkerFactory` | `FabrikamWorker` (hosted service) | `NorthwindWorker` (hosted service) | — | — | `WorkOrderBackgroundService` | — |
| Validation style | `ValidateOnStart` (web) + manual `Validate()` (worker) | `IValidateOptions<FabrikamOptions>` + `ValidateOnStart` | `IValidateOptions<NorthwindOptions>` + `ValidateOnStart` | `IValidateOptions<AdventureWorksOptions>` + `ValidateOnStart` | inline delegate `Validate(...)` + `ValidateOnStart` | inline delegate + `ValidateOnStart` (4 types) | manual pre-build only |
| Lifetimes (web) | all singleton | all singleton | all singleton | scoped data services + singleton infra | scoped data layer + singleton stores | scoped web services + 12 singletons in extension | 9 singletons |
| Reference prefix used in SQL examples | `contoso_channel:` | — | — | — | — | — | — |
| Reference channel-type reconciliation SP | `omp_<module>.usp_ReconcileChannelTypeArtifactRequirements` (canonical template) | — | — | — | — | — | — |
| Service-account / Windows-service name | `OMP.Contoso` | `OMP.Fabrikam` | `OMP.Northwind` | — | — | — | — |

Notes that apply to every consumer example:

- Replace `<module>` in module-owned SQL/template text with the exact module key
  (e.g. `omp_contoso`, `omp_fabrikam`, `omp_tailwind`).
- The per-repo `<workspace>\…` path is shown only to make the example
  concrete; the public repository does not name real consumer repositories or
  paths. See `docs/OMP_COMPONENT_MANIFEST.md` ("Local external-consumer
  overlay") for how the public manifest references consumer build artifacts
  through an optional gitignored overlay.
- Pick one consumer per example and keep its column consistent throughout that
  example — never mix the names of two consumers in one sentence, code block
  or path. This rule is the source of truth for the next author; if a new
  example does not fit any of the seven columns, extend the table here first
  and then write the example using the new column.

## Convention audits

| File | Topic |
|---|---|
| `code-style.md` | Cross-repository code-style audit (editorconfig, build props, CPM, SDK pins, analyzers, JS/TS tooling) |
| `configuration.md` | appsettings, options classes, validation, connection strings, secrets, overlays |
| `data-access.md` | ADO.NET/Dapper, connection factories, SQL location, transactions, module schema |
| `dependency-injection.md` | registration locations, lifetimes, extension methods, hosted services, options pattern, HTTP clients |
| `error-handling.md` | exception policy, problem details, logging correlation |
| `http-clients.md` | named vs typed clients, handlers, timeouts, retries |
| `logging.md` | NLog layout, scopes, sinks, log-level policy |
| `unit-testing.md` | xUnit, fixtures, theory data, naming, parallelism |
