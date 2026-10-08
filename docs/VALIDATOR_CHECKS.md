# Component-version validator: canonical check list

`scripts/omp/validate-component-versions.ps1` is the version-consistency gate
that local CI and the pre-push hook run in every OMP-compatible repository.
Each repository carries its own copy of the validator, but the **check numbers
are a shared contract**: a given `Check N` means the same check in every
repository, so an error message crossing a repository boundary is never
ambiguous. Numbers are never reused for a different check and never renumbered;
new checks get the next unused number.

That numbering contract is upheld by this document and by review — **no guard
enforces it**. Check 15 byte-compares only `validate-component-versions.helpers.ps1`
and `bump-version.ps1`; `validate-component-versions.ps1` itself is deliberately
repo-local (see the next section), so nothing would mechanically catch a repository
that gave `Check 21` a different meaning. Verified 2026-09-08 by extracting every
`Check N` declaration from all nine validators: checks 1–10, 12, 13, 15 and 16 do
carry the same meaning everywhere they appear. What differs between repositories is
which checks are *present*, not what a number *means* — that difference is measured
in "Present-but-vacuous is not applied uniformly" below.

## The shared core vs repo-local flow

Two layers, deliberately separated:

- **Shared core (byte-identical everywhere, guarded):**
  `scripts/omp/validate-component-versions.helpers.ps1` holds the generic
  helpers (BOM-safe reading, fail-loud git readers, version/SQL/hash
  utilities, the canonical `-SelfTest`). It is copied verbatim into every
  OMP-compatible repository and the shared-script drift guard
  (`validate-shared-scripts.ps1`, wired as Check 15 in consumer validators)
  compares every copy byte-for-byte against this one. The same guard covers
  `scripts/omp/bump-version.ps1`, and -- in every repository that runs Pester
  suites or carries a copy of either file -- the canonical Pester step
  `scripts/omp/run-script-tests.ps1` plus `scripts/omp/pester-bootstrap.ps1`.
  Never edit a copy repo-locally: change the canonical file here and
  redistribute in the same change.
- **Repo-local flow (intentionally per repository):** the validator script
  itself (`scripts/omp/validate-component-versions.ps1`) decides which checks
  RUN. Repositories legitimately differ: this platform repository owns
  `sharedProjects` and Web.Shared, consumer repositories own the cross-repo
  cascade direction, and a repository without module-definition SQL has
  nothing for the SQL checks to bite on. This is why the canonical copy is
  **not** a superset and must never be copied blind over a consumer's
  validator. A check whose guarded artifact kind is absent must still be
  *present but vacuous* when it shares a number with the contract — absence
  of a numbered check from a validator that owns the artifact kind is drift,
  not adaptation.

Repo-local checks that exist in exactly one repository get numbers from the
shared list too (see 17 and 19 below), so a local addition can never collide
with a future canonical check. Reserve a number here before using it.

## Canonical checks

