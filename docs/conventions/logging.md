# Logging conventions for OMP+ODV

This document records the logging patterns found in the OMP+ODV repositories (source code only; `bin/`, `obj/`, `artifacts/`, `node_modules/`, `dist/` and other build output were excluded from the audit).

## 1. Per-repo logging map

### OpenModulePlatform

- **Library:** NLog (via `NLog.Extensions.Hosting` / `NLog.Web.AspNetCore`) bridged through `Microsoft.Extensions.Logging` / `ILogger<T>`.
  - `OpenModulePlatform.Web.Shared/Extensions/OmpWebLoggingExtensions.cs:2`
  - `OpenModulePlatform.HostAgent.WindowsService/Program.cs:5`
  - `Directory.Packages.props:27-28`
- **Configuration:** JSON-only. Each executable host has an `"NLog"` section in `appsettings.json`.
  - `OpenModulePlatform.HostAgent.WindowsService/appsettings.json:73-109`
  - `OpenModulePlatform.Portal/appsettings.json:52-88`
  - `OpenModulePlatform.WorkerManager.WindowsService/appsettings.json:34-70`
  - Bootstrap wiring: `OpenModulePlatform.Web.Shared/Extensions/OmpWebLoggingExtensions.cs:12-23`
- **Levels used:** `LogDebug`, `LogInformation`, `LogWarning`, `LogError`, `LogCritical` in code; NLog rules use `Info`/`Debug` thresholds.
  - `OpenModulePlatform.HostAgent.Runtime/Services/HostAgentEngine.cs:84` (Warning), `:94` (Information), `:370` (Error)
  - `OpenModulePlatform.WorkerProcessHost/Services/WorkerProcessHostedService.cs:77` (Critical)
- **Structured logging:** Dominant pattern is message templates with named placeholders.
  - `OpenModulePlatform.HostAgent.Runtime/Services/HostAgentEngine.cs:85`: `"HostAgent skipped cycle ... HostKey={HostKey}, CurrentService={CurrentService}"`
  - Exception-first pattern: `OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerManagerHostedService.cs:458`
  - One backend outlier uses interpolated string building: `OpenModulePlatform.HostAgent.Runtime/Services/DeploySetConsistencyService.cs:40-48`
  - Browser code uses plain `console.warn` strings: `OpenModulePlatform.Web.Shared/wwwroot/js/portal-topbar.js:582`
- **Destinations:** NLog writes to rolling file + console in every app.
  - `OpenModulePlatform.HostAgent.WindowsService/appsettings.json:81-89`
  - Emergency startup fallback writes a one-off text file: `OpenModulePlatform.HostAgent.WindowsService/Program.cs:120-138`
- **Correlation IDs:** No request/correlation middleware or `Activity.Current.Id` usage. A domain-specific `CorrelationKey` exists only for push-event outbox deduplication.
  - `OpenModulePlatform.EventPublisher.Abstractions/PushEvent.cs:11`
  - `OpenModulePortal.Tests/Services/PushEventTests.cs:15`

### IbsPackager

- **Library:** `Microsoft.Extensions.Logging` / `ILogger<T>` only. No NLog/Serilog/log4net in source.
  - `Directory.Packages.props:10` pins `Microsoft.Extensions.Logging.Abstractions` `10.0.9`
  - `IbsPackager.Worker/IbsPackagerWorkerFactory.cs:18,22,27,34` receives `ILoggerFactory` from the host
- **Configuration:** Standard MEL `Logging:LogLevel` sections only.
  - `IbsPackager.Web/appsettings.json:5-9`
  - `IbsPackager/.dev/worker-host/appsettings.json:22-27`
- **Levels used:** `LogDebug`, `LogInformation`, `LogWarning`, `LogError`. No `LogCritical`.
  - `IbsPackager.ChannelTypes.FileDrop/FileDropChannelType.cs:48` (Information), `:64` (Warning), `:1219` (Error)
  - `IbsPackager.Runtime/Services/IbsPackagerWorkerEngine.cs:52` (Information), `:61` (Warning), `:251` (Error)
- **Structured logging:** Almost exclusively structured message templates.
  - `IbsPackager.ChannelTypes.FileDrop/FileDropChannelType.cs:56-60`
  - One string-only outlier: `FileDropChannelType.cs:1101,1107`
- **Destinations:** None configured in repo; relies on host-provided MEL providers.
- **Correlation IDs:** None found.

### LogSearch

- **Library:** NLog bridged through MEL/ILogger.
  - `LogSearch.Service/Program.cs:3,19-20` (`using NLog.Extensions.Logging;`, `ClearProviders(); AddNLog();`)
  - `LogSearch.Web/Program.cs:22` uses `app.Logger.LogError(...)`
