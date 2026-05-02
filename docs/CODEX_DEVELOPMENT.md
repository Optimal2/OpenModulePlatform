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
- `docs/ARCHITECTURE.md` - platform model and request flows.
- `docs/ADMIN_CONFIGURATION.md` - manual Portal administration guidance.
- `PUBLISH_README.md` - publish helper behavior and local runtime layout.
- `sql/README.md` - SQL setup and initialization conventions.

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
OpenModulePlatform repo: E:\Linus Dunkers\Documents\GitHub\OpenModulePlatform
IbsPackager repo:        E:\Linus Dunkers\Documents\GitHub\IbsPackager
Runtime root:            E:\OMP
SQL Server:              localhost
Database:                OpenModulePlatform
Portal URL:              http://localhost:8088/
IbsPackager URL:         http://localhost:8088/ibspackager/
```

These are local development defaults. Do not hardcode user-specific paths into
reusable scripts unless the task explicitly asks for a local-only script.

## Local Publish Commands

For a full local OpenModulePlatform update:

```powershell
.\scripts\manage-local-install.ps1 -Action Install -RuntimeRoot "E:\OMP" -SqlServer "localhost" -Database "OpenModulePlatform" -Yes
```

For a publish-only pass:

```powershell
.\publish-all.ps1 -Configuration Release -OutputRoot "E:\OMP\Publish\OMP" -Restore -CleanOutput
```

Use destructive options such as `-DropDatabase`, `-ClearDatabaseObjects`, or
`-RemoveRuntimeFiles` only when explicitly requested.

## SQL Bootstrap Notes

The bootstrap principal is environment-specific. Prefer
`scripts/manage-local-install.ps1 -BootstrapPortalAdminPrincipal` because the
script escapes principal values before invoking `sqlcmd`.

Do not pass principal values through `sqlcmd -v`. SQLCMD variables are textual
substitution before T-SQL parsing, so values containing SQL metacharacters cannot
be safely validated inside the SQL script after substitution.

## Cross-Repository Boundary

OpenModulePlatform may reference IbsPackager only as an external consumer in
documentation or local runbooks. Platform code, SQL, examples, and shared web
components must stay neutral.