| # | Check | Kind | Runs where |
|---|-------|------|-----------|
| 1 | Component `projectPath` resolves to a `.csproj` | static | every repository |
| 2 | `repositoryVersion` presence and format | static | every repository |
| 3 | Component `version` presence and format | static | every repository |
| 4 | Module-definition version sync (manifest = definition file) | static | every repository with module definitions |
| 4b | Worker plugin host-contract (`minWorkerHostVersion` only on worker/worker-plugin) | static | **measured 2026-09-07: this platform repository and Contoso only** — exactly the two manifests that declare `minWorkerHostVersion`. See "Present-but-vacuous is not applied uniformly" below. |
| 5 | Component-to-module mapping integrity | static | every repository with module definitions |
| 6 | `minModuleDefinitionVersion` sanity (≤ declared definitionVersion) | static | every repository with module definitions |
| 7 | Shared-project cascade version bumps | base-diff | **bites** only where `sharedProjects` is declared (measured 2026-09-07: this platform repository alone). **Present but vacuous** in seven consumers; **absent by design** in Contoso only. See below. |
| 8 | Module-definition SQL diff enforcement (material SQL change ⇒ definitionVersion bump) | base-diff | every repository whose definitions own SQL |
| 8b | `minModuleDefinitionVersion` must not lag a bumped definitionVersion | base-diff | follows Check 8 (OpenDocViewer runs the stricter variant: must EQUAL — a superset that never passes where the canonical rule fails) |
| 9 | Transitive ProjectReference lockstep bumps | base-diff | every repository (vacuous without intra-repo references) |
| 10 | compatibleArtifacts range sanity | static | every repository with compatibleArtifacts |
| 11 | Web.Shared binary identity (parent vs HEAD, same environment) | base-diff + build | this platform repository only |
| 12 | Module-definition content diff enforcement (any content change ⇒ definitionVersion bump) | base-diff + worktree | every repository with module definitions |
| 13 | LOCKSTEP: own project source changed ⇒ component version AND repositoryVersion must move. Docs-only `*.md` changes are exempt EXCEPT under `wwwroot` (dotnet publish copies `wwwroot` verbatim, so such markdown is payload). Out-of-project payload inputs (csproj `Content`/`None`/`Compile` includes that escape the project directory, e.g. `..\tools\…` published into `wwwroot`) are watched too | base-diff + worktree | every repository |
| 14 | Cross-repository shared project cascade (calls `validate-shared-dependencies.ps1` here) | cross-repo | consumer repositories with `sharedDependencies`; intentionally absent here and in consumers that reference no shared projects |
| 15 | Shared-script drift guard (calls `validate-shared-scripts.ps1` here) | cross-repo | consumer validators; this repository is the canonical source and self-skips |
| 16 | Embedded sqlScripts freshness (embedded content/sha256 = on-disk SQL) | worktree | every repository whose definitions embed SQL |
| 17 | (repo-local) unconditional artifact-pointer overwrite guard | static SQL scan | exactly one consumer repository |
| 18 | consistentArtifactSets lockstep (`exact` versionMatchRule) | static | repositories whose module definitions declare `consistentArtifactSets` (currently this platform repository and Contoso) |
| 19 | (repo-local) runtime cascade-bump | base-diff | exactly one consumer repository |
| 20 | Embedded sqlScripts line endings must match what `.gitattributes` declares for the SQL path (`git check-attr text eol`; text unset or no eol attribute = any form accepted; a git lookup that cannot answer at all is a validation **error**, never a silent "bytes untouched") | worktree | every repository whose definitions embed SQL |
| 21 | Direct `TimeZoneInfo.FindSystemTimeZoneById`/`TryFindSystemTimeZoneById`/`TryConvertIanaIdToWindowsId` use is confined to the sanctioned lookup file in production `.cs` files, because hosts without `icu.dll` (before Windows 10 1903 / Server 2019) run .NET in NLS mode where all three fail for IANA ids; production code resolves zones through `OmpTimeZoneLookup`. Matching runs on source with comments and string/char literals masked out -- verbatim and raw strings are masked across line breaks, while interpolation holes (`$"...{expr}..."`) are scanned as code, so a call inside a hole counts. It catches `using static System.TimeZoneInfo` bare calls, `using X = System.TimeZoneInfo` aliases (including the `global using` forms, which are collected repo-wide because a global using applies to every file in the compilation wherever it is declared), fully qualified `System.TimeZoneInfo.…`/`global::` forms, whitespace/newlines around the `.`, and method-group use without parentheses; `nameof(...)` is a name lookup, not a call, and never matches. Excluded: `bin`/`obj`, directories named exactly `test`/`tests` or ending in `.Test`/`.Tests`, and files under a test `.csproj` (name ends in `.Test(s)` or references `Microsoft.NET.Test.Sdk` / sets `<IsTestProject>true</IsTestProject>`). The exemption is exact: the whole file must be named `*TimeZoneLookup.cs` (letters/digits/underscore only, e.g. `OmpTimeZoneLookup.cs` or `NotATimeZoneLookup.cs`) AND the file must declare a type named exactly like the file -- `OmpTimeZoneLookup.cs` declaring `class OmpTimeZoneLookup` is exempt, as is `NotATimeZoneLookup.cs` declaring `class NotATimeZoneLookup`; a file whose name merely carries the substring but declares no eponymous type (`NotATimeZoneLookup.cs` holding only `class Calendar`) is not exempt | worktree | this platform repository; synced to consumer validators by the omp-tidszon-utan-icu-server2016 campaign |
| 22 | The embed tool's line-ending helpers (`Get-GitDeclaredLineEnding`, `ConvertTo-DeclaredLineEndings` in `scripts/dev/embed-module-definition-sql.ps1`) are byte-identical copies of the shared core (function texts compared via the PowerShell parser) | worktree | this platform repository only (vacuous where the embed tool is absent) |
| 23 | (repo-local) `minModuleDefinitionVersion` must not lag the module's current definitionVersion | static | exactly one consumer repository (LogSearch) — renumbered from its local "Check 20" on 2026-10-07 to resolve the collision with canonical Check 20 |

