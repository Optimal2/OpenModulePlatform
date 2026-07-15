# Unit-testing conventions for OMP+ODV

This document records the unit-testing patterns found in the OMP+ODV repositories (audit date 2026-07-15; source code only — `bin/`, `obj/`, `artifacts/`, `node_modules/`, `dist/` and other build output were excluded from the audit).

Repos audited: OpenModulePlatform, IbsPackager, LogSearch, EArkivChecker, Dokumentbibliotek, VajSkrivare, iKrock2, ODVGateway (.NET), OpenDocViewer, AgentDocMap (JS/npm).

## 1. Per-repo unit-test map

### OpenModulePlatform

- **Tests exist:** Yes — 3 test projects, ~56 test files, ~270 test methods (239 `[Fact]` + 31 `[Theory]`).
  - `OpenModulePlatform.Portal.Tests` (31 files), `OpenModulePlatform.HostAgent.Runtime.Tests` (24 files), `OpenModulePlatform.Web.Shared.Analyzers.Tests` (1 file)
  - Plus one Pester file: `tests/Validate-ComponentVersions.Tests.ps1` (script test, not a unit test)
- **Framework:** xUnit.
  - `Directory.Packages.props:33-34` (`xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.5)
  - `OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj:17-18`
  - Analyzer tests additionally use the Roslyn harness `Microsoft.CodeAnalysis.CSharp.Testing`: `OpenModulePlatform.Web.Shared.Analyzers.Tests/OmpWebDefaultsAnalyzerTests.cs:3-5,41-49`
- **Layout/naming:** Sibling `X.Tests/X.Tests.csproj` beside each source project at repo root; subfolders mirror source structure (`Services/`, `Models/`, `Security/`, `Configuration/`, `Integration/`). All 3 projects are in `OpenModulePlatform.slnx:8-10`. Method naming `Method_WhenCondition_ExpectedResult` (e.g. `OpenModulePlatform.HostAgent.Runtime.Tests/Services/OmpHostArtifactRepositoryTierCTests.cs:24`). Global `<Using Include="Xunit" />` in csproj (`OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj:39`).
- **Mock library:** None — hand-written fakes only. No Moq/NSubstitute/FakeItEasy anywhere.
  - `OpenModulePlatform.HostAgent.Runtime.Tests/Services/FakeOmpHostArtifactRepository.cs:8`
  - `OpenModulePlatform.HostAgent.Runtime.Tests/Services/FakeOptionsMonitor.cs:5`
  - `OpenModulePlatform.HostAgent.Runtime.Tests/Services/ManualTimeProvider.cs`
  - `OpenModulePlatform.Portal.Tests/Integration/TestAuthHandler.cs`
- **Package pins (CPM, `Directory.Packages.props:3`):** `Microsoft.NET.Test.Sdk` 18.7.0 (`:13`), `xunit` 2.9.3 (`:33`), `xunit.runner.visualstudio` 3.1.5 (`:34`), `coverlet.collector` 10.0.1 (`:7`), `Microsoft.AspNetCore.Mvc.Testing` 10.0.9 (`:10`), `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` 1.1.4 (`:25`). TargetFramework `net10.0` in all three test csproj files (line 4 of each).
- **Assertion style:** Plain xUnit `Assert.*`. No FluentAssertions.
  - `OpenModulePlatform.Portal.Tests/Services/PushEventTests.cs:19-27,50` (`Assert.Equal`, `Assert.Throws`)
  - `OpenModulePlatform.HostAgent.Runtime.Tests/Services/HostAgentEngineTierDTests.cs:76-80` (`Assert.Single`, `Assert.True`, `Assert.Contains`)
- **Coverage:** `coverlet.collector` referenced (PrivateAssets=all) in `OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj:10-13` and `OpenModulePlatform.HostAgent.Runtime.Tests/OpenModulePlatform.HostAgent.Runtime.Tests.csproj:11-14` — but NOT in the Analyzers.Tests project. No `.runsettings`, no `--collect` invocation in CI/scripts, no coverage upload. `.gitignore:29-31` ignores coverage output.
- **Integration vs unit separation:** Folder-level (`Portal.Tests/Integration/`) plus a Tier C/D naming suffix on HostAgent test classes.
  - Tier C = real SQL Server: `OpenModulePlatform.HostAgent.Runtime.Tests/Services/OmpHostArtifactRepositoryTierCTests.cs:7`
  - Tier D = pure in-memory fakes: `OpenModulePlatform.HostAgent.Runtime.Tests/Services/HostAgentEngineTierDTests.cs:9`
  - DB-backed tests use `IClassFixture` over shared SQL Server fixtures: `OpenModulePlatform.Portal.Tests/Integration/PushEventPipelineIntegrationTests.cs:10` (fixture `PushEventPipelineTestFixture.cs:10-15`, hardcoded `Server=localhost`, creates DB `OpenModulePlatform_PortalTests_PushEvents`); `OpenModulePlatform.Portal.Tests/Services/StaleSchemaHealTests.cs:9`
  - `OmpHostArtifactRepositoryTestDatabase.cs:283-296` honors env var `OMP_TEST_CONNECTION_STRING`, defaulting to `Server=(local);Integrated Security=true`; creates/drops a unique DB per test class
  - Web-hosting integration uses `WebApplicationFactory<PortalResource>` + TestServer: `OpenModulePlatform.Portal.Tests/Integration/PortalWebApplicationFactory.cs:18`
  - No `[Trait]`, no `[Collection]`, no `Skip=` gating, no Testcontainers — DB tests simply fail on a machine without local SQL Server
- **How tests run:** Locally via the tracked pre-push hook `.githooks/pre-push.ps1:88-89` (`dotnet test OpenModulePlatform.slnx -c Release --no-build`), documented at `README.md:198-200`. **GitHub CI does NOT run tests** — `.github/workflows/ci.yml:87-88` ends at `dotnet build`. The Pester file has no CI invocation.
- **Extra notes:** The analyzer test project uses `CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>` with inline fake source strings (`OmpWebDefaultsAnalyzerTests.cs:11-49`) — a good model for future Roslyn analyzers.

### IbsPackager

- **Tests exist:** Yes — 2 test projects, 4 test files, ~42 test methods.
  - `IbsPackager.Tests` (1 file, DB-backed) and `IbsPackager.ChannelTypes.FileDrop.Tests` (3 files, pure unit)
- **Framework:** xUnit + `Xunit.SkippableFact`.
  - `IbsPackager.Tests/IbsPackager.Tests.csproj:13-16`
  - `IbsPackager.ChannelTypes.FileDrop.Tests/IbsPackager.ChannelTypes.FileDrop.Tests.csproj:13-15`
- **Layout/naming:** Test projects at repo root beside source projects, named `<SourceProject>.Tests`; both in `IbsPackager.slnx:8-9`. Naming mismatch: `IbsPackager.Tests` actually tests `IbsPackager.Runtime` (ProjectReference at `IbsPackager.Tests/IbsPackager.Tests.csproj:20`).
- **Mock library:** None — `NullLogger<T>.Instance` (`IbsPackager.ChannelTypes.FileDrop.Tests/FileDropRoutingTests.cs:124`) and hand-written stubs (`TestConfiguration : IConfiguration`, `NoChangeToken`, `EmptyDisposable` at `IbsPackager.Tests/ReconcileChannelTypeArtifactRequirementsTests.cs:421-496`).
- **Package pins (CPM, `Directory.Packages.props:3`):** `Microsoft.NET.Test.Sdk` 18.7.0 (`:12`), `xunit` 2.9.3 (`:13`), `xunit.runner.visualstudio` 3.1.5 (`:14`), `coverlet.collector` 10.0.1 (`:15`), `Xunit.SkippableFact` 1.4.13 (`:16`). TargetFramework `net10.0` (line 4 of each test csproj). `global.json` pins the .NET 10 SDK.
- **Assertion style:** Plain xUnit `Assert.*`.
  - `IbsPackager.ChannelTypes.FileDrop.Tests/FileDropRoutingTests.cs:23-29` (`Assert.Equal`, `Assert.Throws`)
  - `IbsPackager.ChannelTypes.FileDrop.Tests/FileDropIndexFieldExtractorTests.cs:27-29` (`Assert.Single`, `Assert.DoesNotContain`)
- **Coverage:** `coverlet.collector` referenced in both test csprojs (line 15 of each). Nothing invokes it.
- **Integration vs unit separation:** Informal but present, and the best gating pattern in the ecosystem. DB-backed tests are isolated in `IbsPackager.Tests` and use `[SkippableFact]` with environment gating: connection string from `IBSPACKAGER_TEST_CONNECTION_STRING` (`ReconcileChannelTypeArtifactRequirementsTests.cs:33-34`), `SkipException` when the `omp_ibs_packager` schema is absent (`:113-116`), self-deploys the stored procedure under test from `sql/1-setup-ibspackager.sql` (`:63-107`), GUID-suffixed rows with best-effort cleanup (`:382-419`). `FileDrop.Tests` is pure unit tests. No `[Trait]`/`[Collection]`/Testcontainers.
- **How tests run:** Nothing automated. `.github/workflows/ci.yml` is `workflow_dispatch`-only and build+validate only (`ci.yml:63-82`); `scripts/local-ci.ps1:59-100` (the documented pre-push gate) likewise only builds and validates component versions. Tests run ad-hoc via `dotnet test` or Visual Studio.
- **Extra notes:** Consumer repo of OMP — the slnx references sibling OMP projects cross-repo (`IbsPackager.slnx:2-3`) and builds need `/p:OpenModulePlatformRoot=...`. `scripts/run-filedrop-test-cases.ps1` is an end-to-end smoke harness, not a unit-test suite. `docs/ROADMAP.md:51` still lists "automated integration tests against SQL Server" as future work.

### VajSkrivare

- **Tests exist:** Yes — 1 test project, 1 test file, 2 `[Fact]` tests.
  - `tests/Skrivarkoppling.Web.Tests/ApiAnonymityTests.cs:43,61`
- **Framework:** xUnit.
  - `tests/Skrivarkoppling.Web.Tests/Skrivarkoppling.Web.Tests.csproj:14-15`
- **Layout/naming:** Top-level `tests/` folder mirroring `src/` (divergent from the sibling-root convention); project named `<SourceProject>.Tests`; included in `Skrivarkoppling.sln:10` under a `tests` solution folder (`Skrivarkoppling.sln:8,51`).
- **Mock library:** None — hand-written fake `FakeZebraConfigService : IZebraConfigService` (`tests/Skrivarkoppling.Web.Tests/ApiAnonymityTests.cs:38,76-121`).
- **Package pins:** No CPM (no `Directory.Packages.props`); inline versions in the test csproj: `Microsoft.NET.Test.Sdk` 17.14.0 (`:13`), `xunit` 2.9.3 (`:14`), `xunit.runner.visualstudio` 2.8.2 (`:15`), `Microsoft.AspNetCore.Mvc.Testing` 10.0.0 (`:12`). TargetFramework `net10.0` (`:4`). No coverlet.
- **Assertion style:** Plain xUnit `Assert.*` (`ApiAnonymityTests.cs:53-58,71-73`). No FluentAssertions.
- **Coverage:** None — no coverlet reference, no `.runsettings`, no CI coverage.
- **Integration vs unit separation:** None formal. The only tests are integration-style HTTP tests using `WebApplicationFactory<Program>` + `IClassFixture` with in-memory config overrides and `ZebraConfig:Enabled=false` (`ApiAnonymityTests.cs:12-41`, `:32`), so no SQL Server dependency.
- **How tests run:** No `dotnet test` invocation anywhere. CI `.github/workflows/ci.yml` is `workflow_dispatch`-only and build+validate only (`:63-82`); `scripts/local-ci.ps1:70-82` has no test step. Tests run via Visual Studio Test Explorer or manual `dotnet test`.
- **Extra notes:** Test project uses the `Microsoft.NET.Sdk.Web` SDK to match the web app under test. Repo depends on a sibling OpenModulePlatform checkout (`/p:OpenModulePlatformRoot=...`).

### iKrock2

- **Tests exist:** Yes — 1 test project, 10 test classes in 11 files, 35 test methods (23 `[Fact]` + 12 `[Theory]`).
  - `iKrock2.Application.Tests/iKrock2.Application.Tests.csproj`
- **Framework:** xUnit (`iKrock2.Application.Tests/iKrock2.Application.Tests.csproj:13-14`); `global using Xunit;` in `iKrock2.Application.Tests/Usings.cs:1`.
- **Layout/naming:** Single flat test project beside source projects at repo root; included in `iKrock2.slnx:7`. Test classes named `<Subject>Tests`, methods `Method_Condition_Expectation` (e.g. `iKrock2.Application.Tests/BackendStatusServiceTests.cs:11-12`). Despite the name, the project references Application **and** Backend and Contracts (`iKrock2.Application.Tests.csproj:19-21`).
- **Mock library:** None. `WorkOrderExecutorTests.cs:11-13` explicitly documents that sealed, SQL-coupled production classes (`DashboardRepository`, `IboSyncService`) block mocking; tests use `Options.Create(...)` hand-wiring instead (`BackendStatusServiceTests.cs:14-28`).
- **Package pins (CPM, `Directory.Packages.props:3`):** `Microsoft.NET.Test.Sdk` 18.7.0 (`:15`), `xunit` 2.9.3 (`:17`), `xunit.runner.visualstudio` 3.1.5 (`:18`), `coverlet.collector` 10.0.1 (`:7`). TargetFramework `net10.0` (`iKrock2.Application.Tests.csproj:4`); SDK pinned `10.0.200` (`global.json:3`).
- **Assertion style:** Plain xUnit `Assert.*` (`CollisionSearchFilterQueryParserTests.cs:17-27,50`; `WorkOrderExecutorTests.cs:38-41`). No FluentAssertions.
- **Coverage:** `coverlet.collector` referenced (`iKrock2.Application.Tests.csproj:15`) but never invoked; no `.runsettings`, no scripts, no CI upload.
- **Integration vs unit separation:** None — all tests are pure unit tests. `BackendStatusServiceTests.cs:26-37` constructs real `SqlConnectionFactory` objects but only asserts config flags, never opens a connection.
- **How tests run:** Nothing runs them. Root `local-ci.ps1:40` has a stale TODO (in Swedish) saying "add dotnet test when a test project exists" even though the project now exists; `docs/DEV-SETUP.md:119-120` repeats the stale claim. CI `.github/workflows/ci.yml` is `workflow_dispatch`-only, restore+build only (`:62-68`).
- **Extra notes:** Two test classes reach **private static production methods via reflection** (`StatusCodeParsingTests.cs:22-36`, `CollisionSearchFilterQueryParserTests.cs:105-113`) — a fragile pattern that breaks silently on renames.

### LogSearch

- **Tests exist:** **No** — 0 test projects, 0 test files, no Pester/JS tests. Explicitly acknowledged in `README.md:106-110` ("LogSearch does not currently have an automated test project").
- **Framework / mock lib / assertion style:** None. `Directory.Packages.props:6-11` pins only runtime packages.
- **Layout/naming:** 3 source projects in `LogSearch.slnx:2-4` (`LogSearch.Runtime`, `LogSearch.Service`, `LogSearch.Web`). CPM is enabled (`Directory.Packages.props:2-3`); shared TargetFramework `net10.0` via `Directory.Build.props:3`.
- **Coverage / integration separation:** None.
- **How tests run:** They don't. Local gate `scripts/local-ci.ps1:66-102` = build + component-version validation only; `.github/workflows/ci.yml` is `workflow_dispatch`-only with restore/build/validate (`:65,:69,:82`). The README manual-verification checklist (`README.md:112-124`) covers SQL Server-dependent lease/queue behavior by hand.
- **Natural home for tests:** `LogSearch.Tests/LogSearch.Tests.csproj` beside the source projects (matching the sibling `X.Tests` convention), added to `LogSearch.slnx`, versions pinned in `Directory.Packages.props`. The most testable target is `LogSearch.Runtime` (plain class library) — e.g. the lease/ownership semantics currently verified manually in `README.md:123-124`.

### EArkivChecker

- **Tests exist:** **No** — 0 test projects, 0 test files. Only 3 production projects in `EArkivChecker.slnx:2-4` (Runtime, Service, Web).
- **Framework / mock lib / assertion style:** None. CPM enabled (`Directory.Packages.props:3`) but pins only 6 runtime packages (`:6-11`); TargetFramework `net10.0` repo-wide via `Directory.Build.props:3`.
- **Coverage / integration separation:** None.
- **How tests run:** They don't. `scripts/local-ci.ps1:38-54` = build + version validation; `.github/workflows/ci.yml:63-82` mirrors that (also `workflow_dispatch`-only).
- **Natural home for tests:** `EArkivChecker.Runtime.Tests/` beside `EArkivChecker.Runtime/` (FolderScanner, scan processor, repository are the obvious first targets; Service and Web are thin hosts).

### Dokumentbibliotek

- **Tests exist:** **No** — 0 test projects, 0 test files. Single web-module project `RazorPages/OpenModulePlatform.Web.eArkivDokumentbibliotek.RazorPages.csproj` in the old-format `OpenModulePlatform.Web.eArkivDokumentbibliotek.sln` (plus a cross-repo reference to OMP `OpenModulePlatform.Web.Shared`).
- **Framework / mock lib / assertion style:** None. No `Directory.Packages.props` (no CPM) — only `Directory.Build.targets:1-17` wiring the OMP analyzer. Module targets `net10.0` (`RazorPages/OpenModulePlatform.Web.eArkivDokumentbibliotek.RazorPages.csproj:3`).
- **Coverage / integration separation:** None.
- **How tests run:** They don't. `.github/workflows/ci.yml` is `workflow_dispatch`-only: restore (`:65`), build (`:69`), component-version validation (`:82`). `scripts/local-ci.ps1:53,75` = build + version validation. `scripts/validate-component-versions.ps1:25` has a `-SelfTest` switch — an ad-hoc script self-check, not a unit test.
- **Natural home for tests:** A sibling `OpenModulePlatform.Web.eArkivDokumentbibliotek.Tests/` at repo root added to the same sln, referencing the RazorPages project.

### ODVGateway

- **Tests exist:** **No** — 0 test projects, 0 test files. Single source project `src/ODVGateway/ODVGateway.csproj`; **no `.sln`/`.slnx` at all** (builds target the csproj directly, e.g. `.github/workflows/ci.yml:30`).
- **Framework / mock lib / assertion style:** None. No `Directory.Packages.props`. Project targets `net10.0` (`src/ODVGateway/ODVGateway.csproj:3`); `Directory.Build.props:1-7` sets analysis/style props only.
- **Coverage / integration separation:** None for unit tests.
- **How tests run:** No unit tests, but the repo has the strongest non-unit verification of the testless repos: an end-to-end PowerShell **smoke test** that builds the app, launches the real Kestrel process with an `appsettings.Smoke.json` overlay, and checks `/health`, security headers, and sanitized error responses (`scripts/smoke-test.ps1:1-21`, checks at `:302-346`). It runs in the local gate `scripts/local-ci.ps1:8-10` (enforced by `.githooks/pre-push:33`) and in `.github/workflows/ci.yml:28-39` / `release.yml:49-66`.
- **Natural home for tests:** `src/ODVGateway.Tests/ODVGateway.Tests.csproj` beside the source project, plus a new `.slnx` to group them.

### OpenDocViewer

- **Tests exist:** Yes — pure JS repo (no `.csproj` at all), 4 test files.
  - `src/utils/__tests__/runtimeConfig.test.js`, `src/utils/__tests__/documentLoadingConfig.test.js`, `src/logging/__tests__/systemLogger.test.js`, `public/__tests__/web-headers.test.js`
- **Framework:** vitest — `"test": "vitest run"` (`package.json:35`), `"test:watch": "vitest"` (`package.json:36`); imports `from 'vitest'` (e.g. `src/utils/__tests__/runtimeConfig.test.js:10`).
- **Layout/naming:** Colocated `__tests__/` folders beside source modules (`src/<area>/__tests__/*.test.js`); one outlier `public/__tests__/web-headers.test.js` that asserts on the shipped IIS `public/web.config` read from disk (`web-headers.test.js:5-6`). No vitest config anywhere — `vite.config.js` has no `test` key; tests rely on vitest defaults (node environment).
- **Mock library:** None — no `vi.fn`/`vi.mock`/`vi.spyOn` anywhere; all tests exercise pure functions (plus the one real-file read).
- **Package pins:** `vitest` `^4.0.0` in devDependencies (`package.json:92`); no `@vitest/coverage-*`. Engines `node ^22.18.0 || >=24.11.0` (`package.json:25-27`); CI pins node 22.18.0 (`.github/workflows/ci.yml:22`). `package-lock.json` present and registry-validated in CI (`ci.yml:27-35`).
- **Assertion style:** vitest `expect` — `runtimeConfig.test.js:41` (`toEqual`), `systemLogger.test.js:15` (`toBe`), `web-headers.test.js:15` (`toMatch`).
- **Coverage:** None found — no coverage script, package, or CI step.
- **Integration vs unit separation:** None — all 4 files are node-environment unit tests (see header comment `runtimeConfig.test.js:5-7`). Two PowerShell helpers exist but are manual smoke tools, not automated tests: `scripts/Test-ODV-IISProxy.ps1`, `scripts/omp/test-cmd-wrappers.ps1`.
- **How tests run:** Locally via `npm test`. **CI does NOT run tests** — `.github/workflows/ci.yml:67-74` runs lint + build only; `release.yml:97-104` likewise.
- **Extra notes:** Tests are deliberately concentrated on pure config/normalization helpers; no component/React tests. New tests naturally belong in `src/<area>/__tests__/<module>.test.js`.

### AgentDocMap

- **Tests exist:** Yes — pure JS repo, 5 test files in top-level `test/` (`concurrency.test.js`, `generate.test.js`, `outputGuard.test.js`, `secretSafety.test.js`, `writers.test.js`), ~26 `test(...)` cases, plus helper `test/testUtils.js` and a committed fixture project `test/fixture-project/`.
- **Framework:** node:test (built-in runner), no third-party framework. `"test": "node --test test/*.test.js"` (`package.json:16`); `import test from 'node:test'` (`test/generate.test.js:5`).
- **Layout/naming:** Flat top-level `test/` folder; `*.test.js` named after the source unit (`test/writers.test.js` ↔ `src/lib/writers.js`). Shared helper `withTempDir` at `test/testUtils.js:17`.
- **Mock library:** None — tests use real temp dirs and the fixture project (`test/testUtils.js:17`).
- **Package pins:** No test deps to pin (node:test is built-in). Runtime pinned by `engines: node >=22.18.0` (`package.json:11-13`) and CI `node-version: '22.18.0'` (`.github/workflows/ci.yml:31`). `package-lock.json` exists (`lockfileVersion: 3`); CI uses `npm ci` (`ci.yml:38`). No devDependencies at all.
- **Assertion style:** `node:assert/strict` — `test/generate.test.js:1,19-24` (`assert.equal`, `assert.match`).
- **Coverage:** None found — `.gitignore:4` lists `coverage/` but nothing produces it.
- **Integration vs unit separation:** None — all tests are filesystem-backed (temp dirs + fixture project) in one suite; only platform-conditional path selection (`test/outputGuard.test.js:31-37`).
- **How tests run:** `npm test`; `npm run validate` = test + regenerate the committed OpenDocViewer example packet (`package.json:14-18`). **CI runs the tests** — `.github/workflows/ci.yml:40-42` runs `npm run validate` on ubuntu (the only repo in the ecosystem whose GitHub CI executes tests). Documented in `CONTRIBUTING.md:16-24`.
- **Extra notes:** The "integration check" is regenerating `examples/opendocviewer-agent-docs/` from a sibling OpenDocViewer checkout (`ci.yml:22-26`). Minor duplication: a local `withTempDir` copy in `test/secretSafety.test.js:9` instead of importing `testUtils.js`.

## 2. Comparison matrix

| Repo | Tests? | Framework | Mock lib | Test pins | Assertion style | Coverage | Integration gating | Tests run by |
|---|---|---|---|---|---|---|---|---|
| **OpenModulePlatform** | Yes — 3 projects, ~270 methods | xUnit 2.9.3 + runner 3.1.5 | None (hand-written fakes) | CPM: Test.Sdk 18.7.0, coverlet.collector 10.0.1 | Plain `Assert.*` | coverlet referenced (2 of 3 projects), never invoked | Tier C/D naming; DB tests fail without local SQL Server | Pre-push hook only; CI build-only |
| **IbsPackager** | Yes — 2 projects, ~42 methods | xUnit 2.9.3 + runner 3.1.5 + SkippableFact 1.4.13 | None (stubs + NullLogger) | CPM: Test.Sdk 18.7.0, coverlet.collector 10.0.1 | Plain `Assert.*` | coverlet referenced, never invoked | `[SkippableFact]` + env connection string | Nothing automated |
| **VajSkrivare** | Yes — 1 project, 2 tests | xUnit 2.9.3 + runner **2.8.2** | None (hand-written fake) | **Inline (no CPM)**: Test.Sdk **17.14.0**, no coverlet | Plain `Assert.*` | None | None (tests avoid DB via config) | Nothing automated |
| **iKrock2** | Yes — 1 project, 35 methods | xUnit 2.9.3 + runner 3.1.5 | None (Options.Create) | CPM: Test.Sdk 18.7.0, coverlet.collector 10.0.1 | Plain `Assert.*` | coverlet referenced, never invoked | None (pure unit tests) | Nothing automated (stale TODO) |
| **LogSearch** | **No** | — | — | — | — | — | — | — |
| **EArkivChecker** | **No** | — | — | — | — | — | — | — |
| **Dokumentbibliotek** | **No** | — | — | — | — | — | — | — |
| **ODVGateway** | **No** (has e2e smoke script) | — | — | — | — | — | — | Smoke test in local gate + CI |
| **OpenDocViewer** | Yes — 4 files | vitest ^4.0.0 | None | package.json + lockfile | vitest `expect` | None | None | `npm test` locally only; CI build-only |
| **AgentDocMap** | Yes — 5 files, ~26 cases | node:test (built-in) | None | engines + lockfile | `node:assert/strict` | None | None | `npm test` + **CI runs `npm run validate`** |

### Key divergences

- **Testless repos:** 4 of 8 .NET repos have zero automated tests — **LogSearch**, **EArkivChecker**, **Dokumentbibliotek**, **ODVGateway**. ODVGateway at least has an end-to-end smoke script; the other three rely on manual verification.
- **Framework is consistent where tests exist:** xUnit in every .NET repo; vitest in OpenDocViewer; node:test in AgentDocMap. No NUnit/MSTest/jest/mocha anywhere.
- **No mock framework anywhere:** every repo uses hand-written fakes/stubs. This is a deliberate-looking, ecosystem-wide pattern.
- **Pin drift in VajSkrivare:** the only repo without CPM pins the older `Microsoft.NET.Test.Sdk` 17.14.0 and `xunit.runner.visualstudio` 2.8.2 (vs 18.7.0 / 3.1.5 everywhere else) and has no coverlet reference.
- **Coverage is decorative:** `coverlet.collector` is referenced in OMP (2 of 3 test projects), IbsPackager, and iKrock2, but no `.runsettings`, script, or CI step ever collects coverage.
- **CI gap:** only **AgentDocMap** runs tests in GitHub CI. OMP runs tests in the local pre-push hook; all other repos' CI/local-ci gates are build-only.
- **DB-test gating split:** IbsPackager skips cleanly without SQL Server (`[SkippableFact]` + env var); OMP's Tier C tests hard-fail without local SQL Server. No `[Trait]`/`[Collection]`/Testcontainers anywhere.
- **Layout outliers:** VajSkrivare uses a top-level `tests/` folder instead of sibling `X.Tests/`; `IbsPackager.Tests` is named after the repo, not its actual target (`IbsPackager.Runtime`); ODVGateway has no solution file at all.
- **Fragile patterns:** iKrock2 tests reach private statics via reflection; iKrock2's `local-ci.ps1:40` and `docs/DEV-SETUP.md:119` still claim no test project exists (stale, Swedish TODO).

## 3. Recommended standard

### .NET ecosystem

**xUnit + hand-written fakes + plain `Assert.*`, pinned centrally via CPM, in a sibling `<ProjectUnderTest>.Tests` project, executed in the local pre-push gate.**

This is already the de-facto standard: OMP, IbsPackager, and iKrock2 use identical frameworks and pins, and no repo uses a mock framework or FluentAssertions.

- **Project layout:** `<ProjectUnderTest>.Tests/<ProjectUnderTest>.Tests.csproj` at repo root beside the source project, included in the `.slnx`; test subfolders mirror the source structure. Test classes `<Subject>Tests`; methods `Method_WhenCondition_ExpectedResult`. Add `<Using Include="Xunit" />` (or a `Usings.cs`) instead of per-file usings.
- **Pins (in `Directory.Packages.props`, CPM):** `Microsoft.NET.Test.Sdk` **18.7.0**, `xunit` **2.9.3**, `xunit.runner.visualstudio` **3.1.5**, `coverlet.collector` **10.0.1**. TargetFramework `net10.0`. Add `Microsoft.AspNetCore.Mvc.Testing` (currently 10.0.x) for web-host tests and `Xunit.SkippableFact` **1.4.13** for DB-gated tests.
- **Mocking:** keep the no-mock-framework rule. Write small hand-written fakes (see `FakeOmpHostArtifactRepository`, `FakeOptionsMonitor`, `ManualTimeProvider` in OMP). When a production class is hard to fake (sealed, SQL-coupled), refactor it behind an interface rather than introducing Moq or testing private members via reflection.
- **Assertions:** plain xUnit `Assert.*`. Do not add FluentAssertions.
- **Unit vs integration:** adopt OMP's Tier suffix naming — `*TierDTests` for pure in-memory tests, `*TierCTests` for tests needing real SQL Server — **combined with** IbsPackager's gating: `[SkippableFact]` + connection string from an env var (e.g. `<REPO>_TEST_CONNECTION_STRING`) with a localhost default, skipping cleanly when the database/schema is absent. DB tests must create/drop their own uniquely named database or GUID-suffixed rows and never touch shared data. Tier D tests must pass on any machine with only the .NET SDK.
- **Execution:** wire `dotnet test` into the repo's local pre-push gate (`scripts/local-ci.ps1` / `.githooks/pre-push.ps1`), the OMP `pre-push.ps1:88-89` pattern. GitHub CI may stay build-only (metered minutes, no SQL Server on runners), but repos whose tests are all Tier D should consider adding a CI `dotnet test` step.
- **Coverage:** keep `coverlet.collector` in every test project (it is harmless), and document the manual invocation `dotnet test --collect:"XPlat Code Coverage"`. No CI coverage upload is required today.

### JS/npm ecosystem

**vitest for browser/front-end apps, node:test for pure Node tooling; no mock library by default; tests must run in CI.**

- **Framework choice:** follow the existing split — OpenDocViewer's vitest (`^4.0.0`) for anything DOM/React-adjacent or Vite-based; AgentDocMap's `node --test` for dependency-free Node CLI/library code. Do not introduce jest or mocha.
- **Layout:** colocated `src/<area>/__tests__/*.test.js` for app code (OpenDocViewer pattern); top-level `test/*.test.js` mirroring `src/lib/` units for CLI tools (AgentDocMap pattern).
- **Mocking:** start without mocks against pure functions (the dominant pattern); reach for `vi.fn()`/`vi.spyOn()` (vitest) or `node:test`'s built-in `mock` only when a boundary genuinely requires it. Do not add sinon or similar.
- **Assertions:** vitest `expect` / `node:assert/strict` respectively — keep using each runner's native style.
- **Execution:** `npm test` must exist and must be wired into CI (AgentDocMap's `npm run validate` in `ci.yml:40-42` is the model). Coverage is optional; if wanted, use `@vitest/coverage-v8` or `node --test` with its built-in coverage.

## 4. Migration notes per diverging/testless repo

### LogSearch (testless)

- **Current state:** No automated tests; manual SQL Server verification checklist in `README.md:112-124`. CPM and `net10.0` already in place.
- **Migration:** Add `LogSearch.Tests/LogSearch.Tests.csproj` beside the source projects with the standard xUnit pins in `Directory.Packages.props`, add it to `LogSearch.slnx`, and start with Tier D tests of `LogSearch.Runtime` lease/ownership semantics (currently verified manually). Wire `dotnet test` into `scripts/local-ci.ps1`. DB-dependent behavior gets Tier C tests with the SkippableFact + env-var pattern.
- **Priority:** **High** — core queue/lease logic currently has only manual verification.

### EArkivChecker (testless)

- **Current state:** No automated tests; build-only local-ci and CI.
- **Migration:** Add `EArkivChecker.Runtime.Tests/` beside `EArkivChecker.Runtime/` (FolderScanner, scan processor, repository are the first targets; Service and Web are thin hosts), standard CPM pins, add to `EArkivChecker.slnx`, wire `dotnet test` into `scripts/local-ci.ps1`.
- **Priority:** **High** — scan/notification logic is business-critical and unverified.

### Dokumentbibliotek (testless)

- **Current state:** No automated tests; old-format `.sln`; no CPM.
- **Migration:** Add a sibling `OpenModulePlatform.Web.eArkivDokumentbibliotek.Tests/` project at repo root referencing the RazorPages project. Either introduce `Directory.Packages.props` (preferred, matches the ecosystem) or accept inline pins at the standard versions. Add `dotnet test` to `scripts/local-ci.ps1`. Form/image service logic is the natural first target.
- **Priority:** **Medium** — module logic is thinner, but document/form handling would benefit from regression tests.

### ODVGateway (testless, has e2e smoke)

- **Current state:** No unit tests, but a solid end-to-end smoke script (`scripts/smoke-test.ps1`) runs in the local gate and CI. No solution file; no CPM.
- **Migration:** Keep the smoke script as the e2e gate. Add `src/ODVGateway.Tests/` + a new `.slnx`, and unit-test the session-store capacity and security-header logic that today is only covered end-to-end (fast feedback without launching Kestrel). Standard pins.
- **Priority:** **Medium** — the smoke test already catches integration regressions, so this is about fast, focused feedback.

### VajSkrivare (diverging pins/layout)

- **Current state:** xUnit but pinned inline (no CPM) at older versions (Test.Sdk 17.14.0, runner 2.8.2), no coverlet, top-level `tests/` layout, only 2 tests.
- **Migration:** Introduce `Directory.Packages.props` and move test pins to the standard versions (18.7.0 / 3.1.5 / coverlet.collector 10.0.1). Keep the `tests/` folder (renaming is low-value churn) but grow the suite — `WebApplicationFactory` + config overrides with no DB is a good pattern to extend. Add `dotnet test` to `scripts/local-ci.ps1`.
- **Priority:** **Low–Medium** — aligned in spirit, drifted in pins and coverage.

### iKrock2 (mostly aligned)

- **Current state:** Standard xUnit/CPM stack, but nothing runs the tests; stale Swedish TODO in `local-ci.ps1:40` and stale claim in `docs/DEV-SETUP.md:119`; two test classes use reflection into private statics.
- **Migration:** Wire `dotnet test` into `local-ci.ps1` and delete the stale TODO/docs lines (keeping comments in English per repo convention). Replace the reflection-based tests with public-API tests, refactoring production code behind interfaces where needed (the sealed SQL-coupled classes noted in `WorkOrderExecutorTests.cs:11-13`).
- **Priority:** **Low** — small cleanup, no new infrastructure.

### IbsPackager (aligned, minor naming note)

- **Current state:** Standard stack with the ecosystem's best DB-test gating.
- **Migration:** Optionally rename `IbsPackager.Tests` → `IbsPackager.Runtime.Tests` to match the `<ProjectUnderTest>.Tests` convention. Add `dotnet test` to `scripts/local-ci.ps1`. Otherwise the reference model for DB-backed tests.
- **Priority:** **Low**.

### OpenModulePlatform (reference implementation)

- **Current state:** The de-facto standard; tests run in the pre-push hook but not CI; Tier C DB tests hard-fail without local SQL Server; Analyzers.Tests lacks coverlet.
- **Migration:** Adopt IbsPackager's `[SkippableFact]` + env-var gating for Tier C tests so `dotnet test` passes on machines without SQL Server. Add `coverlet.collector` to the Analyzers.Tests project for consistency. Consider a CI step that runs Tier D tests only.
- **Priority:** **Low**.

### OpenDocViewer (CI gap)

- **Current state:** vitest suite exists but CI runs lint+build only (`ci.yml:67-74`).
- **Migration:** Add an `npm test` step to `.github/workflows/ci.yml` (and release workflow if desired). Optionally add `@vitest/coverage-v8` and a `test:coverage` script. Grow tests along the existing `__tests__/` pattern when touching config/logging code.
- **Priority:** **Medium** — the suite exists; it just isn't enforced.

### AgentDocMap (aligned)

- **Current state:** Follows the JS standard fully, including CI execution.
- **Migration:** None required. Optional cleanup: deduplicate the `withTempDir` copy in `test/secretSafety.test.js:9`.
- **Priority:** **Very low**.

### Repos already aligned

- **.NET:** OpenModulePlatform (reference), IbsPackager, iKrock2 — identical xUnit/CPM pins and conventions.
- **JS:** AgentDocMap — node:test, lockfile, CI-enforced.
