# AGENTS.md

## Project workflow

This repository is developed on Windows with Visual Studio, VS Code, PowerShell, .NET, IIS, Windows Services, and SQL Server.

Before making changes:
- Inspect actual files and repository structure first.
- Do not assume file paths, project names, SQL schema, or script behavior.
- Prefer direct file edits over generated shell scripts.
- Keep changes small and reviewable.
- Show a concise summary and git diff after changes.
- Do not mix customer-specific IbsPackager logic into OpenModulePlatform.

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
- Ask before running terminal commands.
- Do not touch registry, startup settings, scheduled tasks, antivirus settings, Windows security settings, or Bitdefender settings.
- If a command is blocked or likely to trigger antivirus heuristics, stop and propose the smallest safe manual alternative.

## Local paths

Default local development paths:

- OpenModulePlatform repo: `E:\Linus Dunkers\Documents\GitHub\OpenModulePlatform`
- IbsPackager repo: `E:\Linus Dunkers\Documents\GitHub\IbsPackager`
- Runtime root: `E:\OMP`
- SQL Server: `localhost`
- Database: `OpenModulePlatform`
- Portal URL: `http://localhost:8088/`
- IbsPackager URL: `http://localhost:8088/ibspackager/`

These paths are local development defaults. Do not hardcode user-specific paths into reusable scripts unless explicitly requested.