# Codex Development Guide

This guide is the compact, agent-friendly entry point for development with Codex
from VS Code. Keep it factual and current. Put detailed architecture and product
context in the linked documents, not here.

## Repository Role

OpenModulePlatform is the neutral platform repository. It must not contain
customer-specific consumer-repository behavior, credentials, or deployment
assumptions beyond documented local development defaults.

Use these files as the main map:

- `AGENTS.md` - operational rules for Codex and other coding agents.
- `README.md` - human project overview and quick start.
- `docs/README.md` - documentation index and recommended reading order.
- `docs/TERMINOLOGY.md` - platform, deployment, and operations glossary.
- `docs/ARCHITECTURE.md` - platform model and request flows.
- `docs/AUTHENTICATION_AND_RBAC.md` - shared OMP authentication, users, role principals, and RBAC.
- `docs/ADMIN_CONFIGURATION.md` - manual Portal administration guidance.
- `docs/VERSIONING_AND_IDENTITIES.md` - artifact versioning and stable identity policy.
- `docs/PORTABLE_DEPLOYMENT_OBJECTS.md` - module-definition and artifact-package object standards.
- `docs/MODULE_DEFINITIONS.md` - versioned module definition documents and SQL ownership.
- `docs/ARTIFACT_PACKAGES.md` - manifest-based artifact package envelope and configuration-file packaging.
- `docs/CONFIG_OVERLAYS.md` - host configuration and host-specific config overlay object standard.
- `docs/UNIVERSAL_MODULE_PACKAGES.md` - universal zip container for OMP portable objects.
- `docs/OMP_COMPONENT_MANIFEST.md` - repository component manifest and version-bump helper usage.
- `docs/HOST_AGENT.md` - HostAgent runtime model, desired-state application, and operational behavior.
- `docs/HOST_AGENT_FIRST_INSTALL.md` - HostAgent-first package and bootstrapper flow.
- `PUBLISH_README.md` - publish helper behavior and local runtime layout.
- `installer/README.md` - public sample HostAgent-first installer layout.
- `scripts/README.md` - current script inventory.
- `sql/README.md` - SQL setup and initialization conventions.

## Reading Order For New Agents

Use the smallest reading set that fits the task:

- Orientation: `AGENTS.md`, `docs/README.md`, `docs/TERMINOLOGY.md`, and
  `docs/ARCHITECTURE.md`.
- Deployment pipeline: add `docs/PORTABLE_DEPLOYMENT_OBJECTS.md`,
  `docs/MODULE_DEFINITIONS.md`, `docs/ARTIFACT_PACKAGES.md`,
  `docs/CONFIG_OVERLAYS.md`, `docs/UNIVERSAL_MODULE_PACKAGES.md`,
  `docs/HOST_AGENT_FIRST_INSTALL.md`, and `docs/HOST_AGENT.md`.
- Repository packaging: add `docs/OMP_COMPONENT_MANIFEST.md`,
  `docs/VERSIONING_AND_IDENTITIES.md`, `installer/README.md`, and
  `scripts/README.md`.
- Local runtime validation: add `PUBLISH_README.md`, `sql/README.md`, and the
  relevant runtime docs such as `docs/HOSTING_WINDOWS_IIS.md` or
  `docs/WORKER_RUNTIME.md`.

## OMP-Compatible Repository Conventions

Module repositories should be easy for both humans and agents to inspect. A
well-formed OMP-compatible repository should normally provide:

- `AGENTS.md` for repository-specific agent rules.
- `omp-components.json` for module definitions and artifact components owned by
  the repository.
- `scripts/omp/export-universal-package.ps1` when the repository can export
  portable objects directly.
- README or docs that explain which module keys, app keys, artifacts, widgets,
  and config overlays the repository owns.

Private consumer repositories may include customer-specific data, but that data
must not leak into this public OpenModulePlatform repository.

## Language and Documentation Policy

- Write code, comments, SQL, scripts, and development documentation in English.
- Use Swedish only in application localization resources, such as `.resx` files.
- Prefer short Markdown sections with stable headings, concrete paths, and runnable commands.
- Keep AI-facing instructions in `AGENTS.md` and this file; avoid duplicating rules across many READMEs.

## Safe Change Workflow

1. Inspect files before editing. Do not infer schema, route, project, or script behavior from names alone.
2. Keep changes scoped to the requested behavior and the owning repository.
3. Update docs when behavior, bootstrap flow, local install steps, or public guidance changes.
4. Run the narrowest useful validation.
5. If the user needs to see the change in IIS, run the matching local install or publish script after building.

## Validation Ladder

Use the narrowest level that gives real confidence:

