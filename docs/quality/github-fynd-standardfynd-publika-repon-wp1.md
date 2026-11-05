# WP1 — cs/dereferenced-value-may-be-null (OpenModulePlatform)

Campaign: github-fynd-standardfynd-publika-repon. Owner of this rule only; sibling
rules (undisposed objects, remaining Warnings, Notes) belong to WP2/WP3/WP4/WP5.
Source scan: 2026-11-05 weekly sweep, base `3d5feaff`, 18 open findings, all
Reliability/Warning. Line numbers below are the scanned lines; the worktree was
at the base commit, so they match except where noted.

## Before

18 open findings, 0 fixed, 0 dismissed. Every finding was treated as a hypothesis
and checked against the code. All 18 turned out to be guarded-but-opaque
dereferences: the null check exists, but in a shape the query does not see
through (`Nullable<T>.Value` after `HasValue`, `?.Value` chains, or
`Assert.NotNull` in xUnit v2, which returns void and therefore cannot carry
narrowing). No finding described a reachable `NullReferenceException`.

## Decisions

| # | File | Scanned line | Disposition | Fix |
|---|------|---:|---|---|
| 1 | OpenModulePlatform.Portal.Tests/Services/UserSettingBitMigrationTests.cs | 50 | Fixed | `definition!.Value.ValueKind` after the existing `Assert.NotNull` |
| 2 | OpenModulePlatform.Portal.Tests/Services/UserSettingBitMigrationTests.cs | 57 | Fixed | `deep!.Value.ValueKind` after the existing `Assert.NotNull` |
| 3 | OpenModulePlatform.Portal.Tests/Services/UserSettingBitMigrationTests.cs | 62 | Fixed | `theme!.Value.ValueKind` after the existing `Assert.NotNull` |
| 4 | OpenModulePlatform.Portal.Tests/Services/UserSettingBitMigrationTests.cs | 67 | Fixed | `culture!.Value.ValueKind` after the existing `Assert.NotNull` |
| 5 | OpenModulePlatform.Portal.Tests/Services/UserSettingBitMigrationTests.cs | 97 | Fixed | `ctrlF!.Value.Value` after the existing `Assert.NotNull` |
| 6 | OpenModulePlatform.Portal.Tests/Services/UserSettingBitMigrationTests.cs | 101 | Fixed | `hover!.Value.Value` after the existing `Assert.NotNull` |
| 7 | OpenModulePlatform.Portal.Tests/Services/UserSettingBitMigrationTests.cs | 107 | Fixed | `definition!.Value.DefaultBit` (line 106 already used `!` and was not flagged, which confirms the query respects it) |
| 8 | OpenModulePlatform.Portal/Pages/Admin/ConfigOverlays.cshtml.cs | 63 | Fixed | `if (id is int overlayId)` pattern; `overlayId` replaces both `id.Value` uses |
| 9 | OpenModulePlatform.Portal/Pages/Admin/ConfigOverlays.cshtml.cs | 83 | Fixed | `id is int previewOverlayId`, `ArtifactId is int previewArtifactId`, `requestedHostKey is string previewHost` plus the unchanged `IsNullOrWhiteSpace` check; `previewHost`/`previewArtifactId` replace the old arguments |
| 10 | OpenModulePlatform.Portal/Pages/Admin/ConfigOverlays.cshtml.cs | 180 | Fixed | `if (Draft.Id is int uploadDraftId)` in `OnPostUploadFileAsync`; `uploadDraftId` replaces both `Draft.Id.Value` uses |
| 11 | OpenModulePlatform.Portal/Pages/Admin/ConfigOverlays.cshtml.cs | 108 | Fixed | `if (Draft.Id is int saveDraftId)` in `OnPostSaveAsync`; `saveDraftId` replaces both `Draft.Id.Value` uses |
| 12 | OpenModulePlatform.Portal/Pages/Admin/ConfigOverlays.cshtml.cs | 91 | Fixed | `if (id is int impactOverlayId && ...)`; `impactOverlayId` replaces both `id.Value` uses |
| 13 | OpenModulePlatform.Installation/InstallationEngine.CoreMaintenance.cs | 98 (now 99) | Fixed | Explicit `current is null \|\|` first disjunct instead of `current?.DefinitionVersion != ...`; the `||` short-circuit truth table is unchanged |
| 14 | OpenModulePlatform.Portal.Tests/Services/OmpAdminRepositoryConfigOverlayImportTests.cs | 141 | Fixed | `export!.Value.HostScope` after the existing `Assert.NotNull` |
| 15 | OpenModulePlatform.Portal.Tests/Pages/SystemLogCalendarInputTests.cs | 108 | Fixed | `model.To!.Value.Date - model.From!.Value.Date`, matching the `!` style already used on lines 106-107 |
| 16 | OpenModulePlatform.Portal.Tests/Pages/SystemLogCalendarInputTests.cs | 108 | Fixed | Same line, second dereference (two findings shared one line) |
| 17 | examples/WorkerAppModule/WebApp/Pages/AppInstances/Edit.cshtml.cs | 88 | Fixed | `(int?)Input.ConfigId` lifted conversion instead of `Input.ConfigId?.Value`; identical boxed value (`null` stays `null`) without any member dereference |
| 18 | examples/ServiceAppModule/WebApp/Pages/AppInstances/Edit.cshtml.cs | 88 | Fixed | Same rewrite as 17 |