- **Configuration:** NLog section in service `appsettings.json`; web project delegates to `OpenModulePlatform.Web.Shared` via `AddOmpWebDefaults`.
  - `LogSearch.Service/appsettings.json:30-66`
  - `LogSearch.Web/appsettings.json:36-41`
- **Levels used:** `Information`, `Warning`, `Error` in source. No Debug/Critical/Trace calls.
  - `LogSearch.Service/LogSearchWorker.cs:30` (Information), `:93` (Warning), `:56` (Error)
  - `LogSearch.Runtime/LogSearchJobProcessor.cs:106` (Warning), `:191` (Information)
- **Structured logging:** All C# logs use structured templates.
  - `LogSearch.Service/LogSearchWorker.cs:30`: `"LogSearch worker started as {WorkerId}."`
  - `LogSearch.Runtime/LogSearchJobProcessor.cs:106`: `"LogSearch source {SourceKey} timed out for job {SearchJobId} after {SourceTimeoutSeconds} second(s)."`
- **Destinations:** File + console via NLog in service; web inherits shared platform setup.
  - `LogSearch.Service/appsettings.json:38-46`
- **Correlation IDs:** None found.

### EArkivChecker

- **Library:** NLog + MEL/ILogger.
  - `EArkivChecker.Service/Program.cs:3,27-28` (`using NLog.Extensions.Logging;`, `ClearProviders(); AddNLog();`)
  - `EArkivChecker.Web/Program.cs:7` uses `AddOmpWebDefaults<EArkivCheckerResource>`
- **Configuration:** Service NLog in `appsettings.json`; web standard MEL section only.
  - `EArkivChecker.Service/appsettings.json:13-47`
  - `EArkivChecker.Web/appsettings.json:27-30`
- **Levels used:** `Information`, `Warning`, `Error` in code; `Info` thresholds in NLog rules.
  - `EArkivChecker.Service/EArkivCheckerWorker.cs:27` (Information), `:41` (Error)
  - `EArkivChecker.Runtime/EArkivCheckerScanProcessor.cs:71` (Information), `:79` (Warning), `:150` (Error)
- **Structured logging:** Structured templates dominate; front-end uses plain `console.warn` strings.
  - `EArkivChecker.Runtime/EArkivCheckerScanProcessor.cs:71-72`: `"Created {NotificationCount} EArkivChecker alarm notification(s) for target {TargetId}."`
- **Destinations:** File + console in service; web uses shared OMP NLog defaults.
  - `EArkivChecker.Service/appsettings.json:21-29,44-47`
- **Correlation IDs:** None. `correlationKey` is used only for push-event creation, not logging.

### Dokumentbibliotek

- **Library:** MEL/ILogger with NLog provider via `OpenModulePlatform.Web.Shared`.
  - `Services/DocumentLibraryImageService.cs:5`
  - `RazorPages/Program.cs:57` creates a named logger from `ILoggerFactory`
- **Configuration:** Root `appsettings.json` contains full NLog section and is linked into the web project.
  - `appsettings.json:23-64`
  - `RazorPages/OpenModulePlatform.Web.eArkivDokumentbibliotek.RazorPages.csproj:11`
- **Levels used:** `LogDebug`, `LogInformation`, `LogWarning`, `LogError`. No Critical/Trace.
  - `Services/DocumentLibraryImageService.cs:248` (Debug), `:237` (Information)
  - `RazorPages/Infrastructure/DocumentLibraryEndpointMapping.cs:21` (Warning)
  - `RazorPages/Program.cs:67` (Error)
- **Structured logging:** Mostly structured; a few plain-string warnings/errors.
  - Structured: `Services/DocumentLibraryImageService.cs:248`: `"Image upload resolved root folder: {RootFolder}"`
  - Plain string: `Services/DocumentLibraryFormService.cs:376`: `"CreateFormAsync skipped forvaltning mapping save because mapping tables were not detected."`
- **Destinations:** File + console via NLog.
  - `appsettings.json:30-39`
- **Correlation IDs:** None found.

### VajSkrivare

- **Library:** MEL/ILogger with NLog provider via `OpenModulePlatform.Web.Shared`.
  - `src/Skrivarkoppling.Web/appsettings.json:8` (NLog section)
  - `src/Skrivarkoppling.Web/Program.cs` uses `AddOmpWebDefaults`
- **Configuration:** `appsettings.json` contains both MEL and NLog sections; `appsettings.Development.json` overrides levels.
  - `src/Skrivarkoppling.Web/appsettings.json:2-44`
  - `src/Skrivarkoppling.Web/appsettings.Development.json:2-6`
