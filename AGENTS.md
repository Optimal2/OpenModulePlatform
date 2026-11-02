# AGENTS.md

## Project workflow

This repository is developed on Windows with Visual Studio, VS Code, PowerShell, .NET, IIS, Windows Services, and SQL Server.

Read `docs/CODEX_DEVELOPMENT.md` for the repository map, validation ladder, and local publish workflow before doing broad changes.

Before making changes:
- Inspect actual files and repository structure first.
- Do not assume file paths, project names, SQL schema, or script behavior.
- Prefer direct file edits over generated shell scripts.
- Keep changes small and reviewable.
- Show a concise summary, validation results, and git diff after changes.
- Do not mix customer-specific consumer-repository logic into OpenModulePlatform.
- Keep code, comments, SQL, scripts, and development documentation in English. Swedish belongs only in application localization resources.
- If a change must be visible in the local IIS/runtime environment, run the matching publish or install script after the code change.
- When a task produces repository changes, validate them, commit with a focused message, and push unless the user asks not to or the worktree contains unrelated user changes.

## Cross-repository builds run in PARALLEL

**Consumer repositories may build at the same time. Do not serialise them.** This section
said the opposite until 2026-09-02; if you have read a serialisation rule here before, this
replaces it.

The collision it guarded against was real - two sibling repositories building the same
referenced OMP projects shared `obj`/`bin` and hit CS2012 "file locked by VBCSCompiler" -
but it is prevented physically now, not by scheduling. Each consumer's
`scripts/local-ci.ps1` passes `-p:OmpIsolatedBuildRoot=<folder inside that repo>`, and
`Directory.Build.props:78-82` redirects the intermediate and output paths under it, so two
consumer builds cannot write the same files. Unset, the default in-tree layout applies, so
builds of this repository are unaffected. The AI Orchestrator removed its global
`build:omp-web-shared` job lock in the same change and now states so explicitly in
`AI-Orchestrator/src/gui/jobConcurrency.ts:119`.

What still holds:

- Parallel file reads and searches are fine - unchanged.
- Never run two builds in the SAME repository at once; each repository still takes its own
  `repo:<toplevel>` lock in the orchestrator.
- A consumer build invoked without `-p:OmpIsolatedBuildRoot` brings the collision back. Go
  through `scripts/local-ci.ps1` rather than calling `dotnet build` by hand across
  repositories.
- `OmpIsolatedBuildRoot` must be an ABSOLUTE path OUTSIDE this repository. A relative value
  is resolved per referenced OMP project and used to write `obj`/`bin` into this repository;
  `Directory.Build.targets` now stops such a build with OMPBUILD001 (relative) or OMPBUILD002
  (inside this repository).
- Publishes and package creation write to a shared runtime root and are NOT covered by build
  isolation - keep those sequential.

Rationale and the fuller version: `docs/CODEX_DEVELOPMENT.md`, "Parallel builds across
repositories".

## Module-definition SQL changes

When a module-definition-owned SQL file changes, the same commit/change must
also:

1. Bump the `definitionVersion` of the affected `.module-definition.json`.
2. Re-run `scripts/dev/embed-module-definition-sql.ps1` so the embedded
   `inlineSql`/`content` and `sha256` reflect the new SQL.
3. Update `minModuleDefinitionVersion` in `omp-components.json` for every
   component that requires the new module contract.

This rule applies to platform-core definitions (for example
`omp_core.module-definition.json`) as well as module-specific definitions.
Never change module SQL and rely on a later artifact import to "pick it up";
artifact import is version-gated and will skip the new SQL unless
`definitionVersion` is newer than the version already applied in the target
database.

## Security / antivirus compatibility

This environment uses Bitdefender on Windows. PowerShell-heavy or suspicious command lines may be blocked.