Nothing was dismissed on GitHub and nothing was suppressed with pragmas or
attributes. Externally visible behavior is unchanged in every case:

- The `is int` / `is string` patterns bind exactly when the old `HasValue`
  guard passed (`null` fails the pattern; `IsNullOrWhiteSpace` still rejects
  empty/whitespace hosts), so the same requests reach the same store calls.
- `current is null ||` throws in exactly the states the old `?.`-comparison
  threw in (`null`, version mismatch, hash mismatch).
- `(int?)Input.ConfigId` uses the existing lifted implicit conversion and
  boxes the same value the `?.Value` chain produced.
- Test-only `!` additions sit directly behind the `Assert.NotNull` that
  already guards the value; with xUnit v2 `NotNull` returns void, so `!` is
  the only available narrowing and matches the surrounding test style.

## Validation (exact commands, native exit codes)

- `dotnet build OpenModulePlatform.slnx --nologo -v minimal` — exit 0,
  0 errors. The 11 remaining warnings are all in untouched files
  (HostAgent Sentinel, DeploymentLockFileTests, OrphanServiceAppFinderTests,
  DpapiNg call sites, OmpAuthRepository, UniversalPackageSeedSqlOrderingTests);
  none of the 7 edited files produces a warning.
- `dotnet test OpenModulePlatform.Portal.Tests --no-build --filter "UserSettingBitMigrationTests|OmpAdminRepositoryConfigOverlayImportTests|SystemLogCalendarInputTests|ConfigOverlaysPageTests"` — exit 0, 78/78 passed.
  `ConfigOverlaysPageTests` is outside this work package and was run
  read-only as regression cover for the page rewrite, not edited.
- `dotnet test OpenModulePlatform.Bootstrapper.Tests --no-build --filter "PlatformCoreMaintenanceTests"` — exit 0, 52/52 passed.
  Read-only run as regression cover for the `VerifyCoreMaintenanceAsync`
  rewrite; the file itself belongs to a later package (Note rules) and was
  not touched.
- `dotnet test OpenModulePlatform.Installation.Tests --no-build` — exit 0,
  30/30 passed.
- `git diff --check` — exit 0, no whitespace errors.
- RED/GREEN note: RED here is the scanner state (18 open findings at base);
  all 18 sites were safe under existing guards, so there was no failing
  runtime behavior to reproduce first. GREEN is the build plus the 160 tests
  above on the rewritten code. The query itself cannot be re-run locally (no
  CodeQL CLI on the workstation); the next browser sweep is expected to show
  0 open `cs/dereferenced-value-may-be-null` findings.

## Test collateral

No new test was added. The rewrites are behavior-identical, so the existing
suites are the regression collateral: the four Portal test classes cover the
edited test files and the overlay page, `PlatformCoreMaintenanceTests` covers
core-maintenance behavior end to end, and `Installation.Tests` covers the
edited assembly. Adding a dedicated test for an unchanged truth table would
be change without value.

## Confidence

High for the defect verdicts (each flagged expression was read with its
guard; the only uncertainty was which sub-expression the query flagged on
lines carrying several, and every candidate on those lines was rewritten).
Medium that the next sweep shows exactly zero: the rewrites remove every
nullable dereference shape on the flagged lines, but rescan timing and rule
revisions are outside this worktree. If any finding survives, it will be a
new rule interpretation, not an unaddressed line.
