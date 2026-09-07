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
that gave `Check 20` a different meaning. Verified 2026-09-08 by extracting every
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
  `scripts/omp/bump-version.ps1`. Never edit a copy repo-locally: change the
  canonical file here and redistribute in the same change.
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
| 4b | Worker plugin host-contract (`minWorkerHostVersion` only on worker/worker-plugin) | static | **measured 2026-09-07: this platform repository and IbsPackager only** — exactly the two manifests that declare `minWorkerHostVersion`. See "Present-but-vacuous is not applied uniformly" below. |
| 5 | Component-to-module mapping integrity | static | every repository with module definitions |
| 6 | `minModuleDefinitionVersion` sanity (≤ declared definitionVersion) | static | every repository with module definitions |
| 7 | Shared-project cascade version bumps | base-diff | **bites** only where `sharedProjects` is declared (measured 2026-09-07: this platform repository alone). **Present but vacuous** in seven consumers; **absent by design** in IbsPackager only. See below. |
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
| 18 | consistentArtifactSets lockstep (`exact` versionMatchRule) | static | repositories whose module definitions declare `consistentArtifactSets` (currently this platform repository and IbsPackager) |
| 19 | (repo-local) runtime cascade-bump | base-diff | exactly one consumer repository |

Checks 7, 8, 9, 12, 13 and 19 diff against `-BaseCommit` (default
`origin/main`); the diff readers are fail-loud — a git answer that could not
be read is a validation error, never a silent pass. Check 13 additionally
scans the working tree and untracked files so an uncommitted own-source edit
is caught before it is committed.

## Present-but-vacuous is not applied uniformly (measured 2026-09-07)

The rule above says a numbered check "must still be *present but vacuous*" where its guarded
artifact kind is absent. Measuring the nine OMP-compatible validators shows the family does not
follow that rule consistently — and the two exceptions point in **opposite** directions:

| | Declares the artifact | Check present in validator | Check absent |
|---|---|---|---|
| **Check 7** (`sharedProjects`) | OpenModulePlatform only | OpenModulePlatform + 7 consumers (vacuous) | **IbsPackager only** — explicitly "Absent by design" in its own header |
| **Check 4b** (`minWorkerHostVersion`) | OpenModulePlatform + IbsPackager | exactly those two | **the other 7 consumers** |

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
