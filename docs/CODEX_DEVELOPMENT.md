# Codex Development Guide

This guide is the compact, agent-friendly entry point for development with Codex
from VS Code. Keep it factual and current. Put detailed architecture and product
context in the linked documents, not here.

## Repository Role

OpenModulePlatform is the neutral platform repository. It must not contain
customer-specific IbsPackager behavior, credentials, or deployment assumptions
beyond documented local development defaults.

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
- SQL changes: review idempotency, rerun only when the task explicitly requires local data mutation
- Formatting hygiene: `git diff --check`
- Local web visibility: publish/update the runtime, then verify the relevant localhost URL

Avoid parallel builds that write the same referenced project outputs. Build
OpenModulePlatform first, then dependent repositories such as IbsPackager.

## Local Runtime Defaults

Default local paths and endpoints:

```text
OpenModulePlatform repo: <workspace>\OpenModulePlatform
Optional consumer repos: <workspace>\IbsPackager, <workspace>\OpenDocViewer
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

OpenModulePlatform may reference IbsPackager only as an external consumer in
documentation or local runbooks. Platform code, SQL, examples, and shared web
components must stay neutral.

Machine-specific developer packages, customer bootstrap values, credentials,
and protected payloads belong in a private installation repository. Keep this
repository limited to neutral code, sample configuration, and reusable package
generation.
