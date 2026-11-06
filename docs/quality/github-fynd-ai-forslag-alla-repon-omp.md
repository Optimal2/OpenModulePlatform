# GitHub AI findings in OpenModulePlatform: every disposition

Campaign: `github-fynd-ai-forslag-alla-repon`. Repository: OpenModulePlatform
(public). Source of the findings: the weekly sweep of 2026-11-06
(`2026-11-06-github-fynd-ai-forslag-alla-repon-wp0.md`), read against the
scanned commit `9a88513babab1019382d744ee8c5dd4c82aa3b84`. That sweep listed
9 open AI findings in 5 files for this repository. The earlier 2026-11-05
prep sweep listed `OpenModulePlatform.Portal/Pages/Index.cshtml` in place of
`OpenModulePlatform.Installer/Ui/MainForm.cs`; this report follows the newer
2026-11-06 sweep quoted above.

Original artifact baseline: `1cc8a623` (`repositoryVersion` 0.3.1111). The
preserved commits were recovered onto mainline base `d62b2f18`, whose two
newer files do not overlap this package.

Every finding below has exactly one disposition and one reason. A finding is
only called fixed when a native test in this repository proves the new
behaviour; a finding that was measured and found not to apply is rejected with
the measurement that rejects it. No finding is dismissed silently.

## The nine open AI findings

| # | File | Finding | Disposition | Reason |
| --- | --- | --- | --- | --- |
| 1 | `OpenModulePlatform.Bootstrapper/NoOverwriteFile.cs` | The identity check and the overwrite move are separate pathname operations, so a foreign file can be overwritten between `IsOwnClaim` and `File.Move(..., overwrite: true)`. | Fixed | The publisher releases only its own locked, identity-checked empty claim and then publishes with `File.Move(temp, target, overwrite: false)`, so a foreign file at the target path is never deleted or replaced; the write fails with `OutputFileExistsException` instead. |
| 2 | `OpenModulePlatform.Bootstrapper/NoOverwriteFile.cs` | `GetFileInformationByHandle` is imported from `kernel32.dll`, so a claim reaches a Windows-only native call and throws `DllNotFoundException` on Unix-like systems, leaving an empty reservation behind. | Fixed | `NoOverwriteFile.Write` now calls `EnsureSupportedPlatform(OperatingSystem.IsWindows())` before the output directory and the claim are created, so a non-Windows run is refused with `PlatformNotSupportedException` and leaves no empty reservation. |
| 3 | `OpenModulePlatform.Installer/Ui/MainForm.cs` | A successful password verification stays valid after the textbox is edited, so an unverified replacement password can be installed while the Install button remains enabled. | Fixed | `AccountValidationState` invalidates the password validation on every text change and on every prerequisite refresh, `CanInstall` is recomputed from the current evaluation, and `RunInstallAsync` returns early unless that gate is open. |
| 4 | `OpenModulePlatform.Portal.Tests/Services/UniversalPackageSeedSqlOrderingTests.Completion.cs` | `sys.dm_tran_locks.resource_subtype` can be `NULL` for application locks, so `GetString(2)` would throw and the waiter comparison would match no row. | Rejected (measured) | On SQL Server 2025, on the development instance and on the LocalDB instance the CI gate uses, the DMV declares `resource_subtype nvarchar(60) NOT NULL`, no row carries a null, and a session-owned application lock holds the empty string, so the read and the parameter binding are correct as they stand. |
| 5 | `OpenModulePlatform.Portal/wwwroot/css/site.css` | The collapsed-card rules are duplicated and the later duplicate overrides the earlier `font-size: 1rem` with `1.05rem`. | Rejected (measured) | The committed headless-Chromium fixture measures the real cascade in all three theme palettes and proves the rendered collapsed size is the intended 1.05 scale in every palette, so consolidating the two blocks would be a CSS-only edit with no measured behaviour change; the fixture now fails if a later edit changes the rendered value. |
| 6 | `OpenModulePlatform.Portal/wwwroot/css/site.css` | The same duplicated collapsed-card rules, reported a second time. | Rejected (measured) | Same measurement and same reason as finding 5; the two duplicates are one defect, and the fixture pins the rendered value that both duplicates produce. |
| 7 | `OpenModulePlatform.Portal/wwwroot/css/site.css` | `--surface-alt` is not defined in the root token aliases, so the whole background declaration is invalid and the tab strip is transparent. | Rejected (measured) | The same fixture measures that `--surface-alt` stays unresolved and that both admin tab surfaces render `rgba(0, 0, 0, 0)` in all three palettes, so the transparent strip is the current rendering; defining an alias would change the rendered UI, which is a design decision and not a findings fix. |
| 8 | `OpenModulePlatform.Portal/wwwroot/css/site.css` | `--surface-alt` is not defined among the portal aliases, so the background declaration is invalid unless another stylesheet provides the variable. | Rejected (measured) | Same measurement and same reason as finding 7; the fixture reads the variable from the computed style of the page in all three palettes, so a variable provided by another stylesheet would have been seen. |
| 9 | `OpenModulePlatform.Portal/wwwroot/js/portal-dashboard.js` | Restoring a snapshot does not rebind notification widgets, so the cloned feed keeps the copied binding marker and its forms and infinite-scroll handler stop working. | Fixed | The rebuild clears the copied `data-dashboard-notification-feed-bound`, `data-dashboard-notification-form-bound`, `data-dashboard-notification-submitting` and `data-notification-loading` markers and calls `bindDashboardNotifications(widget)` for every rebuilt widget, so a restored feed carries exactly one live submit, click and scroll handler. |

