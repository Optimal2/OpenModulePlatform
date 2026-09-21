# Test debt register

This register tracks tests and test areas that are intentionally excluded from
the CI gates, why they are excluded, and what must happen before they can be
re-enabled. It exists so that exclusions are visible, owned, and temporary
rather than silent.

**Sync rule (mandatory):** any change to the `dotnet test --filter` in
`.github/workflows/ci.yml`, or any other change that excludes, skips, or
disables tests from a gate, must update this register in the same commit.
An exclusion that is not registered here is a bug.

| Test/area | Gate status | Why excluded | Re-enable criteria | Owner | Since |
| --- | --- | --- | --- | --- | --- |
| `Portal.Tests.Integration` | Excluded from the `dotnet test` gate in `ci.yml`; runs unfiltered in the local gate (`scripts/local-ci.ps1` tests `OpenModulePlatform.Portal.Tests` without a filter) | Registered in 2026-07 as needing a bootstrap-seeded OMP database. That no longer holds: `PushEventPipelineTestFixture` provisions its own per-process database (`omp.users`, `omp.push_event_outbox`, one test user) through `OmpTestDatabaseProvisioner`, and every class in the namespace shares that fixture through `PushEventPipelineTestCollection`. Re-run 2026-09-21 on SQL Server 2025 (localhost, no `OMP_TEST_CONNECTION_STRING`): 19/19 passed in 7 s with no seeded database present. | The self-provisioning criterion is met. Remaining step: remove `FullyQualifiedName~Portal.Tests.Integration` from both filters in `ci.yml` (and the "Excluded:" echo) in a commit whose CI run is observed green on the hosted LocalDB; the fixture has not yet been exercised on a hosted runner, which is the only thing this register cannot confirm locally. | Repository maintainers | 2026-07 (tests-actually-gate-merges); re-measured 2026-09-21 |
| `Portal.Tests.Services.OmpHostArtifactRepositoryHostDeploymentLeaseTests` | Excluded from the `dotnet test` gate in `ci.yml`; runs in the local gate | Still depends on a bootstrap-seeded database: the class connects to `Server=(local);Database=OpenModulePlatform` (or `OMP_TEST_CONNECTION_STRING`) and claims rows in `omp.HostDeployments` there, so it cannot self-provision on a fresh CI LocalDB. Additionally fragile on SQL Server LocalDB timing in hosted CI runners. Re-run 2026-09-21 against the local developer database: 6/6 passed. | CI-provisioned seeded database or fixture self-provisioning (a per-process database with the `omp.HostDeployments` schema, as the Portal integration fixture now does), plus demonstrated stability on hosted runners. | Repository maintainers | 2026-07 (tests-actually-gate-merges); re-measured 2026-09-21 |
| `OpenModulePlatform.UiTests` (`Category=Ui`) | Excluded from the `dotnet test` gates in `ci.yml` (`Category!=Ui` in the filter) and in the pre-push gate (`scripts/local-ci.ps1` runs an explicit project list that does not include the UiTests project; no filter) | By design per the test standard: the Playwright invariant scans and CSP smoke tests download Chromium and boot the built Portal, Auth and iFrame example module apps against a seeded OMP database, which does not belong in the fast push gate. Runnable locally via `dotnet test --filter "Category=Ui"`; the tests skip with a reason when a prerequisite is missing. | Intentional standing exclusion, not debt in itself; reconsider only if CI gains a browser-capable job with a seeded database. | Repository maintainers | 2026-08 |

All excluded areas remain runnable locally against a provisioned
`OpenModulePlatform` database (see `sql/README.md` and
`docs/CODEX_DEVELOPMENT.md` for the local validation ladder).

## Deviations from the test standard

**DB-backed tests fail instead of skipping without SQL Server (registered
2026-08-19).** The test standard requires environment-dependent tests to use
`Xunit.SkippableFact` and skip with a reason so the suite is green on a
minimal machine. The existing database-backed xUnit tests in this repository
predate that rule: they use plain `[Fact]`/`[Theory]` and fail (rather than
skip) when no SQL Server is reachable. The `Xunit.SkippableFact` package is
already referenced centrally and the UI suite follows the rule. Remediation
criterion: migrate each DB-backed test class to `SkippableFact`/
`SkippableTheory` with an availability probe the next time that class is
touched — no big-bang migration.

## Resolved

**CI flakiness from concurrent `CREATE DATABASE` — fixed 2026-08-14.** Runs failed
intermittently with "Could not obtain exclusive lock on database 'model'" and a wall of
`Execution Timeout Expired`, always in fixture constructors and never in a test body. The
gate at the time ran four test assemblies in parallel (the solution has eight test projects today, all run by one `dotnet test` on the solution in CI), xUnit runs collections within each in parallel,
and seven fixtures each issued `CREATE DATABASE` against the same LocalDB instance —
which copies `model` under an exclusive lock. The queue outlasted the 30-second default
command timeout.

All seven were moved to provision through `tests/shared/OmpTestDatabaseProvisioner.cs` —
and every DB fixture added since uses it as well — which holds a
machine-wide mutex for the creation only, allows 180 seconds for it, and retries the two
transient error numbers. Nothing was excluded or skipped to make this pass.
