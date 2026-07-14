# ADR 0003: Web.Shared private-consumer cross-repo cascade awareness

## Status

Proposed (2026-07-14)

## Context

`OpenModulePlatform.Web.Shared` is the canonical OMP web integration library (topbar, role-switch, auth defaults, localization, etc.). It lives in the public `OpenModulePlatform` repository and is consumed inside that repository by Portal, Auth, ContentWebApp, iFrameWebApp, and the example modules. The public repo already records those in-repo consumers in `omp-components.json` under `sharedProjects` and enforces cascade bumps when the Web.Shared source or binary changes.

Five private consumer repositories also build against `OpenModulePlatform.Web.Shared`:

- `IbsPackager`
- `LogSearch`
- `EArkivChecker`
- `Dokumentbibliotek`
- `VajSkrivare`

Each repository is intentionally independent: it owns its own `omp-components.json`, its own component versions, and its own ported `scripts/validate-component-versions.ps1`. They share no common version-lock file with OpenModulePlatform.

## Problem

A change to `OpenModulePlatform.Web.Shared` in the OpenModulePlatform repository can break or silently alter behavior in the five private consumer web apps. Today there is **no central awareness** that a Web.Shared change in OMP should cascade to those consumers. Each repo's local validators only enforce lockstep inside that repo; none of them compare their declared state against the upstream OMP Web.Shared version or hash.

This is a design/documentation gap, not a bug in any single repo. The independence of the consumer repos is intentional and must be preserved, but the absence of cross-repo cascade awareness leaves the operator-dependent on manual tracking and build discipline.

## Current state

### OMP already handles in-repo Web.Shared cascade

`OpenModulePlatform/omp-components.json` declares `OpenModulePlatform.Web.Shared` as a shared project and lists all in-repo consumers:

```json
"sharedProjects": [
  {
    "projectPath": "OpenModulePlatform.Web.Shared/OpenModulePlatform.Web.Shared.csproj",
    "description": "Canonical OMP web integration library (topbar, role-switch, auth defaults)",
    "consumers": [
      "omp-portal-web",
      "omp-auth-web",
      "content-webapp",
      "iframe-webapp",
      "example-webapp",
      "example-webapp-blazor",
      "example-serviceapp-web",
      "example-workerapp-web"
    ]
  }
]
```

`scripts/omp/validate-component-versions.ps1` uses that declaration for two checks:

- **Check 7** (`scripts/omp/validate-component-versions.ps1:609-675`): if the Web.Shared source tree changed since the baseline, every declared consumer component must have its version bumped.
- **Check 11** (`scripts/omp/validate-component-versions.ps1:678-822`): builds `OpenModulePlatform.Web.Shared.dll` from both the parent commit and HEAD with deterministic settings and compares the SHA-256 hashes. If the binary changed but no in-repo consumer was bumped, the check fails.

There is also a contract scanner, `scripts/omp/validate-webshared-contracts.ps1:1-18`, but it only scans consumers declared inside the same `omp-components.json`.

### Each private repo consumes Web.Shared via `<ProjectReference>` to OMP source