Files 4 and 5 to 8 were not changed. Finding 4 lives in a file whose owner is
another campaign's terminal change; the contract it questions is pinned
instead in a new, separate test file,
`OpenModulePlatform.Portal.Tests/Services/UniversalPackageSeedSqlOrderingTests.ResourceSubtype.cs`,
which holds a real session application lock on the isolated fixture database,
reads the real DMV row, proves the column is not null, and proves that the
measured value counts the real row while a null parameter counts nothing.
Findings 5 to 8 live in a stylesheet, and the decision to leave the cascade
alone is backed by a measurement, not by taste.

## Other hypotheses carried by this package

| Hypothesis | Disposition | Reason |
| --- | --- | --- |
| This build's own released name reservation can keep the target name visible, be read as a foreign file and bypass the bounded retry. | Fixed | Measured on Windows: the released claim stays delete-pending while a reader holds it, so `File.Exists` is true and the publish reported a replacement that never happened. Publication waits inside the bounded window, while a supported handle that identifies a different file proves foreign replacement; an ambiguous open or sharing failure is never called proof of own ownership. |
| A numeric version core above the supported range can be written, including as the result of an automatic bump, and the platform then orders it wrongly. | Fixed | The platform compares versions with `System.Version`, whose core parts are `Int32`, so the helper now validates every selected existing, input and result core against 0..2147483647 before any write and reports a supported-range error instead of overflowing. |
| The version helper has two independent confirmation prompts for one write, so a decline in the inner prompt leaves the repository half bumped. | Fixed | The write phase is plan, confirm, apply with one confirmation boundary per real write; a declined confirmation writes nothing and is reported as a decline. |
| A selected component whose module definition file is missing, or a selected widget whose package file is missing, leaves earlier selected files half bumped. | Fixed | Every selected path is loaded and validated before the first write, so a missing or invalid selected file fails before any mutation and leaves the manifest, the definitions and the widget packages unchanged. |
| The rejected proposal's lost-mainline implementation was a second, parallel edit of the same six paths. | Rejected | The three-way comparison against the fresh base is empty for all six recovered paths on both the published candidate and the current tree, so the differences are expected unpublished branch differences and not a parallel mainline edit. |
| The rejected proposal's version-suite driver shipped a prefix filter that would have excluded the SQL-embedding guard from the gate. | Not shipped | The shipped driver runs every `Bump-Version*.Tests.ps1` suite with no prefix filter and no exclusion, and a suite that is found but did not run fails the gate instead of quietly reducing the coverage. |
| The rejected proposal's version-suite comments describe a child `powershell.exe` and an ambient `$LASTEXITCODE` that the code no longer uses. | Corrected | The helper runs in an isolated runspace that owns the fake host, and the comments now describe what the code does. |
| The rejected proposal's historical restoration receipt can be live-checked after the orchestrator cleanup. | No code fix | The historical receipt cannot be verified live after the cleanup, so no universal temporal proof is claimed; the reviewer tree is preserved untouched and no accepted-review claim is made. |
| `Get-WidgetFileEntryVersion` writes a new `widgetVersion` property although `packageVersion` or `version` may be the field the consumer reads. | Rejected | The same helper reads the version as `widgetVersion`, then `packageVersion`, then `version`; the package reader falls back the same way, and the root artifact stamps `packageVersion` and the widget entry stamps `widgetVersion`, so the written value is the value the reader uses. The finding was reported against another repository's copy of the shared helper. |
| A widget package is validated only after the selected module definitions have already been written. | Stale (regression kept) | The shipped helper loads and validates the whole selection before the first write, so the reported ordering does not exist here; the regression is kept as a test so that a later change cannot reintroduce it. |
| The Razor compiler directory can be replaced by the compiler DLL name that is combined with it. | Comment only | The second operand of each `Path.Combine` is a fixed relative DLL name, so the combined path stays inside the compiler directory; the reason is now written next to the calls. This is not a newly claimed runtime defect. |

