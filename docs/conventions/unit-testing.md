# Unit-testing conventions for OMP+ODV

This document has one job: say what the current testing state of the OMP+ODV family is and what
the standard is. Section 1 is the measured state (re-measured 2026-09-06 from `origin/main` of
every repository), section 2 the standard, section 3 the work that is still open, and section 4
the dated history of how the document got here. Do not add dated "superseded" layers to sections
1-3 again; update the numbers in place and add a line to the changelog.

Repos covered: OpenModulePlatform, IbsPackager, LogSearch, EArkivChecker, Dokumentbibliotek,
VajSkrivare, iKrock2, ODVGateway (.NET); OpenDocViewer, AgentDocMap (JS/npm).

## 1. Current state (measured 2026-09-06)

### 1.1 Family-wide package pins

Read from every repository's `Directory.Packages.props` on the same day (ODVGateway: inline in
`tests/ODVGateway.Tests/ODVGateway.Tests.csproj`, because it has no CPM file yet).

| Package | Version | Where |
|---|---|---|
| `Microsoft.NET.Test.Sdk` | **18.9.0** | all eight .NET repos |
| `xunit` | **2.9.3** | all eight (the latest and final v2 core release) |
| `xunit.runner.visualstudio` | **4.0.0** | all eight |
| `Xunit.SkippableFact` | **1.5.85** | the seven CPM repos (ODVGateway does not use it) |
| `Microsoft.Playwright` | **1.62.0** | the seven repos with a `*.UiTests` project (all but ODVGateway) |
| `coverlet.collector` | **10.0.1** | six repos; **not** VajSkrivare, **not** ODVGateway |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.11 | where web-host tests exist |
| `vitest` | `^4.1.11` | OpenDocViewer (`package.json`) |
| `node:test` | built in | AgentDocMap (no test dependencies at all) |

`global.json` pins the SDK in every .NET repo except VajSkrivare. When these numbers drift again,
fix this table first: duplicating a version in seven places is what let the document go stale.

### 1.2 Per-repo state

"Local gate" is the repository's `scripts/local-ci.ps1` (or root `local-ci.ps1`), run by the
pre-push hook. "CI" is `.github/workflows/ci.yml`.

| Repo | Test projects on `origin/main` | Framework | Pins | Local gate runs tests | CI runs tests |
|---|---|---|---|---|---|
| **OpenModulePlatform** | 8 xUnit projects (`Bootstrapper.Tests`, `HostAgent.Runtime.Tests`, `Portal.Tests`, `UiTests`, `Web.Shared.Analyzers.Tests`, `Worker.Abstractions.Tests`, `WorkerManager.WindowsService.Tests`, `WorkerProcessHost.Tests`) + 9 Pester 5 suites in `tests/*.Tests.ps1` | xUnit + Playwright; Pester 5 (5.9.1) | CPM | Yes: `dotnet test` per project, Pester suites, zero-execution TRX gate | Yes: `dotnet test` (Integration, lease and `Category=Ui` excluded by filter), Pester suites, TRX gate |
| **IbsPackager** | 4 (`IbsPackager.Tests`, `IbsPackager.ChannelTypes.FileDrop.Tests`, `IbsPackager.ChannelTypes.ImageCompose.Tests`, `IbsPackager.UiTests`) | xUnit + SkippableFact + Playwright | CPM | Yes | No (build + validate only) |
| **LogSearch** | 2 (`LogSearch.Tests`, `LogSearch.UiTests`) | xUnit + Playwright | CPM | Yes | No |
| **EArkivChecker** | 3 (`EArkivChecker.Runtime.Tests`, `EArkivChecker.Web.Tests`, `EArkivChecker.UiTests`) | xUnit + SkippableFact + Playwright | CPM | Yes | No |
| **Dokumentbibliotek** | 2 (`tests/OpenModulePlatform.Web.eArkivDokumentbibliotek.Tests`, `...UiTests`) | xUnit + Playwright | CPM | Yes | No |
| **VajSkrivare** | 2 (`tests/Skrivarkoppling.Web.Tests`, `tests/Skrivarkoppling.Web.UiTests`) | xUnit + Playwright | CPM; no coverlet; no `global.json` | Yes | No |
| **iKrock2** | 2 (`iKrock2.Application.Tests`, `iKrock2.UiTests`) | xUnit + Playwright | CPM | Yes (`dotnet test iKrock2.slnx --no-build --filter "Category!=Ui"`) | No |
| **ODVGateway** | 1 (`tests/ODVGateway.Tests`) + end-to-end smoke script `scripts/smoke-test.ps1` | xUnit | Inline in the test csproj; no CPM; no solution file | Yes (`dotnet test` + smoke) | Smoke script only |
| **OpenDocViewer** | vitest suite under `src/**/__tests__/` and `public/__tests__/` | vitest | `package.json` + lockfile | `npm test` | Yes (`npm test`, `ci.yml:71`) |
| **AgentDocMap** | `test/*.test.js` | node:test | engines + lockfile | `npm test` | Yes (`npm run validate`, `ci.yml:42`) |