| Repository | Web.Shared reference path | How `OpenModulePlatformRoot` is resolved |
|---|---|---|
| `IbsPackager` | `IbsPackager.Web/IbsPackager.Web.csproj:11` — `$(OpenModulePlatformRoot)\OpenModulePlatform.Web.Shared\OpenModulePlatform.Web.Shared.csproj` | `Directory.Build.targets:9-10` defaults to sibling `../OpenModulePlatform` |
| `LogSearch` | `LogSearch.Web/LogSearch.Web.csproj:7` — `$(OpenModulePlatformRoot)\OpenModulePlatform.Web.Shared\OpenModulePlatform.Web.Shared.csproj` | `Directory.Build.targets:7-8` defaults to sibling `../OpenModulePlatform` |
| `EArkivChecker` | `EArkivChecker.Web/EArkivChecker.Web.csproj:7` — `$(OpenModulePlatformRoot)\OpenModulePlatform.Web.Shared\OpenModulePlatform.Web.Shared.csproj` | `Directory.Build.targets:7-8` defaults to sibling `../OpenModulePlatform` |
| `Dokumentbibliotek` | `RazorPages/OpenModulePlatform.Web.eArkivDokumentbibliotek.RazorPages.csproj:27` — `..\..\OpenModulePlatform\OpenModulePlatform.Web.Shared\OpenModulePlatform.Web.Shared.csproj` | `Directory.Build.targets:7-8` defaults to sibling `../OpenModulePlatform` |
| `VajSkrivare` | `src/Skrivarkoppling.Web/Skrivarkoppling.Web.csproj:17` — `..\..\..\OpenModulePlatform\OpenModulePlatform.Web.Shared\OpenModulePlatform.Web.Shared.csproj` | Hard-coded relative path (no `Directory.Build.targets` override found) |

None of the five repos consume Web.Shared as a NuGet package or a copied binary; they all compile against the OMP source tree on the local workstation.

### No private repo declares Web.Shared in `omp-components.json`

All five `omp-components.json` files have a `components` array and one or more `moduleDefinitions`, but **none of them have a `sharedProjects` section**.

- `IbsPackager/omp-components.json:1-53` — no `sharedProjects`.
- `LogSearch/omp-components.json:1-47` — no `sharedProjects`.
- `EArkivChecker/omp-components.json:1-47` — no `sharedProjects`.
- `Dokumentbibliotek/omp-components.json:1-27` — no `sharedProjects`.
- `VajSkrivare/omp-components.json:1-27` — no `sharedProjects`.

Because there is no `sharedProjects` entry, the existing `validate-component-versions.ps1` logic used in OpenModulePlatform cannot be ported verbatim to those repos for Web.Shared.

### Ported validators explicitly disclaim cross-repo cascade

Three of the five validators explicitly state that consumer repos have no shared-projects cascade:

- `LogSearch/scripts/validate-component-versions.ps1:16-17`: "This script is a simplified counterpart to OpenModulePlatform's validate-component-versions.ps1. Consumer repos have no sharedProjects cascade."
- `EArkivChecker/scripts/validate-component-versions.ps1:16-17`: same text.
- `Dokumentbibliotek/scripts/validate-component-versions.ps1:16-17`: same text.

`VajSkrivare/scripts/validate-component-versions.ps1:1-40` does not contain the same comment, but its logic likewise contains no Web.Shared cascade check.

`IbsPackager/scripts/validate-component-versions.ps1:481-574` has its own **Check 10** for `IbsPackager.Runtime` cascade bumps (`ibs-packager-web`, `ibs-packager-worker`, `file-drop-channel-type`), but there is no equivalent check for `OpenModulePlatform.Web.Shared`.

### Local CI / pre-push gates are repo-local

Every private repo's `pre-push.ps1` simply calls its own `scripts/local-ci.ps1`:

- `IbsPackager/.githooks/pre-push.ps1:33-43`
- `LogSearch/.githooks/pre-push.ps1:33-43`
- `EArkivChecker/.githooks/pre-push.ps1:33-43`
- `Dokumentbibliotek/.githooks/pre-push.ps1:33-43`
- `VajSkrivare/.githooks/pre-push.ps1:33-43`

Each `local-ci.ps1` performs two steps:

1. `dotnet build` of that repo's solution, passing `/p:OpenModulePlatformRoot=..\OpenModulePlatform` (or resolving a sibling path).
2. `scripts/validate-component-versions.ps1 -BaseCommit origin/main` inside that repo.

The build will naturally pick up whatever Web.Shared source is present in the sibling `OpenModulePlatform` folder, but the validator does not flag whether the consumer's declared version reflects a newer Web.Shared than the last known-good build. A Web.Shared change in OMP is therefore invisible to the consumer repo's own gate unless the consumer developer manually notices the build output change and bumps the component.

