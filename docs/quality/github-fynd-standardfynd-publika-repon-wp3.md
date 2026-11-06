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

## Recovery causal preservation proofs

Recovery base: `e1216a119d6618c5d5d733b7f617a16f64794171`. The original observations
above remain **GREEN-before/GREEN-after characterization** of working code; they are not
retroactively relabeled as defect REDs. The following four temporary production mutations
are separate causal proofs that the focused tests protect the preserved contracts. Every
mutation was rebuilt by `dotnet test`, run sequentially, restored to the exact original
bytes, and never committed.

The unmodified baseline first passed `MainFormTests` **4/4** and `UserEditBitTextTests`
**3/3**, each with native exit **0**, using these commands:

```text
dotnet test OpenModulePlatform.Installer.Tests/OpenModulePlatform.Installer.Tests.csproj --filter FullyQualifiedName~MainFormTests --logger "console;verbosity=normal" --verbosity minimal
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --filter FullyQualifiedName~UserEditBitTextTests --logger "console;verbosity=normal" --verbosity minimal
```

### M1: password validation remains mandatory

Mutation: remove only `passwordValidated &&` from
`MainForm.CanInstallAfterPasswordValidation`.

```text
dotnet test OpenModulePlatform.Installer.Tests/OpenModulePlatform.Installer.Tests.csproj --filter FullyQualifiedName~MainFormTests.Password_validation_is_required_before_install --no-restore --logger "console;verbosity=normal" --verbosity minimal
```

RED receipt: native exit **1**, **1 failed / 1 passed**. The false-password theory case
failed at `MainFormTests.cs:19`: `Assert.Equal`, expected `False`, actual `True`. After the
exact restoration, the same command was GREEN: native exit **0**, **2/2 passed**.

### M2: a validated password does not bypass a blocking prerequisite

Mutation: add only `|| check.Id == PrerequisiteCheckId.SqlDatabase` to the failed-check
acceptance predicate, thereby accepting a blocking database check.

```text
dotnet test OpenModulePlatform.Installer.Tests/OpenModulePlatform.Installer.Tests.csproj --filter FullyQualifiedName~MainFormTests.Validated_password_does_not_bypass_a_blocking_check --no-restore --logger "console;verbosity=normal" --verbosity minimal
```

RED receipt: native exit **1**, **1/1 failed** at `MainFormTests.cs:42`: `Assert.False`,
expected `False`, actual `True`. After the exact restoration, the same command was GREEN:
native exit **0**, **1/1 passed**.

### M3: true keeps its lowercase display text

Mutation: change only the `BitText` true arm from `"true"` to `"True"`.

```text
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --filter FullyQualifiedName~UserEditBitTextTests.Bit_values_keep_their_invariant_display_text --no-restore --logger "console;verbosity=normal" --verbosity minimal
```

RED receipt: native exit **1**, **1 failed / 2 passed**. The true theory case failed at
`UserEditBitTextTests.cs:34`: `Assert.Equal`, expected `"true"`, actual `"True"`. After the
exact restoration, the same command was GREEN: native exit **0**, **3/3 passed**.

### M4: null remains distinct from false

Mutation: change only the first, live `BitText` null guard from `return null` to
`return "false"`. This is the output consumed by `PortalSettingValueText`, not a later dead
fallback.

```text
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --filter FullyQualifiedName~UserEditBitTextTests.Bit_values_keep_their_invariant_display_text --no-restore --logger "console;verbosity=normal" --verbosity minimal
```

RED receipt: native exit **1**, **1 failed / 2 passed**. The null theory case failed at
`UserEditBitTextTests.cs:34`: `Assert.Equal`, expected `""`, actual `"false"`. After the
exact restoration, the final class-level command below was GREEN: native exit **0**,
**3/3 passed**.

```text
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --filter FullyQualifiedName~UserEditBitTextTests --no-restore --logger "console;verbosity=normal" --verbosity minimal
```

The final restored installer rerun was also GREEN, native exit **0**, **4/4 passed**:

```text
dotnet test OpenModulePlatform.Installer.Tests/OpenModulePlatform.Installer.Tests.csproj --filter FullyQualifiedName~MainFormTests --no-restore --logger "console;verbosity=normal" --verbosity minimal
```

Original and restored SHA-256 values, checked after each applicable restoration:

```text
OpenModulePlatform.Installer/Ui/MainForm.cs
b2df9b774b661d561c7e7473acc252939605c589b1f4affd5ea9b74265494d53
OpenModulePlatform.Portal/Pages/Admin/Users/Edit.cshtml.cs
221ee1b2d42601997091a756bafc3eb547ecca288c8832acfe6fb956b10705a0
```

The real `OnVerifyPassword` still passes its Boolean result and snapshotted evaluation to
`CanInstallAfterPasswordValidation`; the real page value-cell path still calls
`PortalSettingValueText`, which calls `BitText`. The tests use only synthetic booleans and
fabricated prerequisite/setting objects. They do not instantiate or show a Windows Form,
call `AccountPasswordValidator.Validate` or Win32 `LogonUser`, accept sign-in inputs,
exercise GUI event dispatch, or perform database I/O. Consequently they prove the pure
gating and presentation contracts, not actual credential validation, WinForms behavior, or
database availability.

## F1: repository-default installer package identity

The accepted empirical identity probe was a genuine policy RED (native exit **1**), not a
build failure. Release builds of the Installation library at
`fccc8b92921cc8365e4231aa101a7c5eae60c63d` and
`e1216a119d6618c5d5d733b7f617a16f64794171` both exited **0**, but their DLL SHA-256 values
differed and PE metadata showed `ValidateAppliedCoreDefinition` absent in the former and
present in the latter. Both selected `repositoryVersion` **0.3.1109** and therefore the same
default identity, `OpenModulePlatformHostAgentFirst-0.3.1109.zip`. No zip, signing, publish,
import, installation, or runtime operation ran, and this receipt makes no claim about prior
artifact distribution.

Guarded integration preparation used the canonical
`scripts/omp/bump-version.ps1 -RepositoryOnly` path to raise only `repositoryVersion` from
**0.3.1109** to **0.3.1110**. Component versions, module-definition versions, minimums, and
compatible-artifact ranges remain unchanged. The production validator remains present in
`OpenModulePlatform.Installation/InstallationEngine.CoreMaintenance.cs`.

The actual packager precedence was inspected: with no `-Version` override and a blank
configuration value, `scripts/deployment/package-hostagent-first.ps1` selects
`omp-components.json:repositoryVersion`, then formats both the package directory and zip name
as `OpenModulePlatformHostAgentFirst-$Version`. No installer package was created for this
proof. The final manifest policy check exited **0** and printed the new, distinct identity:

```text
python -c "import json; from pathlib import Path; v=json.loads(Path('omp-components.json').read_text(encoding='utf-8-sig'))['repositoryVersion']; identity=f'OpenModulePlatformHostAgentFirst-{v}.zip'; assert v == '0.3.1110' and identity != 'OpenModulePlatformHostAgentFirst-0.3.1109.zip'; print(identity)"
OpenModulePlatformHostAgentFirst-0.3.1110.zip
```

## Scope

The original production changes are limited to the two owned finding files. The two new test
files are explicit related regressions. The Portal component and repository version were
prepared through the canonical version script during guarded integration. This recovery adds
only this evidence document and the F1 repository-only identity bump; all four temporary
source mutations were exactly restored and no production or test source change is retained.
The installer has no separate component key; its package identity follows
`repositoryVersion`.
