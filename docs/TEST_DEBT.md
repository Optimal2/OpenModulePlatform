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
| `Portal.Tests.Integration` | Excluded from the `dotnet test` gate in `ci.yml` | Requires a bootstrap-seeded OMP database; `sql/2-initialize-openmoduleplatform.sql` refuses to run until the bootstrap-admin placeholder is replaced, so the tests cannot self-provision on a fresh CI LocalDB. | CI provisions a seeded OMP database (bootstrap-admin placeholder replaced) before the test step, or the test fixture self-provisions its own seeded schema. | Repository maintainers | 2026-07 (tests-actually-gate-merges) |
| `Portal.Tests.Services.OmpHostArtifactRepositoryHostDeploymentLeaseTests` | Excluded from the `dotnet test` gate in `ci.yml` | Same bootstrap-seeded-database dependency as the Portal integration tests, and additionally fragile on SQL Server LocalDB timing in hosted CI runners. | Same as above: CI-provisioned seeded database or fixture self-provisioning, plus demonstrated stability on hosted runners. | Repository maintainers | 2026-07 (tests-actually-gate-merges) |

Both excluded areas remain runnable locally against a provisioned
`OpenModulePlatform` database (see `sql/README.md` and
`docs/CODEX_DEVELOPMENT.md` for the local validation ladder).