### AI Orchestrator only serializes shared builds, it does not version-check

`DEV/OpenModulePlatform/AI-Orchestrator/src/gui/jobConcurrency.ts:24-33` lists the repositories that share OMP web builds:

```typescript
const ompSharedBuildRepoNames = new Set([
  'openmoduleplatform',
  'dokumentbibliotek',
  'logsearch',
  'earkivchecker',
  'vajskrivare',
  'ibspackager',
  'ikrock2',
]);
```

The same file uses `build:omp-web-shared` as an exclusive lock for implementation-mode jobs (`jobResourceLocks`, `usesOmpSharedBuild`, `jobConcurrency.ts:92-94`). This prevents two jobs from building `OpenModulePlatform.Web.Shared` concurrently on the same workstation, but it does **not** verify that each consumer repo has bumped its version after a Web.Shared change.

## Gap confirmation

**What IS checked today:**

1. Inside OpenModulePlatform: Web.Shared source changes force in-repo consumer bumps (Check 7).
2. Inside OpenModulePlatform: Web.Shared binary changes force in-repo consumer bumps (Check 11).
3. Inside each private repo: component versions are bumped when that component's own project files change.
4. AI Orchestrator: builds that touch OMP web-shared output are serialized to avoid bin/obj corruption.

**What is NOT checked today:**

1. No private repo records which Web.Shared version/hash it was last built against.
2. No private repo validator fails or warns when OMP's Web.Shared changes but the consumer component version is unchanged.
3. OMP's validators and pre-push gate (`OpenModulePlatform/.githooks/pre-push.ps1:100-106`) have no knowledge of the private repos.
4. AI Orchestrator knows the repos share a build but does not track cross-repo version dependencies.

## Proposal

Introduce a lightweight, opt-in cross-repo cascade-awareness mechanism. The recommended approach is a **declared expected Web.Shared version/hash per consumer** combined with a **central comparator** that can run in OMP's local CI or as a standalone AO job.

### Mechanism A: Declared Web.Shared version/hash per consumer (recommended)

Each private repo records the Web.Shared version it expects/was last verified against. The declaration can live in one of two places:

**Option A1 — inside each consumer's `omp-components.json`:**

```json
{
  "manifestVersion": 1,
  "repositoryKey": "ibspackager",
  "repositoryVersion": "0.3.40",
  "sharedProjects": [
    {
      "projectPath": "$(OpenModulePlatformRoot)/OpenModulePlatform.Web.Shared/OpenModulePlatform.Web.Shared.csproj",
      "external": true,
      "expectedSourceRepositoryKey": "openmoduleplatform",
      "expectedRepositoryVersion": "0.3.256",
      "expectedWebSharedHash": "sha256:abc123...",
      "consumers": [ "ibs-packager-web" ]
    }
  ],
  "components": [ ... ]
}
```

A new central validator (for example `scripts/omp/validate-webshared-consumer-cascade.ps1` in OpenModulePlatform, or an AI Orchestrator job) reads:

- OMP's current `omp-components.json` to obtain `repositoryVersion` and the current Web.Shared source/binary hash.
- Each private repo's `omp-components.json` to read the declared `expectedRepositoryVersion` / `expectedWebSharedHash`.

Rules:

- If the declared values match the current OMP state → PASS.
- If the declared values are older than the current OMP state → WARN or ERROR, depending on policy.
- If the private repo has no declaration → treat as opt-out (no error) until the repo opts in.

**Why A1 is preferred:** the truth lives in each repo's own manifest, preserving independence. The check can be run from OMP without giving OMP write access to the private repos.

**Option A2 — a separate `webshared-consumer-manifest.json` per repo:**

Keeps the cross-repo declaration out of `omp-components.json` entirely. This decouples the consumer manifest from component-version semantics but adds another file to maintain and keep in sync.

