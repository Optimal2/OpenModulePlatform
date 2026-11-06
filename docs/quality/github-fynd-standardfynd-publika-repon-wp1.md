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
| 13 | OpenModulePlatform.Installation/InstallationEngine.CoreMaintenance.cs | 98 | Fixed | Explicit `current is null \|\|` first disjunct instead of `current?.DefinitionVersion != ...`; recovery extracted the unchanged condition into the internal pure `ValidateAppliedCoreDefinition`, called by `VerifyCoreMaintenanceAsync` |
| 14 | OpenModulePlatform.Portal.Tests/Services/OmpAdminRepositoryConfigOverlayImportTests.cs | 141; remaining site at 142 | Fixed | The original rewrite used `export!.Value.HostScope` but left `export.Value.Json`; recovery keeps `Assert.NotNull(export)` and unwraps once into the nonnullable local `exportedOverlay = export.GetValueOrDefault()` for both assertions |
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
  threw in (`null`, version mismatch, hash mismatch). The extracted validator
  preserves the exact `InvalidOperationException` message and ordinal,
  case-insensitive hash comparison.
- `(int?)Input.ConfigId` uses the existing lifted implicit conversion and
  boxes the same value the `?.Value` chain produced.
- The original test-only `!` additions sit directly behind `Assert.NotNull`
  and match the surrounding test style. xUnit v2 `NotNull` returns void, but
  `!` is not the only possible narrowing: an explicit nonnullable local or a
  pattern can also make the guard visible. The remaining export site now uses
  a single `GetValueOrDefault()` local after its unchanged assertion. Working
  nullable assertions elsewhere were not churned for cosmetics.

## Measured scanner state

The original code-level dispositions above are not proof of scanner closure.
The conductor's independent 2026-11-06 GitHub sweep of published `b6cc23d8`
measured the OMP standard-query total falling from 144 to 127 and
`cs/dereferenced-value-may-be-null` falling from 18 to **one**, not zero. The
remaining location was `OmpAdminRepositoryConfigOverlayImportTests.cs:142`,
`Assert.Contains("appsettings.json", export.Value.Json, StringComparison.Ordinal)`.
That is the site recovered above. Final CodeQL closure still requires a fresh
post-recovery conductor rescan; no zero-finding claim is made here.

## Original WP1 validation (recorded, not a behavioral RED/GREEN proof)

- `dotnet build OpenModulePlatform.slnx --nologo -v minimal` — exit 0,
  0 errors and 11 warnings in untouched files.
- `dotnet test OpenModulePlatform.Portal.Tests --no-build --filter "UserSettingBitMigrationTests|OmpAdminRepositoryConfigOverlayImportTests|SystemLogCalendarInputTests|ConfigOverlaysPageTests"` — exit 0, 78/78 passed.
- `dotnet test OpenModulePlatform.Bootstrapper.Tests --no-build --filter "PlatformCoreMaintenanceTests"` — exit 0, 52/52 passed.
- `dotnet test OpenModulePlatform.Installation.Tests --no-build` — exit 0,
  30/30 passed.
- `git diff --check` — exit 0.

An open scanner count was never a failing runtime test and is not labelled
RED. The recovery below supplies actual behavioral RED/GREEN evidence for the
post-apply guard.

## Recovery validation (exact commands, native exit codes)

Initial and restored GREEN:

- `dotnet test OpenModulePlatform.Installation.Tests/OpenModulePlatform.Installation.Tests.csproj --filter "FullyQualifiedName~CoreMaintenanceVerificationTests" --nologo -v minimal` — exit 0, 5/5 passed before mutation.
- `dotnet test OpenModulePlatform.Installation.Tests/OpenModulePlatform.Installation.Tests.csproj --no-restore --filter "FullyQualifiedName~CoreMaintenanceVerificationTests" --nologo -v minimal` — exit 0, 5/5 passed after exact source restoration.
- `dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --filter "FullyQualifiedName~OmpAdminRepositoryConfigOverlayImportTests" --nologo -v minimal` — exit 0, 6/6 passed using only its normal isolated test fixture.
- `dotnet test OpenModulePlatform.Installation.Tests/OpenModulePlatform.Installation.Tests.csproj --no-build --nologo -v minimal` — exit 0, 35/35 passed.
- `dotnet test OpenModulePlatform.Bootstrapper.Tests/OpenModulePlatform.Bootstrapper.Tests.csproj --filter "FullyQualifiedName~PlatformCoreMaintenanceTests" --nologo -v minimal` — exit 0, 52/52 passed.
- `dotnet build OpenModulePlatform.slnx --nologo -v minimal` — exit 0, 0 errors and 6 warnings, all in untouched HostAgent Sentinel/HostAgent.Runtime.Tests files.
- `dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --no-build --filter "UserSettingBitMigrationTests|OmpAdminRepositoryConfigOverlayImportTests|SystemLogCalendarInputTests|ConfigOverlaysPageTests" --nologo -v minimal` — exit 0, 78/78 passed.
- `git diff --check` — exit 0.

Causal mutation checks temporarily removed one guard at a time from the real
production validator. Each command built successfully and then failed its
corresponding exact test with native exit 1 (1/1 failed, 0 passed):

- Null guard removed; filter `ValidateAppliedCoreDefinition_MissingCurrent_ThrowsExactVerificationError`:
  `Assert.Throws() Failure: Exception type was not an exact match`; expected
  `InvalidOperationException`, actual `NullReferenceException`.
- Version guard removed; filter `ValidateAppliedCoreDefinition_DifferentVersion_ThrowsExactVerificationError`:
  `Assert.Throws() Failure: No exception was thrown`; expected
  `InvalidOperationException`.
- Hash guard removed; filter `ValidateAppliedCoreDefinition_DifferentHash_ThrowsExactVerificationError`:
  `Assert.Throws() Failure: No exception was thrown`; expected
  `InvalidOperationException`.

Every filter above was passed as
`--filter "FullyQualifiedName~CoreMaintenanceVerificationTests.<method>"` to
`dotnet test OpenModulePlatform.Installation.Tests/OpenModulePlatform.Installation.Tests.csproj --no-restore --nologo -v minimal`.
The production file SHA-256 was
`A4EAF5FBC27D285CD0ED557E01A09313B6B31F4E48144F6F516FC7A961694CA3`
before the first mutation and after the final restoration. No mutation was
committed.

## Test collateral and limits

`CoreMaintenanceVerificationTests.cs` directly covers the existing post-apply
condition: missing current definition, different version, different hash,
matching identity, and case-insensitively matching hash. The negative tests
require the exact exception type and message. This is separate from
pre-transition validation.

`VerifyCoreMaintenanceAsync` was inspected at its real call site: it queries
`omp_core`, calls `ValidateAppliedCoreDefinition(definition, current)` before
schema and SQL-history checks, and retains the guarded `current!` use for the
later SQL-history query. Pure-validator tests cannot simulate SQL I/O or a
concurrent database race, and do not dynamically prove that caller wiring.
`PlatformCoreMaintenanceTests` exercise adjacent payload, transition and repair
operations; they do not exercise this exact post-apply current-null/version/hash
condition. They and the full Installation suite are regression collateral, not
substitutes for the focused tests.

## Confidence

High for the preserved guard contract because each guard was independently
mutated, produced its genuine failed assertion, was byte-restored, and passed
again. High for the remaining site's code-level disposition because the null
assertion and failure behavior remain unchanged. Scanner closure is unclaimed
until the conductor's fresh post-recovery rescan.
