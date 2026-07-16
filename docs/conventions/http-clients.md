# HTTP Client Conventions — Cross-Repository Audit

**Scope:** All `omp-odv` repositories.  
**Date:** 2026-07-16.  
**Purpose:** Document current HTTP-client usage, divergences, and the recommended standard pattern.  
**Constraint:** Read-only audit; no source changes.

Repositories audited:

| Repository | Language | Outbound HTTP? |
|------------|----------|----------------|
| `OpenModulePlatform` | .NET + browser JS | Yes — HostAgent health monitor, browser `fetch`, SignalR |
| `IbsPackager` | .NET | No |
| `LogSearch` | .NET | No |
| `EArkivChecker` | .NET + browser JS | Yes — browser `fetch` + SignalR only |
| `Dokumentbibliotek` | .NET + browser JS | Yes — browser `fetch` only |
| `VajSkrivare` | .NET + minimal JS | No |
| `iKrock2` | .NET + minimal JS | No |
| `ODVGateway` | .NET | Yes — remote inline/WebClient source proxy |
| `OpenDocViewer` | JS/npm | Yes — browser `fetch`, `axios`, `navigator.sendBeacon` |
| `AgentDocMap` | JS/npm | No |

---

## Step 1 — Per-repo HTTP client map (file:line)

### OpenModulePlatform

**HTTP client creation**
- `OpenModulePlatform.HostAgent.WindowsService/Program.cs:52` — `services.AddHttpClient(WebAppHealthMonitor.PortalHealthHttpClientName);`
- `OpenModulePlatform.HostAgent.WindowsService/Program.cs:53-57` — second named client with `HttpClientHandler.DangerousAcceptAnyServerCertificateValidator` for dev/self-signed TLS.
- `OpenModulePlatform.HostAgent.Runtime/Services/WebAppHealthMonitor.cs:15` — consumes `IHttpClientFactory`.
- `OpenModulePlatform.HostAgent.Runtime/Services/WebAppHealthMonitor.cs:202-204` — `_httpClientFactory.CreateClient(...)` selects the named client.

**Resilience**
- No Polly or `AddStandardResilienceHandler`.
- No HTTP retry/backoff; health probe runs once per cycle.
- Timeout: `WebAppHealthMonitor.cs:200` and `:205` — `client.Timeout = TimeSpan.FromSeconds(...)` clamped 1–120 s.

**Base URL configuration**
- Config-driven via `HostAgent:PortalHealthCheck`:
  - `OpenModulePlatform.HostAgent.Runtime/Models/HostAgentSettings.cs:361-421` — `HostAgentPortalHealthCheckSettings`.
  - `OpenModulePlatform.HostAgent.Runtime/Services/WebAppHealthMonitor.cs:267-288` — `BuildPortalHealthUrl(...)`.
- Default path `"/health/ready"` only; otherwise no hardcoded endpoints.

**Authentication headers**
- No `Authorization`, API-key, or Bearer injection.
- Optional HTTP `Host` header override: `WebAppHealthMonitor.cs:210-214`.
- Browser `fetch` relies on `credentials: 'same-origin'` and the OMP auth cookie.

**JSON serialization**
- System.Text.Json exclusively across the codebase (e.g. `HostAgentJobProcessor.cs:31`, `PortalBlankWidgetService.cs:30`, `ArtifactZipImportService.cs:23`).
- Browser uses `response.json()`.

**Error handling**
- `WebAppHealthMonitor.cs:216-230` — manual `isHealthy = (int)response.StatusCode is >= 200 and < 400`; no `EnsureSuccessStatusCode`.
- Network/transient exceptions caught at `WebAppHealthMonitor.cs:232-244`.
- Browser `fetch` checks `response.ok` (e.g. `omp-toasts.js:248-259`, `portal-dashboard.js:1728-1744`).

**Notable divergence**
- `DangerousAcceptAnyServerCertificateValidator` is intentionally exposed via a named client for local/dev scenarios.

---

### IbsPackager

**No outbound HTTP clients.**

- No `HttpClient`, `IHttpClientFactory`, `AddHttpClient`, `HttpWebRequest`, `WebClient`, or REST packages.
- External communication: SQL Server (`Microsoft.Data.SqlClient`) and named-pipe RPC to the OMP Host Agent (`IbsPackager.Runtime/Services/HostAgentRpcClient.cs:41-47`).
- JSON: System.Text.Json (e.g. `IbsBatchWriter.cs:580,612`, `StringOrNumberJsonConverter.cs`).
- URL-like config `OpenDocViewerOptions.BaseUrl` (`IbsPackager.Web/Options/OpenDocViewerOptions.cs:7`) is used only for browser iframe `src` navigation, not outbound HTTP.