### Mechanism B: CI-time cross-repo build verification

When OMP detects a Web.Shared change, an AO job or GitHub Actions workflow checks out each private repo and runs its local CI/build against the **current** OMP source. If the build fails, or if the resulting consumer binary differs from the previous known-good binary without a version bump, the job flags the repo.

Rules:

- Build each private repo's web component against the OMP Web.Shared source at HEAD.
- Compare the emitted consumer DLL/package with the baseline for that component version.
- If the binary changed but the component version was not bumped → flag.

**Trade-offs:** fully automatic for the operator, but slower and more fragile. It requires access to private repo source at build time and does not give the consumer repo a durable declaration of "I was built against Web.Shared X." It also does not help when the consumer repo is built later on a different workstation.

### Mechanism C: Notification/OMP-side registry (lightweight alternative)

OpenModulePlatform maintains a read-only registry of known private consumers and their last-verified Web.Shared state:

```json
{
  "knownExternalConsumers": [
    {
      "repositoryKey": "ibspackager",
      "componentKey": "ibs-packager-web",
      "lastVerifiedOmpRepositoryVersion": "0.3.256",
      "lastVerifiedWebSharedHash": "sha256:abc123...",
      "verifiedAt": "2026-07-14"
    }
  ]
}
```

A periodic AO job or local script diffs OMP's current Web.Shared against the registry and emits warnings when the state has moved past a known consumer's last verification.

**Trade-offs:** the truth lives in OMP rather than the consumer repo, which is simpler for OMP but hides the contract from the consumer repo's own validators. It also requires someone to update the registry after each consumer verification.

### Comparison

| Concern | Mechanism A (declared per consumer) | Mechanism B (CI cross-build) | Mechanism C (OMP-side registry) |
|---|---|---|---|
| Source of truth | consumer repo's own manifest | actual build output | OMP-managed registry |
| Consumer independence | high — repo owns its declaration | medium — repo is built by external job | low — OMP owns the contract |
| Operator visibility | explicit in consumer `omp-components.json` | implicit in CI logs | explicit in OMP registry file |
| Build-time cost | low (metadata comparison) | high (N repo builds) | low (metadata comparison) |
| Backward compatibility | opt-in; missing declaration = no check | requires private repo access and working builds | opt-in registry; missing entry = no check |
| Works offline / per-workstation | yes | no (needs all repos) | yes if registry is committed |
| Respect for repo independence | yes | partial | weak |

## Decision flags for ÄGARBESLUT

**(a) FORCE vs WARN**

- **WARN (recommended first step)**: the central check logs a high-severity warning and surfaces a diagnostic list of out-of-sync consumers. OMP pushes are not blocked, and consumer repos continue to build.
- **FORCE**: OMP push is blocked if any opted-in consumer's declared Web.Shared version/hash lags behind OMP's current state. This requires every opted-in consumer to be updated before OMP can push, which is the long-term desired state but likely too disruptive for an initial rollout.

**(b) WHERE the consumer declaration lives**

- **Option 1 (recommended)**: add the declaration to each consumer repo's `omp-components.json` under a new `sharedProjects` entry marked `external: true`. The consumer repo owns the contract, and OMP's validator reads it read-only.
- **Option 2**: add a separate `webshared-consumer-manifest.json` per consumer repo. Decouples from component versioning but adds maintenance.
- **Option 3**: keep the registry in OpenModulePlatform only (Mechanism C). Simplest for tooling but centralizes truth in OMP.

**(c) SCOPE**

- **All five private repos, opt-in (recommended)**: declare the new section in `IbsPackager`, `LogSearch`, `EArkivChecker`, `Dokumentbibliotek`, and `VajSkrivare`. Repos that have not yet added the section are ignored. This preserves independence while making the gap visible for active consumers.
- **Opt-in per repo**: each consumer repo decides whether to participate. Same practical effect as "all five, opt-in" at the start, but the policy statement makes clear that participation is voluntary.
- **Mandate all five immediately**: not recommended because it couples OMP pushes to private-repo state without a transition period.