## Recovery review dispositions

| Finding | Disposition | Native evidence and rule |
| --- | --- | --- |
| F1: the source-check driver and CSS fixture had a machine-specific scratch fallback. | Fixed | With `AO_JOB_SCRATCH` absent, the actual initialization now resolves below the owned platform temp directory on Windows PowerShell 5.1 and PowerShell 7. The CSS fixture creates a unique owned directory, passes temp variables only to the browser child, mutates no process temp variable, and cleans the directory. The old probes resolved outside owned temp; the new probes pass without touching that external location, and the real 35-case Node driver passes. |
| F2: the initial `ownClaimPending` sample could become stale before publication. | Fixed | The old source held an own reader through the initial sample, replaced the released name with a foreign file in `beforePublish`, then spent the retry window and misreported its own reservation. The candidate positively detects a different identity through a supported handle, refuses with `OutputFileExistsException`, preserves foreign bytes and cleans its temporary file. A sharing-denied identity probe remains ambiguous and never produces the own-reservation diagnosis; the original bounded own-reader success still passes. |
| F3: the intermediate review launch named a nonexistent base. | Metadata correction only | The reviewed artifact was the real `1cc8a623..06ecd094` chain. Recovery used the verified original campaign base `9a88513b`, the actual artifact parent `1cc8a623`, and the non-overlapping mainline base `d62b2f18`; no production source change follows from the launch typo. |
| F4: exact-SHA CI was interpreted as a requirement to change action pins. | Rejected | Exact-SHA verification means each workflow run's `head_sha` equals the published commit. Unchanged action-tag configuration is outside this package, so no workflow file is edited. |

## Scopes this package does not own

The standard-finding campaign `github-fynd-standardfynd-publika-repon` owns the
LINQ `select` and `where` scopes, the Console-ownership corrections and the
native DTO layout rationale for this repository. Those receipts are reused
here and are not duplicated, re-argued or changed:
`docs/quality/github-fynd-standardfynd-publika-repon-final-integration.md`,
`docs/quality/github-fynd-standardfynd-publika-repon-wp2-console-rationale.md`,
`docs/quality/github-fynd-standardfynd-publika-repon-wp4-select.md` and
`docs/quality/github-fynd-standardfynd-publika-repon-wp4-where-rationale.md`.

## Limits of these dispositions

- The stylesheet dispositions rest on a headless-Chromium measurement of the
  shipped cascade in three palettes. They are not a design review and they are
  not a claim that the cascade could not be tidied.
- The `resource_subtype` disposition rests on SQL Server 2025 as measured here.
  A different engine version with a different DMV contract would make the new
  test fail loudly rather than silently, which is the intended behaviour.
- No scanner-clearance claim is made from source inspection or from tests.
  The browser finding counts and any dismissal stay with the conductor.
- No finding is claimed to be fixed in the GitHub surface; the fix is in this
  repository, and the next scan decides whether the surface agrees.