- **Levels used:** `Information`, `Warning`, `Error`.
  - `src/Skrivarkoppling.Web/Program.cs:70` (Information), `:141` (Warning), `:124` (Error)
- **Structured logging:** Structured templates throughout `ILogger` calls.
  - `src/Skrivarkoppling.Web/Program.cs:70`: `"Skrivarkoppling web app started. Environment={Environment}; ContentRoot={ContentRoot}; WebRoot={WebRoot}"`
  - Bootstrap diagnostics use interpolated strings: `src/Skrivarkoppling.Web/Diagnostics/BootstrapDiagnostics.cs:31`
- **Destinations:** NLog file + console; IIS stdout capture; custom bootstrap fallback files.
  - `src/Skrivarkoppling.Web/appsettings.json:16-24`
  - `src/Skrivarkoppling.Web/web.config:8`
- **Correlation IDs:** Uses ASP.NET Core `HttpContext.TraceIdentifier` as request ID.
  - `src/Skrivarkoppling.Web/Program.cs:107,127,149`

### iKrock2

- **Library:** NLog + MEL/ILogger.
  - `Directory.Packages.props:16` pins `NLog.Web.AspNetCore` `6.1.4`
  - `iKrock2.Backend/Program.cs:14-18` wires NLog
  - `iKrock2.Web/Program.cs:17` uses `AddOmpWebDefaults<IKrock2Resource>`
- **Configuration:** NLog sections in backend and web `appsettings.json`.
  - `iKrock2.Backend/appsettings.json:29-65`
  - `iKrock2.Web/appsettings.json:47-83`
- **Levels used:** `Information`, `Warning`, `Error` in source. No Debug/Critical/Trace.
  - `iKrock2.Backend/Services/WorkOrderBackgroundService.cs:23` (Information), `:47` (Error), `:178` (Warning)
  - `iKrock2.Web/Services/IKrock2UserNameResolver.cs:40` (Warning)
- **Structured logging:** Structured templates dominate; the operation descriptor in `IKrock2DataService` is sometimes built with string concatenation before the log call.
  - Structured: `iKrock2.Backend/Services/WorkOrderBackgroundService.cs:23`: `"iKrock2 work-order service started on {HostName}."`
  - Concatenated descriptor: `iKrock2.Web/Services/IKrock2DataService.cs:43,104,251`
- **Destinations:** File + console in both apps.
  - `iKrock2.Backend/appsettings.json:37-45`
  - `iKrock2.Web/appsettings.json:55-63`
- **Correlation IDs:** None. Web adds `X-iKrock2-Web-Node` header (machine name), but it is not a correlation ID.

### ODVGateway

- **Library:** `Microsoft.Extensions.Logging` only (default ASP.NET Core pipeline).
  - `src/ODVGateway/Program.cs:72` (`app.Logger`)
  - `src/ODVGateway/Program.cs:179,183` (`ILoggerFactory`)
  - `src/ODVGateway/Services/OpenDocViewerIndexRenderer.cs:18`
- **Configuration:** Standard MEL `Logging:LogLevel` in `appsettings.json`; no NLog/Serilog setup.
  - `src/ODVGateway/appsettings.json:2-7`
  - `src/ODVGateway/web.config:29` (`stdoutLogEnabled="false"`)
- **Levels used:** `Warning` (most common), `Information`, `Error`. No Debug/Trace/Critical.
  - `src/ODVGateway/Program.cs:210` (Warning), `:224` (Information)
  - `src/ODVGateway/Services/OpenDocViewerDistResolver.cs:34` (Error)
- **Structured logging:** All logs use structured message templates.
  - `src/ODVGateway/Program.cs:210`: `"Rejected /prep because the gateway session store is at capacity. ActiveSessions={ActiveSessions}, MaxConcurrentSessions={MaxConcurrentSessions}"`
- **Destinations:** Default ASP.NET Core console/debug providers only; no explicit file/EventLog/Seq/DB sinks.
- **Correlation IDs:** None found.

### OpenDocViewer

- **Library:** Custom browser loggers (`systemLogger`, `userLogger`) plus `morgan` on the Node server side.
  - `src/logging/systemLogger.js:243`
  - `src/logging/userLogger.js:129`
  - `server/system-log-server.js:34` (`morgan`)
- **Configuration:** Runtime config in `public/odv.config.js` and optional `public/odv.site.config.js`.
  - `public/odv.config.js:792-797` (`systemLog`)
  - `public/odv.config.js:431-498` (`userLog`)
  - `src/logging/systemLogger.js:81-168` resolves config precedence
