# WP3: OpenModulePlatform constant-condition findings

Campaign: `github-fynd-standardfynd-publika-repon`
Owned rule: `cs/constant-condition`
Scanned source base: `3d5feaff6ad44419c043316c6b83bbf0925e5c52`
Initial worktree HEAD: `b6cc23d80d6f3d721c6ce21e15138bffd60fced8`

## Before count and measurement

The supplied GitHub Standard findings snapshot contains **3** owned findings: two in
`OpenModulePlatform.Portal/Pages/Admin/Users/Edit.cshtml.cs` and one in
`OpenModulePlatform.Installer/Ui/MainForm.cs`.

A local reproduction was attempted before editing by running this command from the
local CodeQL tool directory (outside the public repository):

```text
python codeql_lokal.py "<worktree>" --language csharp cs/constant-condition
```

Native exit code: **2**. The pinned CodeQL CLI is not installed, so this is an
unmeasured local result, not zero findings. No CLI was downloaded. The decisions below
come from direct source review and behavioral tests. Post-fix GitHub rule closure remains
subject to a later scan; no finding was dismissed.

## Finding decisions

### Portal nullable-bit switch arms (2 findings)

- Scanned locations: `OpenModulePlatform.Portal/Pages/Admin/Users/Edit.cshtml.cs:584`
  and `:585` (the `false` and `null` switch arms).
- Hypothesis review: the nullable Boolean mapping was functionally correct. The constant
  switch arms were a maintainability warning, not a runtime defect.
- Disposition: **fixed by a clear behavior-preserving rewrite**, without suppression.
  `BitText` now uses an explicit null guard and a Boolean conditional at current lines
  580-587. The public page presentation still maps `true`, `false`, and `null` to `true`,
  `false`, and an empty value-cell string respectively.
- Regression coverage:
  `OpenModulePlatform.Portal.Tests/Pages/UserEditBitTextTests.cs` exercises all three
  states through the public `PortalSettingValueText` method.
- RED/GREEN: no failing functional RED was manufactured because source review found no
  behavioral defect. The characterization test was GREEN before the rewrite (3/3) and
  remained GREEN afterward (3/3).

### Installer redundant evaluation null check (1 finding)

- Scanned location: `OpenModulePlatform.Installer/Ui/MainForm.cs:345`.
- Hypothesis review: `_evaluation is not null` was always true after the non-null service
  account guard. Password validation and install blocking were otherwise correct.
- Disposition: **fixed by a clear behavior-preserving rewrite**, without suppression.
  `OnVerifyPassword` now snapshots `_evaluation`, guards that snapshot once, and passes
  the non-null evaluation to `CanInstallAfterPasswordValidation` at current lines 325-353.
- Regression coverage: `OpenModulePlatform.Installer.Tests/MainFormTests.cs` verifies
  that an unvalidated password blocks installation, a validated password permits
  auto-fixable and service-account checks, and a validated password never bypasses an
  unrelated blocking check. The existing prerequisite tests continue to cover the
  underlying evaluation contract.
- RED/GREEN: no failing functional RED was manufactured because source review found no
  behavioral defect. The 17 existing characterization tests were GREEN before the
  rewrite; those 17 plus the 4 focused install-gating cases were GREEN afterward (21/21).

## Exact behavioral validation

All commands ran sequentially from the worktree root. Native exit code was **0** for each
command below, and every accepted run printed its test count.

Before the production rewrite:

```text
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --filter FullyQualifiedName~UserEditBitTextTests --logger "console;verbosity=normal" --verbosity minimal
```

Result: **3 passed, 0 failed** (exit 0).

```text
dotnet test OpenModulePlatform.Installer.Tests/OpenModulePlatform.Installer.Tests.csproj --filter FullyQualifiedName~PrerequisiteEvaluatorTests --logger "console;verbosity=normal" --verbosity minimal
```

Result: **17 passed, 0 failed** (exit 0).

After the production rewrite:

```text
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --filter FullyQualifiedName~UserEditBitTextTests --no-restore --logger "console;verbosity=normal" --verbosity minimal
```

Result: **3 passed, 0 failed** (exit 0).

```text
dotnet test OpenModulePlatform.Installer.Tests/OpenModulePlatform.Installer.Tests.csproj --filter "FullyQualifiedName~MainFormTests|FullyQualifiedName~PrerequisiteEvaluatorTests" --no-restore --logger "console;verbosity=normal" --verbosity minimal
```

Result: **21 passed, 0 failed** (exit 0).

Two earlier `--no-restore` attempts made before the first restore returned no test
summary and are not counted as validation. A direct build then exposed the missing
`project.assets.json` (`NETSDK1004`, exit 1); the accepted before-tests above were rerun
with restore and produced real test counts.

## Scope

Production changes are limited to the two owned finding files. The two new test files are
explicit related regressions. The Portal component and repository version are prepared
through the canonical version script during guarded integration. The installer has no
separate component key; its package identity follows `repositoryVersion`.