Checks 7, 8, 9, 11, 12, 13 and 19, and the unnumbered `repositoryVersion`
rule (repositoryVersion must move when any component version moved since the
baseline), diff against `-BaseCommit` (default
`origin/main`); the diff readers are fail-loud — a git answer that could not
be read is a validation error, never a silent pass. Check 13 additionally
scans the working tree and untracked files so an uncommitted own-source edit
is caught before it is committed.

## Finding the platform checkout (Checks 14 and 15)

Check 15 compares against the OpenModulePlatform checkout. `validate-shared-scripts.ps1`
resolves it in this order: `-PlatformRepositoryRoot`, then the environment variable
`OMP_PLATFORM_ROOT`, then `OpenModulePlatformRoot`, then a sibling directory named
`OpenModulePlatform`. Set `OMP_PLATFORM_ROOT` when the consumer checkout does not sit
beside the platform checkout, for example a git worktree created under another root.

When no root resolves, the guard reports `NOT VERIFIED`. Without `-Strict` that is a
warning and exit 0; with `-Strict` it is exit 1. Pre-push hooks and `scripts/local-ci.ps1`
must run the validator with `-Strict`. Measured 2026-09-26: jobs running in worktrees under
another root found no sibling, got the warning, and pushed shared-script drift that then
stopped every local push in eight repositories. Plain ad-hoc runs, and CI that checks out a
single repository, are the only callers that should leave `-Strict` off.

A root that is named explicitly (`-PlatformRepositoryRoot` or `OMP_PLATFORM_ROOT`) but is not an
OpenModulePlatform checkout (the canonical scripts or `omp-components.json` are missing) is a
configuration error: exit 1 even without `-Strict`. A sibling directory that exists but is not a
checkout is reported like a missing sibling: warning and exit 0 without `-Strict`, exit 1 with it.
Relative roots are resolved against the consumer repository root, not the current directory.

The consumer validator locates `validate-shared-scripts.ps1` (and, for Check 14,
`validate-shared-dependencies.ps1`) before calling it. That lookup used to be repo-local code in
each consumer, and when it found nothing it warned and exited 0 unless `-Strict` was passed -- a
green validation for a check that never ran. The lookup now belongs in the shared core:
`Resolve-PlatformCheckScript` in `validate-component-versions.helpers.ps1` resolves
`-PlatformRepositoryRoot`, `OMP_PLATFORM_ROOT`, `OpenModulePlatformRoot` and the sibling in that
order and returns the script path and platform root. When nothing is found it records a
validation **error** whose message says how to set `OMP_PLATFORM_ROOT`, with or without `-Strict`.
The one way to accept a missing checkout is the explicit exception `OMP_ALLOW_MISSING_PLATFORM=1`
(meant for CI that checks out one repository): the check is then reported as a `NOT VERIFIED`
warning, never skipped in silence. A root that is named explicitly but does not hold the script
is an error even under the exception. Consumer validators wire Checks 14 and 15 like this:

```powershell
$check15 = Resolve-PlatformCheckScript -RepositoryRoot $repositoryRoot `
    -ScriptRelativePath 'scripts/omp/validate-shared-scripts.ps1' -CheckLabel 'Check 15' `
    -Errors $errors -Warnings $warnings -PlatformRepositoryRoot $PlatformRepositoryRoot
if ($null -ne $check15) {
    & $check15.ScriptPath -ConsumerRepositoryRoot $repositoryRoot -PlatformRepositoryRoot $check15.PlatformRoot -Strict:$Strict
    if ($LASTEXITCODE -ne 0) {
        Add-ValidationError -Errors $errors -Message 'Check 15 (shared script drift) failed; see the Check 15 lines above.'
    }
}
```

## Enumerating the consumer repositories (Check 14 scope)