Family-wide facts that follow from the table:

- Every repository has automated tests and runs them in its local pre-push gate.
- Framework is uniform: xUnit in every .NET repo (no NUnit/MSTest), vitest for the browser app,
  node:test for the Node tool. No mock framework anywhere - every repo uses hand-written fakes.
  No FluentAssertions anywhere.
- GitHub CI executes tests in OpenModulePlatform, OpenDocViewer and AgentDocMap. The consumer
  .NET repos keep CI at build + validate (metered minutes, no SQL Server on the runners); their
  tests run in the local gate.
- CPM (`Directory.Packages.props`) is present in 7 of 8 .NET repos; ODVGateway is the only one
  without it.
- Coverage is decorative: `coverlet.collector` is referenced where present but no `.runsettings`,
  script or CI step collects it.

### 1.3 OpenModulePlatform test execution in detail

OpenModulePlatform is the reference implementation, so its gates are described here rather than
in the per-repo table.

- **Layout:** sibling `<ProjectUnderTest>.Tests/` at repo root, subfolders mirroring the source
  (`Services/`, `Models/`, `Security/`, `Configuration/`, `Integration/`), all referenced from
  `OpenModulePlatform.slnx`. Method naming `Method_WhenCondition_ExpectedResult`. Global
  `<Using Include="Xunit" />` in each csproj.
- **Fakes:** `OpenModulePlatform.HostAgent.Runtime.Tests/Services/FakeOmpHostArtifactRepository.cs`,
  `FakeOptionsMonitor.cs`, `ManualTimeProvider.cs`;
  `OpenModulePlatform.Portal.Tests/Integration/TestAuthHandler.cs`.
- **Unit vs integration:** Tier suffix on class names - `*TierDTests` = pure in-memory,
  `*TierCTests` = real SQL Server (`OmpHostArtifactRepositoryTierCTests.cs`,
  `HostAgentEngineTierDTests.cs`). DB-backed tests use `IClassFixture` over per-class databases:
  `OmpHostArtifactRepositoryTestDatabase.cs` honors `OMP_TEST_CONNECTION_STRING` (default
  `Server=(local);Integrated Security=true`), creates a uniquely named database per test class
  tagged with owner machine + PID + process-start ticks, sweeps only databases whose owner is
  verifiably dead (or unidentifiable and older than 24 h), and reports cleanup failures to
  `OMP_TEST_CLEANUP_LOG`, which `ci.yml` surfaces as workflow warnings. Web hosting uses
  `WebApplicationFactory<PortalResource>` (`Portal.Tests/Integration/PortalWebApplicationFactory.cs`).
  Tier C tests fail (rather than skip) on a machine without SQL Server.