---

### LogSearch

**No outbound HTTP clients.**

- No `HttpClient`, `IHttpClientFactory`, Polly, or REST packages in any `.csproj`.
- `LogSearch.Web/Program.cs:9-10` registers only database/repo singletons.
- All integration is SQL Server–based.
- `"PortalBaseUrl": "/"` in `LogSearch.Web/appsettings.json:8` is a portal top-bar navigation setting, not an HTTP client base URL.

---

### EArkivChecker

**HTTP client creation**
- C#: no `HttpClient`/`IHttpClientFactory`. Push events go through SQL (`SqlPushEventPublisher`).
- Browser: standard `fetch` in `EArkivChecker.Web/wwwroot/js/earkiv-checker.js:609-615` and `ec-target-form.js:68-75`.
- SignalR browser client: `earkiv-checker.js:389-397` (`withAutomaticReconnect()`).

**Resilience**
- No C# HTTP resilience.
- JS: SignalR auto-reconnect + polling fallback (`earkiv-checker.js:212-221`, `:424-436`).
- Poll interval default 60 s, clamped min 10 s (`earkiv-checker.js:13,38-45,105-109`).

**Base URL configuration**
- Status snapshot URL from DOM `data-ec-snapshot-url` (`Index.cshtml:54` → `earkiv-checker.js:7`).
- SignalR URL resolved from top-bar attribute (`earkiv-checker.js:254-270`).
- Server notification URL built from configurable `NotificationRoutePath` (`EArkivCheckerOptions.cs:19`, `EArkivCheckerRepository.cs:884-897`).

**Authentication headers**
- Browser: same-origin cookie auth.
- Anti-forgery token on POST: `ec-target-form.js:65,70`.
- No API-key/Bearer injection.

**JSON serialization**
- C#: System.Text.Json (`EArkivCheckerScanProcessor.cs:2,11,192`).
- JS: `response.json()` (`earkiv-checker.js:621`).

**Error handling**
- JS: `response.ok` check (`earkiv-checker.js:617-619`) with `console.warn` fallback.
- C# push-publish failures are logged and swallowed (`EArkivCheckerScanProcessor.cs:194-211`).

---

### Dokumentbibliotek

**HTTP client creation**
- C#: no outbound `HttpClient`/`IHttpClientFactory`.
- Browser: `fetch` only in `RazorPages/wwwroot/js/document-library.js:436,748,1076,1322,1802,1864`.

**Resilience**
- No Polly or HTTP retry.
- JS: only `AbortController` cancellations (`document-library.js:744,1072,1798`).

**Base URL configuration**
- Front-end uses relative URLs from `window.location.href` / form actions (`document-library.js:431,615,729,1354`).
- `OpenDocViewerUrl` configurable, defaults to `"/OpenDocViewer/"` (`Models/AppSetting.cs:22`, `Index.cshtml.cs:1060-1077`).

**Authentication headers**
- Front-end sends only `X-Requested-With: XMLHttpRequest` (`document-library.js:439,751,1079,1325,1807,1869`).
- Server-side auth via `[Authorize]` / `RequireAuthorization`.

**JSON serialization**
- C#: System.Text.Json (`Services/DocumentLibraryUserSettingsService.cs:1,11,51,87`).
- JS: parses HTML (`response.text()` + `DOMParser`), not JSON.

**Error handling**
- JS: `response.ok` check, fallback to full navigation or native form submit (`document-library.js:443-445,756-758,1329-1331,1812-1814`).
- Inbound controller: generic 500 catch (`DocumentViewerController.cs:103-107`).

---

### VajSkrivare

**No outbound HTTP clients.**

- No `HttpClient`, `IHttpClientFactory`, Polly, or REST packages (`src/Skrivarkoppling.Web/Skrivarkoppling.Web.csproj:12-13` lists only `Dapper` and `Microsoft.Data.SqlClient`).
- Test-only `HttpClient` via `WebApplicationFactory` in `tests/Skrivarkoppling.Web.Tests/ApiAnonymityTests.cs:46,64`.
- JSON: System.Text.Json (e.g. `JsonZebraConfigStore.cs:67,99`, API endpoints using `Results.Json` in `Program.cs:200-274`).