Follow these rules strictly:
- Do not use encoded PowerShell commands.
- Do not use `-ExecutionPolicy Bypass`.
- Do not run nested PowerShell, for example `pwsh.exe` launching `powershell.exe`.
- Do not generate long inline `-Command` scripts.
- Do not chain many shell commands together.
- Do not use obfuscated scripts or suspicious automation patterns.
- Prefer direct file edits over shell-based search/replace.
- Prefer normal file I/O over shell piping or generated scripts.
- Prefer standard `dotnet`, `git`, `sqlcmd`, `robocopy`, and short explicit commands when command execution is needed.
- If PowerShell is needed, prefer existing `.ps1` files in the repository.
- If a new script is needed, create a readable `.ps1` file in the repository instead of passing a large inline command.
- Run normal inspection, build, test, git, sqlcmd, robocopy, and repository scripts when they are needed for the task. Ask before destructive actions, registry/security changes, database drops, or broad service/runtime resets.
- Do not touch registry, startup settings, scheduled tasks, antivirus settings, Windows security settings, or Bitdefender settings.
- If a command is blocked or likely to trigger antivirus heuristics, stop and propose the smallest safe manual alternative.

## Version bumping

The canonical version-bumping script is `scripts/omp/bump-version.ps1`. It
updates repositoryVersion, component versions, module-definition
versions, compatibleArtifacts.maxVersion, dashboard widget versions, and
supports cascade-bumps from shared projects. Use it for all routine bumps:

```powershell
.\scripts\omp\bump-version.ps1 -ComponentKey omp-auth-web
.\scripts\omp\bump-version.ps1 -AllComponents
.\scripts\omp\bump-version.ps1 -ModuleKey omp_auth -UpdateModuleMinimums
```

`scripts/bump-component-version.ps1` is deprecated and kept only for backward
compatibility. It bumps component versions but does not bump repositoryVersion,
module-definition versions, or compatibleArtifacts.maxVersion, so a normal run
leaves the repository in a half-bumped state. Do not use it for releases.

## Local paths

Default local development paths:

- `<workspace>` means the local parent folder where these sibling repositories are cloned.
- OpenModulePlatform repo: `<workspace>\OpenModulePlatform`
- Optional consumer repos: `<workspace>\<consumer-repo>`
- Runtime root: `G:\OMP` (moved from `E:\OMP` on 2026-09-06; `E:\OMP` remains as a junction to the same directory, so older paths still resolve — write `G:\OMP` in new material)
- SQL Server: `localhost`
- Database: `OpenModulePlatform`
- Portal URL: `http://localhost:8088/`

These paths are local development defaults. Do not hardcode user-specific paths into reusable scripts unless explicitly requested.

## Run everything headless — no visible windows

Agents work on the operator's own workstation. A console window that pops up in the middle of
their work steals focus and can swallow keystrokes meant for something else, so everything an
agent starts runs hidden and never waits for input.

- Never start a visible console window. Scripts, builds, tests and helper processes run hidden —
  both what you start directly and what your scripts start in turn.
- Python: background and scheduled scripts use `pythonw.exe`, or
  `subprocess.Popen(..., creationflags=subprocess.CREATE_NO_WINDOW)` when a process without its
  own console starts children. Never use `os.startfile` or the `start` command to run a script.
- PowerShell: `pwsh -NoProfile -NonInteractive -WindowStyle Hidden -File …`. Never use
  `Start-Process` without `-WindowStyle Hidden` or `-NoNewWindow`, and never `cmd /c start …`.
- Scheduled tasks start through `conhost.exe --headless <program> …` or through `wscript.exe`
  with a `.vbs` that runs hidden. Never register a task without one of those patterns.
- Node: `child_process.spawn/exec` with `windowsHide: true`.
- Never wait for input: no `Read-Host`, `pause`, `input()` or interactive flags (`-i`). Set
  `GIT_TERMINAL_PROMPT=0` and run non-interactively (`--yes`, `-NonInteractive`, `--no-input`
  where the tool has it). Never run a command that can prompt without someone watching it.
- Browsers and GUI programs start only when the task needs them, never as a side effect of a
  script.
- If a window you caused appears, fix how it is started in the same piece of work and say so in
  your report. If it comes from a tool we do not control, report it so the operator can raise it
  with the vendor.