- C# changes: `dotnet build OpenModulePlatform.slnx`
- Publish script changes: parse the changed `.ps1` file with `System.Management.Automation.Language.Parser`
- Script-logic changes to `scripts/omp/bump-version.ps1`, `scripts/omp/validate-component-versions*.ps1`, `scripts/omp/get-ci-version-matrix.ps1`, or `scripts/omp/assert-tests-executed.ps1`: run `scripts/omp/run-script-tests.ps1` (Pester 5 suites, pinned to Pester 5.9.1; also blocking in pre-push and CI)
- SQL changes: review idempotency, rerun only when the task explicitly requires local data mutation. If the changed SQL is referenced by a module definition `sqlScripts[].path`, run `.\scripts\dev\embed-module-definition-sql.ps1` before `.\scripts\omp\validate-module-definitions.ps1`.
- Formatting hygiene: `git diff --check`
- Local web visibility: publish/update the runtime, then verify the relevant localhost URL

### Pushing a version bump

`repositoryVersion` in `omp-components.json` is the repository's hottest field, and two
machines bumping in parallel take the same number: git merges the identical lines cleanly and
the gate fails afterwards with "changed but not bumped". Use
`scripts/omp/push-with-rebump.ps1` instead of a hand-run fetch/rebase/bump/push loop. It
fetches, rebases when origin moved, re-runs `scripts/omp/bump-version.ps1` for the component
set taken from the commit's own diff, amends, and retries a bounded number of times.

Bump with `scripts/omp/bump-version.ps1`, never by editing `omp-components.json` by hand: the
tool raises `repositoryVersion` for you, and since 2026-09-04
`scripts/omp/validate-component-versions.ps1` fails the build when a component version moved
while `repositoryVersion` stayed put. See `docs/VERSIONING_AND_IDENTITIES.md` for why that
value is the universal package's identity.

### Parallel builds across repositories

**Consumer repositories may build in parallel. Do not serialise them.** This rule was the
opposite until 2026-08-22, and the old text survived here until 2026-09-02 - if you have
read a serialisation rule in this file before, this paragraph replaces it.

The collision the old rule guarded against was real: two sibling repositories building the
same referenced OMP projects wrote to the same `obj`/`bin` folders and hit CS2012 "file
locked by VBCSCompiler". It is now prevented physically rather than by scheduling.
`scripts/local-ci.ps1` in each consumer passes
`-p:OmpIsolatedBuildRoot=<folder inside the consumer repo>`, and
`Directory.Build.props:62-66` redirects `BaseIntermediateOutputPath`, `BaseOutputPath` and
`MSBuildProjectExtensionsPath` under that root, so two consumer builds cannot share output
files for Web.Shared, Web.Shared.Analyzers or EventPublisher.*. When the property is unset
the default in-tree layout applies, so OMP's own builds are unaffected.

The AI Orchestrator dropped its global `build:omp-web-shared` lock in the same change; each
repository still takes its own `repo:<toplevel>` lock, so two implementations in the SAME
repository remain serialised. (The orchestrator lives in the private DEV workspace, not in
this repository: `AI-Orchestrator/src/gui/jobConcurrency.ts` carries the reasoning as a
comment next to the lock list. It is named here for provenance only — it is not a path you
can open from a clone of this repo.)

What still holds:

- Parallel file reads and searches are fine - unchanged.
- Never run two builds in the same repository at the same time.
- If a consumer build is invoked WITHOUT `-p:OmpIsolatedBuildRoot`, the old collision is
  back. Go through `scripts/local-ci.ps1` rather than calling `dotnet build` by hand across
  repositories.
- Publishing and package creation write to a shared runtime root (see below) and are not
  covered by build isolation - keep those sequential.

## Local Runtime Defaults

Default local paths and endpoints:

```text
OpenModulePlatform repo: <workspace>\OpenModulePlatform
Optional consumer repos: <workspace>\<consumer-repo>
Runtime root:            E:\OMP
SQL Server:              localhost
Database:                OpenModulePlatform
Portal URL:              http://localhost:8088/
```

These are local development defaults. Do not hardcode user-specific paths into
reusable scripts unless the task explicitly asks for a local-only script.

## Local Publish Commands

For a full local OpenModulePlatform install or upgrade, use the HostAgent-first
sample installer:

```powershell
.\scripts\deployment\update-installer-runner-only.ps1 -PackageRoot .\installer
.\installer\OpenModulePlatform.Bootstrapper.exe
```

For a publish-only pass:

```powershell
.\publish-all.ps1 -Configuration Release -OutputRoot "E:\OMP\Publish\OMP" -Restore -CleanOutput
```

Use destructive options such as `-DropDatabase`, `-ClearDatabaseObjects`, or
`-RemoveRuntimeFiles` only when explicitly requested.

## SQL Bootstrap Notes

The bootstrap principal is environment-specific. Prefer the HostAgent-first
installer because it patches principal values into a temporary SQL file before
invoking SQL bootstrap logic.

Do not pass principal values through `sqlcmd -v`. SQLCMD variables are textual
substitution before T-SQL parsing, so values containing SQL metacharacters cannot
be safely validated inside the SQL script after substitution.

## Cross-Repository Boundary

OpenModulePlatform may reference consumer repositories only as external
consumers in documentation or local runbooks. Platform code, SQL, examples,
and shared web components must stay neutral.

Machine-specific developer packages, customer bootstrap values, credentials,
and protected payloads belong in a private installation repository. Keep this
repository limited to neutral code, sample configuration, and reusable package
generation.