## Migration considerations

1. **Opt-in only.** Consumer repos without the new declaration continue exactly as today. No existing build, validator, or pre-push gate changes behavior.
2. **Start as WARN.** Run the comparator as a separate AO job or local script for at least one sprint to measure drift before considering FORCE.
3. **No OpenModulePlatform source changes needed for the ADR itself.** If Mechanism A1 is accepted, the private repos add a `sharedProjects` entry; OpenModulePlatform adds the comparator script/job. Neither change alters Web.Shared source, SQL, or component versions.
4. **Hash computation must be stable.** If `expectedWebSharedHash` is used, compute it the same way as `scripts/omp/validate-component-versions.ps1:154-185` (deterministic build, identical settings). Otherwise use `repositoryVersion` from OMP's `omp-components.json:4`, which is already bumped on every significant platform change.
5. **Preserve AI Orchestrator build serialization.** The existing `build:omp-web-shared` exclusive lock in `jobConcurrency.ts` remains unchanged; this ADR adds version awareness, not build orchestration.
6. **Manual verification path for VGR/customer environments.** If the comparator is not runnable in a customer environment, produce a manual checklist: after deploying a new OMP web-shared artifact, verify each consumer web app was rebuilt against that OMP `repositoryVersion` and its component version was bumped if the binary changed.

## Consequences

- Private repos gain an explicit, versioned place to declare which OMP Web.Shared state they were built against.
- OMP developers can see, before pushing, which external consumers are potentially out of sync.
- The change is additive: existing validators, pre-push gates, and AI Orchestrator locks continue to work unchanged.
- No source code, SQL, or component version changes are required to adopt this ADR; only metadata declarations and a comparator script/job.

## References

- `OpenModulePlatform/omp-components.json:52-67`
- `OpenModulePlatform/scripts/omp/validate-component-versions.ps1:609-675`
- `OpenModulePlatform/scripts/omp/validate-component-versions.ps1:678-822`
- `OpenModulePlatform/scripts/omp/validate-webshared-contracts.ps1:1-18`
- `OpenModulePlatform/.githooks/pre-push.ps1:100-106`
- `DEV/OpenModulePlatform/AI-Orchestrator/src/gui/jobConcurrency.ts:24-33`
- `DEV/OpenModulePlatform/AI-Orchestrator/src/gui/jobConcurrency.ts:92-94`
- `IbsPackager/Directory.Build.targets:9-10`
- `IbsPackager/IbsPackager.Web/IbsPackager.Web.csproj:11`
- `IbsPackager/omp-components.json:1-53`
- `IbsPackager/scripts/validate-component-versions.ps1:481-574`
- `LogSearch/Directory.Build.targets:7-8`
- `LogSearch/LogSearch.Web/LogSearch.Web.csproj:7`
- `LogSearch/omp-components.json:1-47`
- `LogSearch/scripts/validate-component-versions.ps1:16-17`
- `EArkivChecker/Directory.Build.targets:7-8`
- `EArkivChecker/EArkivChecker.Web/EArkivChecker.Web.csproj:7`
- `EArkivChecker/omp-components.json:1-47`
- `EArkivChecker/scripts/validate-component-versions.ps1:16-17`
- `Dokumentbibliotek/Directory.Build.targets:7-8`
- `Dokumentbibliotek/RazorPages/OpenModulePlatform.Web.eArkivDokumentbibliotek.RazorPages.csproj:27`
- `Dokumentbibliotek/omp-components.json:1-27`
- `Dokumentbibliotek/scripts/validate-component-versions.ps1:16-17`
- `VajSkrivare/src/Skrivarkoppling.Web/Skrivarkoppling.Web.csproj:17`
- `VajSkrivare/omp-components.json:1-27`
- `VajSkrivare/scripts/validate-component-versions.ps1:1-40`