- **Levels used:** `debug`, `info`, `warn`, `error` in `systemLogger`; `userLogger` has no levels.
  - `src/logging/systemLogger.js:41-44,175-178`
- **Structured logging:** Mixed. Backend POST and user logger are structured JSON; browser console output is interpolated string.
  - Structured JSON POST: `src/logging/systemLogger.js:426-433`
  - Interpolated console line: `src/logging/systemLogger.js:381-383`
  - `morgan` uses Apache `combined` string format: `server/system-log-server.js:159`
- **Destinations:** Browser console; HTTP POST to `/log` and `/userlog/record`; rolling files written by Node servers.
  - `server/system-log-server.js`, `server/user-log-server.js`
- **Correlation IDs:** No explicit correlation/operation ID. `sessionId`/`iframeId` are captured in user logs for session grouping.

### AgentDocMap

- **Library:** Node.js built-in `console` only.
  - `src/cli.js:6,85,90`
  - `test/testUtils.js:30`
- **Configuration:** None.
- **Levels used:** Effectively `info` (`console.log`), `error` (`console.error`), `warn` (`console.warn`). No debug/trace.
- **Structured logging:** None — string interpolation only.
  - `src/cli.js:85`: `console.log(`AgentDocMap wrote ${result.outputFiles.length} files to ${result.outDir}`)`
- **Destinations:** `stdout`/`stderr` only.
- **Correlation IDs:** None.

## 2. Comparison matrix

| Repo | Library | Configuration | Levels used | Structured? | Destinations | Correlation/context IDs |
|---|---|---|---|---|---|---|
| **OpenModulePlatform** | NLog + MEL/ILogger | `appsettings.json` `"NLog"` sections | Debug/Info/Warning/Error/Critical | Yes (templates), some console strings | File + console | Push-event `CorrelationKey` only |
| **IbsPackager** | MEL/ILogger only | `appsettings.json` `Logging:LogLevel` only | Debug/Info/Warning/Error | Yes (templates), one string outlier | Host-provided | None |
| **LogSearch** | NLog + MEL/ILogger | Service `appsettings.json` NLog section; web shared defaults | Info/Warning/Error | Yes | File + console | None |
| **EArkivChecker** | NLog + MEL/ILogger | Service `appsettings.json` NLog; web MEL only | Info/Warning/Error | Yes (templates); front-end console strings | File + console | None |
| **Dokumentbibliotek** | NLog + MEL/ILogger (via Web.Shared) | Root `appsettings.json` NLog section | Debug/Info/Warning/Error | Yes; a few plain strings | File + console | None |
| **VajSkrivare** | NLog + MEL/ILogger (via Web.Shared) | `appsettings.json` NLog section | Info/Warning/Error | Yes; bootstrap strings | File + console + IIS stdout + bootstrap fallback | `HttpContext.TraceIdentifier` |
| **iKrock2** | NLog + MEL/ILogger | `appsettings.json` NLog sections | Info/Warning/Error | Yes; concatenated descriptors in one service | File + console | None |
| **ODVGateway** | MEL/ILogger only | `appsettings.json` `Logging:LogLevel` only | Info/Warning/Error | Yes | Default console/debug only | None |
| **OpenDocViewer** | Custom `systemLogger`/`userLogger`, `morgan` | `odv.config.js` / `odv.site.config.js` runtime config | debug/info/warn/error (`systemLogger`) | JSON in transit; string in browser console | Browser console, HTTP POST, rolling files | `sessionId`/`iframeId` in user logs |
| **AgentDocMap** | `console` only | None | log/error/warn | No | stdout/stderr | None |

### Key divergences

- **.NET library split:** 6 of 8 .NET repos use NLog as the provider; **IbsPackager** and **ODVGateway** rely on plain MEL.
- **Configuration split:** NLog-based .NET repos configure logging in `appsettings.json`; IbsPackager and ODVGateway only use the MEL `Logging:LogLevel` section.
- **JS ecosystem split:** OpenDocViewer has a custom logger + `morgan`; AgentDocMap uses raw `console`.
- **Correlation IDs:** Only **VajSkrivare** (`TraceIdentifier`) and **OpenDocViewer** (`sessionId`/`iframeId` for user logs) have any request/session context in logs.

## 3. Recommended standard pattern

### .NET ecosystem

**Use NLog as the provider and `Microsoft.Extensions.Logging` (`ILogger<T>`) as the abstraction, configured entirely in `appsettings.json`.**