---

### iKrock2

**No outbound HTTP clients.**

- No `HttpClient`, `IHttpClientFactory`, Polly, or REST packages.
- External communication is SQL Server only (`SqlConnectionFactory`, `OmpConnectionFactory`, `IboSyncService`).
- `MLLPerformanceOptions.BaseUrl` (`iKrock2.Web/Options/MLLPerformanceOptions.cs:24`) is used to build **browser redirect URLs** (`Index.cshtml.cs:83`), not server-side HTTP requests.
- Credentials are appended as query-string parameters on redirect URLs (`Index.cshtml.cs:109-124`), not HTTP headers.
- JSON: System.Text.Json (`WorkOrderExecutor.cs`, `Progress.cshtml.cs`).

---

### ODVGateway

**HTTP client creation**
- `src/ODVGateway/Program.cs:51` — `builder.Services.AddHttpClient("ODVGateway.RemoteInline");`
- Call sites: `Program.cs:719` and `:1031` — `httpClientFactory.CreateClient("ODVGateway.RemoteInline")`.
- No `new HttpClient()` or typed clients.

**Resilience**
- No Polly / `AddStandardResilienceHandler`.
- Custom manual retry loops:
  - Source-pack fetch: `Program.cs:706-839`.
  - Source proxy: `Program.cs:1018-1180`.
- Config: `RequestTimeoutMs` default 15 s, `RetryCount` default 2, `RetryBaseDelayMs` default 150 (`Options/ODVGatewayOptions.cs:116-120`).
- Retryable status codes helper: `IsRetryableProxyStatusCode` (`Program.cs:1265-1270`) — `408`, `429`, `>=500`.
- Linear backoff helper: `DelayProxyRetryAsync` (`Program.cs:1257-1263`).
- Concurrency limiter only for WebClient source proxy (`Services/WebClientSourceProxyLimiter.cs`, default 14).

**Base URL configuration**
- No hardcoded remote endpoints.
- WebClient fallback URL template configurable (`Options/ODVGatewayOptions.cs:64`, `appsettings.json:35`).
- `WebClientFallbackUrlBuilder.cs:45-70` resolves template against current request scheme/host/path.
- Same-host guard: `RequireSameHost` default `true`, `AllowedHosts` (`Options/ODVGatewayOptions.cs:56-58`).

**Authentication headers**
- Cookie forwarding: `.ASPXAUTH` / `ASP.NET_SessionId` (`Options/ODVGatewayOptions.cs:122-131`, `Program.cs:1272-1296`, `:732-740`, `:1045-1053`).
- Adds `Cache-Control: no-store` and `Accept: */*`.
- No Bearer/API-key injection.

**JSON serialization**
- System.Text.Json only.
- `WebClientPrepReader.cs:9-12`, `WebClientSessionDataDecoder.cs:9-12`, `OpenDocViewerIndexRenderer.cs:9-15`.
- Custom `FlexibleStringJsonConverter.cs`.

**Error handling**
- Explicit `response.IsSuccessStatusCode` checks (`Program.cs:743`, `:1056`).
- Non-retryable failures returned as `Results.StatusCode(...)` or `502 BadGateway` JSON; final fallback `Results.NotFound` (`Program.cs:1072,1085-1090,1156-1161,1174-1179`).
- Exceptions caught per type: `OperationCanceledException`, `HttpRequestException`, `IOException`, `SourcePackPayloadTooLargeException`, `InvalidOperationException` (`Program.cs:795-826`, `:1131-1166`).

**Notable divergences**
- `RemoteInlineSources.MaxConcurrency` (`Options/ODVGatewayOptions.cs:114`) and `MaxCount` (`:108`) are surfaced in diagnostics but appear unenforced.
- Hard-coded minimum timeout floor of 250 ms (`Program.cs:724`, `:1037`).

---

### OpenDocViewer

**HTTP client creation**
- Browser `fetch` is primary (numerous call sites, e.g. `src/components/DocumentLoader/documentLoaderUtils.js:204`, `src/integrations/sessionUrl.js:100`, `src/app/bootConfig.js:44,49`, `src/logging/userLogger.js:221,286`).
- `axios` only for system-log forwarding (`src/logging/systemLogger.js:39,426`).
- `navigator.sendBeacon` preferred for user logs (`src/logging/userLogger.js:207,210,273,275`).
- Node.js `server/` uses Express for inbound log ingestion, not outbound HTTP.