- **UI tier:** `OpenModulePlatform.UiTests` (xUnit + Microsoft.Playwright) with the shared fixtures
  in `tests/shared/` (`OmpTestDatabaseProvisioner.cs`, `Ui/PlaywrightSessionFixture.cs`,
  `Ui/WebAppProcessFixture.cs`, `Ui/UiInvariantScanner.cs`, `Ui/UiTestPaths.cs`); consumer repos
  link these files rather than copying them.
- **Local gate:** `scripts/local-ci.ps1`, run by `.githooks/pre-push.ps1`: module-definition and
  SQL-ownership validation, component-version validation against the resolved baseline,
  PSScriptAnalyzer, the Pester suites via `scripts/omp/run-script-tests.ps1`, a Release build,
  `dotnet test` per project with TRX output, and `scripts/omp/assert-tests-executed.ps1
  -ShowSkipReasons -RequirePerFile -MinimumTrxFiles <project count>`.
- **GitHub CI:** `ci.yml` derives its build/test legs as a .NET version matrix from `global.json`
  and the committed target frameworks (`scripts/omp/get-ci-version-matrix.ps1`): the pinned SDK
  band and the newest `rollForward` band run on every push; a runtime-floor leg
  (`DOTNET_ROLL_FORWARD=Disable`) runs weekly. It provisions LocalDB and runs `dotnet test` with a
  `--filter` whose exclusions are registered in `docs/TEST_DEBT.md`, then the Pester suites, then
  the zero-execution TRX gate.
- **Pester:** the suites use the Pester 5 dialect; `run-script-tests.ps1` pins Pester 5.9.1,
  restored on demand into the repo-local `.psmodules` cache by `scripts/omp/pester-bootstrap.ps1`
  (process-local `PSModulePath`, so a divergent global Pester cannot affect the run). Per-suite
  harness code lives in `tests/*.TestHelpers.ps1`, dot-sourced from each `Describe` block's
  `BeforeAll` because Pester 5 runs containers in a separate session state. Both gates invoke the
  runner through `powershell.exe` because one suite spawns child `powershell.exe` processes.
- **Zero-execution gate:** VSTest exits 0 when a filter matches nothing, so
  `assert-tests-executed.ps1` parses every `.trx` and fails when `executed == 0` (with
  `-RequirePerFile`, when any single file shows 0), when a `.trx` lacks `ResultSummary/Counters`,
  or when fewer than `-MinimumTrxFiles` results exist. Consumer repos call the OMP copy.
- **Analyzer tests:** `OpenModulePlatform.Web.Shared.Analyzers.Tests` uses
  `CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>` with inline source strings - the model for
  future Roslyn analyzers.

### 1.4 Reference implementations in the consumer repos

These are the patterns the standard in section 2 points at; the file references are from the
2026-07-15 audit and are kept because the patterns have not changed.

- **DB-test gating (IbsPackager, EArkivChecker):** `[SkippableFact]` + connection string from an
  environment variable (`IBSPACKAGER_TEST_CONNECTION_STRING`,
  `EARKIVCHECKER_TEST_CONNECTION_STRING`) with a localhost default; `SkipException` when the
  database or schema is absent; the procedure or schema under test is self-deployed from the
  repo's own `sql/` setup script; GUID-suffixed rows or a uniquely named per-class database with
  cleanup on dispose. EArkivChecker pairs this with OMP's Tier C/D class naming
  (`EArkivCheckerRepositoryTierCTests.cs`, `FolderScannerTierDTests.cs`) and is the cleanest Tier C
  pattern in the family.
- **Hand-written fakes instead of a mock library:** `NullLogger<T>.Instance` and small stubs
  (`TestConfiguration : IConfiguration`, `NoChangeToken`) in IbsPackager; `Options.Create(...)`
  hand-wiring in iKrock2; `FakeZebraConfigService` in VajSkrivare.
- **Web-host tests without a database:** VajSkrivare's `WebApplicationFactory<Program>` +
  in-memory configuration overrides (`tests/Skrivarkoppling.Web.Tests/ApiAnonymityTests.cs`).
