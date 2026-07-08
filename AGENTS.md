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

## Cross-repository build sequencing

When working across more than one repository, never run builds, publishes, or
package creation in parallel if more than one command can build shared OMP
projects such as `OpenModulePlatform.Web.Shared`. This applies especially to
OMP web projects and dependent consumer repositories that build shared OMP
projects.

Parallel file reads and searches are fine. Build/publish/package work must be
sequential: build OpenModulePlatform first when shared platform projects may be
involved, then build one dependent repository at a time.

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

## Local paths

Default local development paths:

- `<workspace>` means the local parent folder where these sibling repositories are cloned.
- OpenModulePlatform repo: `<workspace>\OpenModulePlatform`
- Optional consumer repos: `<workspace>\<consumer-repo>`
- Runtime root: `E:\OMP`
- SQL Server: `localhost`
- Database: `OpenModulePlatform`
- Portal URL: `http://localhost:8088/`

These paths are local development defaults. Do not hardcode user-specific paths into reusable scripts unless explicitly requested.