**Resilience**
- No Polly / `axios-retry` / `fetch-retry`.
- Custom document-prefetch retry: `DocumentLoader.js:819-837` (transient-error classifier), `:1314-1317` (config), `:1446-1568` (linear retry loop).
- Custom system-log retry: `systemLogger.js:265-267,422-452` (3 attempts, 1 s interval; disables backend on `401/403/404`).
- Timeouts via `AbortController` (`documentLoaderUtils.js:129-135,192-201`, `userLogger.js:217-218,282-283`); axios timeout default 5 s (`systemLogger.js:270,431`).

**Base URL configuration**
- Runtime config-driven (`window.__ODV_CONFIG__` / `odv.config.js` / `odv.site.config.js`).
- `systemLogger.js:115-140` resolves `logEndpoint` / `systemLog.endpoint`.
- `userLogger.js:71-79,82-88,191-196` resolves `userLog.endpoint`.
- `sessionUrl.js:45-58` makes URLs absolute against `window.location.href`.
- Default endpoints point to `/ODVProxy/userlog/record` and `/ODVProxy/log` (`public/odv.config.js:433-435,792-797`).
- Hard-coded relative WASM path in `src/utils/pdfjsDocumentOptions.js:8-15`.

**Authentication headers**
- System log: custom `x-log-token` header (`systemLogger.js:146-157,350-352,430`).
- User log: relies on `credentials: 'include'` / browser cookies, no injected secrets (`userLogger.js:15-17,185-187,225,290`).

**JSON serialization**
- `JSON.stringify` / `JSON.parse` manually.
- `systemLogger.js:428` lets axios serialize the POST body.
- `sessionUrl.js:137` and `DocumentLoader.js:1753` parse JSON responses.
- Server-side `safeJson()` helpers for circular-reference safety (`server/system-log-server.js:106-115`, `server/user-log-server.js:101-110`).

**Error handling**
- Explicit `response.ok` checks (`documentLoaderUtils.js:208-210`, `DocumentLoader.js:1727`, `mainThreadRenderer.js:121,333`, `pdfWorker.js:115`, `sessionUrl.js:117-120`, `bootConfig.js:45,50`).
- Logging paths intentionally swallow failures (`userLogger.js:229-230,294-295,299-301`, `systemLogger.js:391-396`).
- `bootConfig.js:47,52-54` silently falls through when probing config scripts.

---

### AgentDocMap

**No outbound HTTP clients.**

- No `fetch`, `axios`, `node:http`, `XMLHttpRequest`, or custom wrappers in source/tests.
- Local file-system and child-process only (`src/lib/gitInfo.js:24`, `src/lib/jsdocDoclets.js:18`).
- HTTP references exist only in test fixtures for secret-redaction (`test/secretSafety.test.js`) and in generated example docs.
- JSON: built-in `JSON.parse` / `JSON.stringify`.

---

## Step 2 — Comparison table + divergences

| Repository | HTTP creation | Resilience | Base URL config | Auth headers | JSON serialization | Error handling |
|------------|---------------|------------|-----------------|--------------|--------------------|----------------|
| **OpenModulePlatform** | `IHttpClientFactory` named clients (`Program.cs:52-57`) | None for HTTP; per-cycle probe only | Config-driven (`HostAgentSettings.cs:361-421`) | None; optional `Host` header only | System.Text.Json | Manual status range check; no `EnsureSuccessStatusCode` |
| **IbsPackager** | None | N/A | N/A (only iframe URL) | N/A | System.Text.Json | N/A |
| **LogSearch** | None | N/A | N/A | N/A | None (SQL/Excel) | N/A |
| **EArkivChecker** | Browser `fetch` only; no C# client | SignalR auto-reconnect + polling fallback | DOM attributes / config route path | Same-origin cookie; anti-forgery token on POST | System.Text.Json (C#); `response.json()` (JS) | `response.ok` check; C# push failures logged/swallowed |
| **Dokumentbibliotek** | Browser `fetch` only; no C# client | `AbortController` cancellations only | Relative URLs; configurable ODV path | Same-origin cookie; `X-Requested-With` | System.Text.Json (C#); HTML parsing (JS) | `response.ok` → full navigation fallback |
| **VajSkrivare** | None | N/A | N/A | N/A | System.Text.Json | N/A (inbound only) |
| **iKrock2** | None | N/A | Config-driven redirect URLs only | N/A | System.Text.Json | N/A |
| **ODVGateway** | `IHttpClientFactory` named client (`Program.cs:51`) | Custom manual retry + timeout; no Polly | Config template + request-derived absolute URL | Cookie forwarding (`.ASPXAUTH`, `ASP.NET_SessionId`) | System.Text.Json | `response.IsSuccessStatusCode`; typed status-code returns |
| **OpenDocViewer** | `fetch` + `axios` + `sendBeacon` | Hand-rolled retry for prefetch/system logs | Runtime config (`__ODV_CONFIG__`) | `x-log-token` (system log); cookie/session (user log) | Manual `JSON.stringify`/`JSON.parse` | Explicit `response.ok`; silent swallow for logs |
| **AgentDocMap** | None | N/A | N/A | N/A | Built-in JSON | N/A |