- **End-to-end smoke as a gate:** ODVGateway's `scripts/smoke-test.ps1` builds the app, launches
  the real Kestrel process with an `appsettings.Smoke.json` overlay and checks `/health`,
  security headers and sanitized error responses, in both the local gate and CI.
- **JS:** OpenDocViewer keeps tests colocated in `src/<area>/__tests__/*.test.js` against pure
  config/normalization helpers (one outlier asserts on the shipped `public/web.config`);
  AgentDocMap keeps a flat `test/` folder with `withTempDir` in `test/testUtils.js` and a
  committed fixture project.

## 2. Recommended standard

### 2.1 .NET

**xUnit + hand-written fakes + plain `Assert.*`, pinned centrally via CPM, in a sibling
`<ProjectUnderTest>.Tests` project plus a `<Repo>.UiTests` project, executed in the local pre-push
gate.**

- **Project layout:** `<ProjectUnderTest>.Tests/<ProjectUnderTest>.Tests.csproj` at repo root
  beside the source project, included in the `.slnx`; test subfolders mirror the source structure.
  Test classes `<Subject>Tests`; methods `Method_WhenCondition_ExpectedResult`. Add
  `<Using Include="Xunit" />` (or a `Usings.cs`) instead of per-file usings.
- **Pins (in `Directory.Packages.props`, CPM):** `Microsoft.NET.Test.Sdk` **18.9.0**, `xunit`
  **2.9.3**, `xunit.runner.visualstudio` **4.0.0**, `coverlet.collector` **10.0.1**. TargetFramework
  `net10.0`, SDK pinned in `global.json`. Add `Microsoft.AspNetCore.Mvc.Testing` (currently 10.0.x)
  for web-host tests, `Xunit.SkippableFact` **1.5.85** for DB-gated tests and `Microsoft.Playwright`
  **1.62.0** for the UI tier.