- NLog is already the dominant provider across OMP services and web apps, pinned centrally in `OpenModulePlatform/Directory.Packages.props:27-28` (`NLog.Extensions.Hosting` and `NLog.Web.AspNetCore` `6.1.4`).
- Web apps should continue to inherit NLog through `OpenModulePlatform.Web.Shared/Extensions/OmpWebLoggingExtensions.cs:12-23` (`ClearProviders()` + `UseNLog(...)`).
- Service/worker hosts should call `logging.ClearProviders(); logging.AddNLog();` (or equivalent host setup) and keep the NLog section in `appsettings.json`.
- Use MEL log levels (`Debug`/`Information`/`Warning`/`Error`/`Critical`) in code; map them to NLog rules using `minLevel`/`maxLevel`.
- Prefer structured message templates (`"Event {Property}"`, value) over string interpolation or pre-built strings.
- Standard destinations: rolling file + console. Use the shared layout `${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}` unless a repo needs something different.
- Add an ASP.NET Core request-correlation middleware that pushes `HttpContext.TraceIdentifier` (or `Activity.Current.Id`) into `NLog.MappedDiagnosticsLogicalContext`/`AsyncLocal` scope so every log line in a request carries the same ID.

### JS/npm ecosystem

**Use structured JSON logging with level support everywhere; avoid raw `console.log` in production code.**

- For browser code, keep a thin wrapper similar to OpenDocViewer's `systemLogger` that supports `debug/info/warn/error`, writes to `console` in development, and POSTs structured JSON in production when configured.
- For Node.js server code, replace ad-hoc file writers and `morgan` string formats with a single structured logger such as **Pino** or **Winston**, writing NDJSON to stdout/files and leaving ingestion to the host/log shipper.
- Keep runtime configuration separate from source (environment variables or site config files), and avoid `console.log`/`console.error` outside of CLI startup/help output.
- Carry an explicit `requestId`/`sessionId` in every log envelope and propagate it across HTTP boundaries.

## 4. Migration notes per diverging repo

### IbsPackager

- **Current state:** MEL/ILogger only; no NLog config or package references.
- **Migration:** Add `NLog.Extensions.Hosting` to the worker/runtime projects and `NLog.Web.AspNetCore` to the web project (or reference `OpenModulePlatform.Web.Shared` and call `AddOmpWebDefaults`). Add `"NLog"` sections to `appsettings.json`, wire `ClearProviders()` + `AddNLog()` in worker/service `Program.cs`, and convert the remaining string-only log call to a template.
- **Priority:** Medium. Logging works today via the host, but it is inconsistent with the rest of OMP and gives no file/console control from the repo itself.
- **Risk:** Low. NLog is already a transitive dependency through shared platform packages in many deployments.

### ODVGateway

- **Current state:** Plain ASP.NET Core MEL logging; only console/debug output by default.
- **Migration:** Add `NLog.Web.AspNetCore` and an `appsettings.json` `"NLog"` section, then call `builder.Host.UseNLog()` (or consume `OpenModulePlatform.Web.Shared` if appropriate). Keep existing structured templates; they already follow the convention.
- **Priority:** Medium. ODVGateway is a small, focused gateway; adding NLog gives consistent file logging and makes production troubleshooting easier.
- **Risk:** Low. No logging behavior change other than adding a file target.

### AgentDocMap

- **Current state:** Raw `console.log`/`console.error`/`console.warn`; no levels or structured output.
- **Migration:** Introduce a small logger wrapper (or a dependency like `pino`) that supports `debug/info/warn/error` and outputs JSON or leveled text. Replace all `console.*` calls in `src/cli.js` and `test/testUtils.js`.
- **Priority:** Low. AgentDocMap is a development/CLI utility; consistent logging is nice but not operationally critical.
- **Risk:** Very low.

### OpenDocViewer

- **Current state:** Custom browser loggers + `morgan`/custom file writers on the server.
- **Migration (alignment, not rewrite):** Keep the browser `systemLogger`/`userLogger` wrappers but ensure they always emit structured JSON to the backend and use leveled messages consistently. On the server side, consider replacing `morgan` + hand-rolled file rotation with **Pino** + `pino-http` for a single NDJSON stream.
- **Priority:** Low–Medium. The current design works and is already structured in transit; standardizing on Pino on the server would reduce custom code.
- **Risk:** Medium if the custom ingestion protocol is relied on by external consumers; verify contract compatibility before changing server output format.

### Repos already aligned

- **LogSearch, EArkivChecker, Dokumentbibliotek, VajSkrivare, iKrock2** already follow the recommended .NET pattern (NLog + MEL/ILogger, `appsettings.json` config, structured templates, file + console).
- **VajSkrivare** additionally has correlation via `TraceIdentifier`; this is the model other .NET web apps should adopt.