A Web.Shared (or other shared-project) cascade must take its consumer list from
`scripts/omp/list-shared-consumers.ps1`, never from memory. The script walks the
sibling repositories of the OpenModulePlatform checkout, finds every repository
whose git-tracked projects — `.csproj` **and** `Directory.Build.props` /
`Directory.Build.targets`, since a `ProjectReference` can legally live in any of
them — carry a `ProjectReference` into a shared project declared in this
repository's `omp-components.json` (`sharedProjects`), and reports whether each
referencing repository declares the matching `sharedDependencies`/`treeId` entry
that Check 14 enforces. It exits non-zero when a referencing repository does not
declare the dependency, so it doubles as the gap detector. It also exits
non-zero when a sibling repository cannot be scanned at all (`git ls-files`
fails): an unreadable repository must never read as "nothing to declare".

```powershell
.\scripts\omp\list-shared-consumers.ps1
# or, when the consumer checkouts do not sit beside this one:
.\scripts\omp\list-shared-consumers.ps1 -SiblingRoot 'D:\path\to\workspace'
```

Measured 2026-10-07 why this must not come from memory: a cascade ran against a
remembered list of six consumers and missed two referencing repositories — one
that already carried a `treeId`, and one with a `ProjectReference` but no
`sharedDependencies` block at all. Both are invisible until someone lists the
references mechanically.

## Present-but-vacuous is not applied uniformly (measured 2026-09-07)

The rule above says a numbered check "must still be *present but vacuous*" where its guarded
artifact kind is absent. Measuring the nine OMP-compatible validators shows the family does not
follow that rule consistently — and the two exceptions point in **opposite** directions:

| | Declares the artifact | Check present in validator | Check absent |
|---|---|---|---|
| **Check 7** (`sharedProjects`) | OpenModulePlatform only | OpenModulePlatform + 7 consumers (vacuous) | **Contoso only** — explicitly "Absent by design" in its own header |
| **Check 4b** (`minWorkerHostVersion`) | OpenModulePlatform + Contoso | exactly those two | **the other 7 consumers** |

So Check 7 is kept present-and-vacuous almost everywhere, while Check 4b is dropped wherever it
would be vacuous. Both patterns are defensible on their own; having both at once means the phrase
"present but vacuous" does not currently describe the family.

**This is a validator question, not a documentation question** — the table above records what the
scripts do today, deliberately without changing them. Deciding which pattern is the contract (and
making the nine validators agree) needs a campaign; until then, read "Runs where" as *measured*,
not as *specified*.

Method, so this is reproducible: `grep -oE "Check [0-9]+[a-b]?"` over each
`scripts/omp/validate-component-versions.ps1`, then confirming each hit is executable code rather
than a comment (Check 14 appears in this repository's validator only inside comments that hand the
check to the consumer's jurisdiction — it is genuinely absent here, as the table states), and
`grep -c` for the guarding key in each `omp-components.json`.

## Path convention

Every shared omp script — `bump-version.ps1`,
`validate-component-versions.ps1`,
`validate-component-versions.helpers.ps1`, the package builders — lives under
`scripts/omp/` in every repository, including consumers. An earlier layout
kept the consumer validators at `scripts/`; that difference was accidental
(the scripts predate the `scripts/omp/` convention) and was removed in the
same campaign that established this list.

## When the canonical files change

Verified 2026-09-07: both canonical files are byte-identical across all nine OMP-compatible
repositories — `validate-component-versions.helpers.ps1` at md5 `af9e9dcc…` and
`bump-version.ps1` at md5 `9b5abf18…` (1 039 lines) in every one of them. (AgentDocMap carries no
validator at all; it is a Node/JS documentation tool, not an OMP component.) Deliberately recorded
as a *measurement with a date*, not as a hash list to maintain — see the paragraph below.

The canonical files ARE the reference; there is no hash list to update.
Changing `bump-version.ps1` or `validate-component-versions.helpers.ps1`
here turns Check 15 red in every consumer whose copy is stale — that is the
guard working. The update path is: change the canonical file, copy it
verbatim into each consumer repository in the same campaign, and let each
repository's validator prove the copy landed. `validate-shared-scripts.ps1`
prints exactly which repositories still need the copy.

When a validator change alters which checks exist or what a number means,
update this document in the same commit.