- **Runner baseline rationale (decided 2026-08-31):** the supported baseline is a runner-only
  upgrade. The adapter's package documentation states that `xunit.runner.visualstudio` 4.0.0 runs
  projects built with xUnit.net v2 and v3, and its `net8.0` asset has no dependency on an xUnit
  core package. The v2-to-v3 migration (renaming `xunit` to `xunit.v3`, executable test projects,
  v3 APIs) is a separate step and not a prerequisite for runner 4.0.0; it is outside this baseline.
  Repositories that link `tests/shared/Ui/*.cs` apply the exact pins above; no shared fixture API
  changes are required. References: the
  [Visual Studio adapter package](https://www.nuget.org/packages/xunit.runner.visualstudio/4.0.0),
  [xUnit.net runner package guidance](https://xunit.net/docs/nuget-packages-v3) and
  [the v2-to-v3 migration guide](https://xunit.net/docs/getting-started/v3/migration).
- **Mocking:** keep the no-mock-framework rule. Write small hand-written fakes (see section 1.3 and
  1.4). When a production class is hard to fake (sealed, SQL-coupled), refactor it behind an
  interface rather than introducing Moq or testing private members via reflection.
- **Assertions:** plain xUnit `Assert.*`. Do not add FluentAssertions.
- **Unit vs integration:** OMP's Tier suffix naming - `*TierDTests` for pure in-memory tests,
  `*TierCTests` for tests needing real SQL Server - **combined with** IbsPackager's gating:
  `[SkippableFact]` + connection string from `<REPO>_TEST_CONNECTION_STRING` with a localhost
  default, skipping cleanly when the database/schema is absent. DB tests create/drop their own
  uniquely named database or GUID-suffixed rows and never touch shared data. Tier D tests must
  pass on any machine with only the .NET SDK.
- **UI tier:** `*.UiTests` on xUnit + Microsoft.Playwright, linking the shared fixtures from
  `OpenModulePlatform/tests/shared/Ui/`, tagged `Category=Ui` so gates can exclude them where no
  browser is available.
- **Execution:** `dotnet test` in the repo's local pre-push gate (`scripts/local-ci.ps1` /
  `.githooks/pre-push.ps1`), followed by the OMP zero-execution gate
  `scripts/omp/assert-tests-executed.ps1`. GitHub CI may stay build-only for consumer repos
  (metered minutes, no SQL Server on runners); repos whose tests are all Tier D should consider a
  CI `dotnet test` step.
- **Coverage:** keep `coverlet.collector` in every test project (it is harmless) and document the
  manual invocation `dotnet test --collect:"XPlat Code Coverage"`. No CI coverage upload is
  required today.

### 2.2 JS/npm

**vitest for browser/front-end apps, node:test for pure Node tooling; no mock library by default;
tests must run in CI.**

- **Framework choice:** OpenDocViewer's vitest for anything DOM/React-adjacent or Vite-based;
  AgentDocMap's `node --test` for dependency-free Node CLI/library code. Do not introduce jest or
  mocha.
- **Layout:** colocated `src/<area>/__tests__/*.test.js` for app code; top-level `test/*.test.js`
  mirroring `src/lib/` units for CLI tools.
- **Mocking:** start without mocks against pure functions; reach for `vi.fn()`/`vi.spyOn()`
  (vitest) or `node:test`'s built-in `mock` only when a boundary genuinely requires it. Do not add
  sinon or similar.
- **Assertions:** vitest `expect` / `node:assert/strict` respectively.
- **Execution:** `npm test` must exist and must be wired into CI (both JS repos already are).
  Coverage is optional; if wanted, use `@vitest/coverage-v8` or `node --test`'s built-in coverage.

## 3. Open work

Only items that are still open on 2026-09-06; everything else from the original migration plan is
done and recorded in the changelog.

| Repo | Remaining | Priority |
|---|---|---|
| VajSkrivare | Add `coverlet.collector` 10.0.1 to `Directory.Packages.props`; add a `global.json` SDK pin (the only repo without one, so it floats to the build host's SDK). Keep the `tests/` folder layout - renaming is low-value churn. | Low |
| ODVGateway | Introduce `Directory.Packages.props` (the only .NET repo without CPM) and a `.slnx` grouping `src/ODVGateway` and `tests/ODVGateway.Tests`; keep the smoke script as the e2e gate. | Low-Medium |
| OpenModulePlatform | Adopt `[SkippableFact]` + env-var gating for Tier C tests so `dotnet test` passes on machines without SQL Server (CI currently sidesteps this with a `--filter`, tracked in `docs/TEST_DEBT.md`). Confirm `coverlet.collector` is referenced by every test project. | Low |
| iKrock2 | Replace the two test classes that reach private static production methods via reflection (`StatusCodeParsingTests.cs`, `CollisionSearchFilterQueryParserTests.cs`, noted in the 2026-07-15 audit; not re-measured) with public-API tests. | Low |
| IbsPackager | Optionally rename `IbsPackager.Tests` to `IbsPackager.Runtime.Tests` to match the `<ProjectUnderTest>.Tests` convention (it tests `IbsPackager.Runtime`). | Very low |
| AgentDocMap | Deduplicate the local `withTempDir` copy in `test/secretSafety.test.js` by importing `test/testUtils.js`. | Very low |
| Consumer .NET repos | GitHub CI is build + validate only; tests run in the local gate. Consider a Tier D-only `dotnet test` step. | Low |

## 4. Changelog

Newest first. Earlier versions of this document carried these entries as struck-through text and
dated banners inside the audit body; they were consolidated into sections 1-3 on 2026-09-06.

- **2026-09-06** - Consolidated the document: one measured state table, standard, open work,
  changelog. Re-measured every `Directory.Packages.props` on `origin/main`: family-wide
  `Microsoft.NET.Test.Sdk` 18.9.0, `xunit` 2.9.3, `xunit.runner.visualstudio` 4.0.0,
  `coverlet.collector` 10.0.1, `Microsoft.Playwright` 1.62.0, `Xunit.SkippableFact` 1.5.85; the
  comparison matrix and the recommended-standard pins had still quoted the July values
  (18.7.0 / 3.1.5 / 1.4.13). Measured test-project counts per repo (IbsPackager 4, EArkivChecker 3,
  every other .NET repo 2, ODVGateway 1) and that every local gate runs `dotnet test`.
- **2026-09-04** - `tests/` holds eight Pester suites (`ArtifactPackageWorkerHostRoundTrip`,
  `Assert-LegSdk`, `Assert-RunnerSignature`, `Assert-TestsExecuted`, `Bump-Version`,
  `Get-CiVersionMatrix`, `Validate-ComponentVersions`, `Validate-SharedScripts`);
  `Validate-ModuleDefinitions` followed on 2026-09-06, making nine.
- **2026-09-03** - Pester migration: script suites moved to the Pester 5 dialect,
  `run-script-tests.ps1` pins Pester 5.9.1, the 3.4.0 dialect (`Should Be`) is gone, per-suite
  harness code moved to `tests/*.TestHelpers.ps1`. The zero-execution TRX gate moved into the
  shared `scripts/omp/assert-tests-executed.ps1` (with `-RequirePerFile`) and was wired into both
  `ci.yml` and `scripts/local-ci.ps1`; consumer repos call the OMP copy.
- **2026-09-02** - OpenModulePlatform counted at 8 test projects (the banner had said seven). Pins
  re-measured: 18.9.0 in all eight .NET repos (the earlier "18.8.1/18.9.0" split was gone),
  runner 4.0.0 everywhere. `ci.yml` derives its build/test legs as a .NET version matrix via
  `scripts/omp/get-ci-version-matrix.ps1`.
- **2026-09-01** - ODVGateway lifted to `Microsoft.NET.Test.Sdk` 18.9.0.
- **2026-08-31** - Runner/core compatibility baseline decided: runner-only upgrade to
  `xunit.runner.visualstudio` 4.0.0 on `xunit` 2.9.3, `Xunit.SkippableFact` 1.5.85,
  `Microsoft.Playwright` 1.62.0 (rationale in section 2.1).
- **2026-08-27** - VajSkrivare adopted CPM (`Directory.Packages.props`, Test.Sdk 18.9.0, runner
  3.1.5 at the time) and added `Skrivarkoppling.Web.UiTests`; no testless repos left. ODVGateway
  became the only .NET repo without CPM. First status banner added to this document.
- **2026-08-22** - Every repo has automated tests: `LogSearch.Tests` + `LogSearch.UiTests`,
  `tests/OpenModulePlatform.Web.eArkivDokumentbibliotek.Tests` + `...UiTests`,
  `tests/ODVGateway.Tests`. OpenDocViewer's CI runs `npm test`.
- **2026-08-19** - The family standardized on two-tier testing: xUnit unit tests plus xUnit +
  Microsoft.Playwright UI tests (`*.UiTests`), with shared tooling in
  `OpenModulePlatform/tests/shared/`.
- **2026-08** - OpenModulePlatform `ci.yml` provisions LocalDB and runs `dotnet test` with a
  `--filter` whose exclusions are registered in `docs/TEST_DEBT.md`.
- **2026-07-15** - Original audit (source code only; `bin/`, `obj/`, `artifacts/`, `node_modules/`,
  `dist/` excluded). Findings at the time: OpenModulePlatform had 3 test projects (~270 methods)
  plus two Pester files; LogSearch, Dokumentbibliotek and ODVGateway had no tests; family pins were
  `Microsoft.NET.Test.Sdk` 18.7.0, `xunit.runner.visualstudio` 3.1.5, `Xunit.SkippableFact`
  1.4.13; VajSkrivare pinned inline (no CPM) at Test.Sdk 17.14.0 / runner 2.8.2; only AgentDocMap
  ran tests in GitHub CI; iKrock2's `local-ci.ps1` carried a stale Swedish TODO instead of
  `dotnet test`. The audit also established the standard in section 2, which has held.