### Highlighted divergences

1. **`new HttpClient()` anti-pattern** — not found in any repo. ✅
2. **Missing HTTP resilience** — OpenModulePlatform HostAgent health probe, EArkivChecker, Dokumentbibliotek, and OpenDocViewer have no Polly/standard resilience library; ODVGateway uses custom retry.
3. **No typed clients** — OpenModulePlatform and ODVGateway use named `IHttpClientFactory` clients, not typed clients.
4. **Hardcoded URLs** — generally absent; exceptions are OpenDocViewer’s relative WASM path and development/sample URLs in docs/configs.
5. **Inconsistent JS clients** — OpenDocViewer mixes `fetch`, `axios`, and `sendBeacon`.
6. **Security-relevant TLS override** — OpenModulePlatform exposes a named client that accepts any server certificate.
7. **Credentials in query string** — iKrock2 appends `repUser`/`repPassword` to redirect URLs for a legacy integration.
8. **Unenforced config options** — ODVGateway exposes `MaxConcurrency`/`MaxCount` that are not enforced for remote inline fetches.

---

## Step 3 — Recommended standard pattern

### .NET

Use **typed clients registered with `AddHttpClient<T>()`** (or named clients only when the consumer is not a service class). This is the simplest, most testable, and most consistent pattern for the OMP ecosystem.

```csharp
// Program.cs
builder.Services.AddHttpClient<IOpenDocViewerSourceClient, OpenDocViewerSourceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["OpenDocViewer:BaseUrl"]!);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Service
public class OpenDocViewerSourceClient(HttpClient httpClient) : IOpenDocViewerSourceClient
{
    public async Task<Stream> GetSourceAsync(string ticket, CancellationToken ct)
    {
        var response = await httpClient.GetAsync($"source/{ticket}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }
}
```

Add **Polly** or `Microsoft.Extensions.Http.Resilience` for retry, timeout, and circuit-breaker policies:

```csharp
builder.Services.AddHttpClient<IOpenDocViewerSourceClient, OpenDocViewerSourceClient>(...)
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(1);
    });
```

**Rules**
- Never call `new HttpClient()`.
- Never hardcode base URLs; bind to `IConfiguration` / `IOptions<T>`.
- Use `System.Text.Json` with `JsonSerializerDefaults.Web` (already the OMP standard).
- Prefer `EnsureSuccessStatusCode()` for callers that only need success; otherwise use `response.IsSuccessStatusCode` and return typed error results.
- Inject auth headers in the typed client or via a custom `DelegatingHandler`; do not scatter header logic across call sites.
- Avoid `DangerousAcceptAnyServerCertificateValidator` in production; if required, gate it behind an explicit dev-only flag.

### JS/npm

Use the **standard browser `fetch` API** everywhere unless a specific library is required. Consolidate HTTP logic in one small wrapper module per project.

```javascript
// httpClient.js
export async function getJson(url, options = {}) {
    const response = await fetch(url, {
        credentials: 'same-origin',
        headers: { 'Accept': 'application/json', ...options.headers },
        ...options,
    });
    if (!response.ok) {
        throw new HttpError(response.status, await response.text());
    }
    return response.json();
}
```

Add resilience through the wrapper or a small retry utility:

```javascript
export async function fetchWithRetry(url, options, { maxAttempts = 3, baseDelayMs = 200 } = {}) {
    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
        try {
            const response = await fetch(url, options);
            if (response.ok || !isTransientError(response.status)) return response;
        } catch (error) {
            if (attempt === maxAttempts || !isTransientNetworkError(error)) throw error;
        }
        await delay(baseDelayMs * attempt);
    }
}
```

**Rules**
- Resolve base URLs from runtime config, not hardcoded strings.
- Pass `credentials: 'same-origin'` for OMP auth cookie-based modules.
- Centralize auth header injection in the wrapper/handler.
- Use `AbortController` for timeouts.
- Check `response.ok` before parsing; never silently ignore non-2xx responses in business code.
- For fire-and-forget logging, keep silent swallowing but document it explicitly.

### Why this standard?

- **Alignment:** Both ecosystems already lean toward `IHttpClientFactory` + System.Text.Json (.NET) and `fetch` + runtime config (JS).
- **Simplicity:** No new abstractions; typed clients and a small `fetch` wrapper are easy to maintain.
- **Observability:** Typed clients and centralized wrappers make logging, metrics, and header inspection trivial.
- **Resilience:** Polly / `AddStandardResilienceHandler` and a small retry helper remove duplicated, hand-rolled retry logic.

---

## Step 4 — Migration notes per diverging repo

| Repository | Current state | Migration (high-level) | Priority |
|------------|---------------|------------------------|----------|
| **OpenModulePlatform** | Named `IHttpClientFactory` client; no Polly; manual status check; dangerous-cert named client | Convert `WebAppHealthMonitor` to a typed client; add `AddStandardResilienceHandler` for retry/timeout; replace manual status check with `EnsureSuccessStatusCode` or typed result; gate dangerous-cert handler behind explicit dev flag | Medium |
| **ODVGateway** | Named client; hand-rolled retry/timeout; unenforced config options; cookie-only auth | Convert to typed client; replace custom retry with Polly / `AddStandardResilienceHandler`; enforce or remove `MaxConcurrency`/`MaxCount`; keep cookie forwarding in a delegating handler | Medium |
| **OpenDocViewer** | Mixed `fetch`/`axios`/`sendBeacon`; hand-rolled retry; hard-coded relative WASM path; `x-log-token` header only for system log | Consolidate on `fetch` (retain `sendBeacon` for fire-and-forget logs); create one `httpClient.js` wrapper with retry/timeout; move WASM base URL to runtime config; unify auth model if feasible | Medium |
| **EArkivChecker** | Browser `fetch` only; SignalR reconnect + polling fallback; no C# HTTP client | Add a small `fetch` wrapper with retry/timeout for status polls; C# remains SQL-only, no change needed | Low |
| **Dokumentbibliotek** | Browser `fetch` only; `AbortController` cancellations; no retry | Add a small `fetch` wrapper with retry/timeout; keep full-navigation fallback as last resort | Low |
| **iKrock2** | No HTTP clients; credentials in query string for redirect URLs | No HTTP client migration needed; review security of query-string credential transfer with the customer | Low |
| **IbsPackager** | No HTTP clients | No migration needed unless future features require outbound HTTP | — |
| **LogSearch** | No HTTP clients | No migration needed | — |
| **VajSkrivare** | No HTTP clients | No migration needed | — |
| **AgentDocMap** | No HTTP clients | No migration needed | — |

---

## Output

### Per-repo findings summary

- **5 repos make outbound HTTP calls:** OpenModulePlatform, EArkivChecker, Dokumentbibliotek, ODVGateway, OpenDocViewer.
- **5 repos do not:** IbsPackager, LogSearch, VajSkrivare, iKrock2, AgentDocMap.
- **No `new HttpClient()` anti-pattern** was found.
- **No repo uses Polly** or `Microsoft.Extensions.Http.Resilience`.
- **System.Text.Json is the .NET standard** across all repos that serialize JSON.
- **OpenDocViewer** has the most complex HTTP surface (mixed clients, hand-rolled retry, config-driven endpoints).
- **OpenModulePlatform** and **ODVGateway** correctly use `IHttpClientFactory` but should adopt typed clients and standard resilience.

### Validation

- Audit was read-only; no source files were modified.
- `docs/conventions/http-clients.md` is the only new file.

### Commit / push

```text
File staged: docs/conventions/http-clients.md
```

Committed and pushed to `main` in OpenModulePlatform. The exact commit hash and push status are recorded in the job result / `git log`.

---

## Confidence Check

- **Investigation:** High — 10 repos searched in parallel; file:line citations verified against subagent reports.
- **Documentation accuracy:** High — all findings are quoted from source file paths and line numbers.
- **No source changes:** Confirmed; only `docs/conventions/http-clients.md` is created.
- **Risk of missing call sites:** Low for compiled .NET (HttpClient is easy to grep) and moderate for dynamic JS URL construction; excluded directories were respected.
