# Code Style and Compiler Strictness Conventions

This document maps the current code-style and compiler-strictness settings across the OMP+ODV ecosystem. It is based on a read-only audit of the repositories; no source files were modified.

## Scope and method

- **Audited repositories:** 10 sibling repos under `E:\Linus Dunkers\Documents\GitHub`.
  - .NET: `OpenModulePlatform`, `IbsPackager`, `LogSearch`, `EArkivChecker`, `Dokumentbibliotek`, `VajSkrivare`, `iKrock2`, `ODVGateway`
  - JS/npm: `OpenDocViewer`, `AgentDocMap`
- **Excluded paths:** `bin/`, `obj/`, `artifacts/`, `node_modules/`, `dist/`, `.git/`, `.vs/`.
- **Citations:** `file:line` references are relative to each repository root unless an absolute path is given.

## Step 1 — Per-repo code style map

### OpenModulePlatform (.NET)

| Area | Finding |
|------|---------|
| `.editorconfig` | Yes. `root = true` at `.editorconfig:1`. Universal `[*]` block: `charset = utf-8` (`:4`), `end_of_line = lf` (`:5`), `insert_final_newline = true` (`:6`), `trim_trailing_whitespace = true` (`:7`), `indent_style = space` (`:8`), `indent_size = 4` (`:9`). Markdown override at `:11-12`, JSON/YAML 2-space indent at `:14-15`, SQL 4-space indent at `:17-18`. No naming conventions or diagnostic severities are configured. |
| Nullable | Enabled in every `.csproj` inspected, e.g. `OpenModulePlatform.Artifacts/OpenModulePlatform.Artifacts.csproj:4`. No project disables it. |
| `TreatWarningsAsErrors` | **Not enabled** anywhere. |
| `LangVersion` / `TargetFramework` | Most projects do not set `<LangVersion>` (default for `net10.0`). Exception: `OpenModulePlatform.Web.Shared.Analyzers/OpenModulePlatform.Web.Shared.Analyzers.csproj:5` sets `<LangVersion>9.0</LangVersion>`. Most targets are `net10.0`; `OpenModulePlatform.Bootstrapper` uses `net10.0-windows`; the analyzer project targets `netstandard2.0`. SDK pinned in `global.json:3` to `10.0.200`. |
| Analyzers | No third-party Roslyn analyzer packages. Custom OMP Web.Shared analyzer defines `OMPWEB001` and `OMPWEB002` (`OpenModulePlatform.Web.Shared.Analyzers/OmpWebDefaultsAnalyzer.cs:26-27`, rules `:38-56`). It is auto-injected for all `Microsoft.NET.Sdk.Web` projects via `Directory.Build.targets:9-14`. `OpenModulePlatform.Auth` is skipped at the analyzer level (`OmpWebDefaultsAnalyzer.cs:175-177`). |
| `Directory.Build.props` | Yes. Sets version/company metadata (`Directory.Build.props:3-11`). Does **not** set `LangVersion`, `Nullable`, `TreatWarningsAsErrors`, `ImplicitUsings`, `AnalysisLevel`, or `EnforceCodeStyleInBuild`. |
| `Directory.Packages.props` | Yes. Central Package Management enabled (`Directory.Packages.props:3`). |

### IbsPackager (.NET)

| Area | Finding |
|------|---------|
| `.editorconfig` | **Absent**. |
| Nullable | Enabled per project, e.g. `IbsPackager.Abstractions/IbsPackager.Abstractions.csproj:4`. |
| `TreatWarningsAsErrors` | **Not enabled**. |
| `LangVersion` / `TargetFramework` | `<LangVersion>` not set. `<TargetFramework>net10.0</TargetFramework>` in every project. SDK pinned in `global.json:3` to `10.0.200`. |
| Analyzers | No NuGet analyzer packages. OMP Web.Shared analyzer hooked for web projects in `Directory.Build.targets:13-18`. |
| `Directory.Build.props` | Yes. Version/company metadata only (`Directory.Build.props:3-11`). |
| `Directory.Packages.props` | Yes. CPM enabled (`Directory.Packages.props:3`). |

### LogSearch (.NET)

| Area | Finding |
|------|---------|
| `.editorconfig` | **Absent**. |
| Nullable | Enabled repo-wide in `Directory.Build.props:4`. |
| `TreatWarningsAsErrors` | **Disabled** repo-wide: `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` at `Directory.Build.props:6`. |
| `LangVersion` / `TargetFramework` | `<LangVersion>` not set. `<TargetFramework>net10.0</TargetFramework>` at `Directory.Build.props:3`. |
| Analyzers | No NuGet analyzer packages. OMP Web.Shared analyzer for web projects in `Directory.Build.targets:11-16`. |
| `Directory.Build.props` | Yes. Sets `TargetFramework`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors` (`Directory.Build.props:3-6`). |
| `Directory.Packages.props` | Yes. CPM enabled (`Directory.Packages.props:3`). |

### EArkivChecker (.NET)

| Area | Finding |
|------|---------|
| `.editorconfig` | **Absent**. |
| Nullable | Enabled repo-wide in `Directory.Build.props:4`. |
| `TreatWarningsAsErrors` | **Disabled** repo-wide: `Directory.Build.props:6`. |
| `LangVersion` / `TargetFramework` | `<LangVersion>` not set. `<TargetFramework>net10.0</TargetFramework>` at `Directory.Build.props:3`. |
| Analyzers | No NuGet analyzer packages. OMP Web.Shared analyzer for web projects in `Directory.Build.targets:11-16`. |
| `Directory.Build.props` | Yes. Sets `TargetFramework`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors` (`Directory.Build.props:3-6`). |
| `Directory.Packages.props` | Yes. CPM enabled (`Directory.Packages.props:3`). |

### Dokumentbibliotek (.NET)

| Area | Finding |
|------|---------|
| `.editorconfig` | **Absent**. |
| Nullable | Enabled in the single project: `RazorPages/OpenModulePlatform.Web.eArkivDokumentbibliotek.RazorPages.csproj:4`. |
| `TreatWarningsAsErrors` | **Not enabled**. |
| `LangVersion` / `TargetFramework` | `<LangVersion>` not set. `<TargetFramework>net10.0</TargetFramework>` at `RazorPages/...RazorPages.csproj:3`. |
| Analyzers | No NuGet analyzer packages. OMP Web.Shared analyzer hooked in `Directory.Build.targets:11-16`. |
| `Directory.Build.props` | **Absent**. |
| `Directory.Packages.props` | **Absent**. |

### VajSkrivare (.NET)

| Area | Finding |
|------|---------|
| `.editorconfig` | Yes. `root = true` at `.editorconfig:1`. Universal `[*]`: `charset = utf-8` (`:4`), `end_of_line = crlf` (`:5`), `insert_final_newline = true` (`:6`), `indent_style = space` (`:7`), `indent_size = 4` (`:8`), `trim_trailing_whitespace = true` (`:9`). C#/cshtml 4-space at `:11-12`, JSON/Markdown/YAML 2-space at `:14-15`. No naming conventions or diagnostic severities. |
| Nullable | Enabled repo-wide in `Directory.Build.props:3` and repeated in each `.csproj`. |
| `TreatWarningsAsErrors` | **Disabled** repo-wide: `Directory.Build.props:6`. |
| `LangVersion` / `TargetFramework` | `<LangVersion>latest</LangVersion>` at `Directory.Build.props:7`. `<TargetFramework>net10.0</TargetFramework>` in each `.csproj`. |
| Analyzers | No NuGet analyzer packages. OMP Web.Shared analyzer hooked in `Directory.Build.targets:11-16`. |
| `Directory.Build.props` | Yes. Sets `Nullable`, `ImplicitUsings`, `AnalysisLevel`, `TreatWarningsAsErrors`, `LangVersion` (`Directory.Build.props:3-7`). |
| `Directory.Packages.props` | **Absent**. |

### iKrock2 (.NET)

| Area | Finding |
|------|---------|
| `.editorconfig` | **Absent**. |
| Nullable | Enabled per project, e.g. `iKrock2.Application/iKrock2.Application.csproj:4`. |
| `TreatWarningsAsErrors` | **Not enabled**. |
| `LangVersion` / `TargetFramework` | `<LangVersion>` not set. `<TargetFramework>net10.0</TargetFramework>` in every project. SDK pinned in `global.json:3` to `10.0.200`. |
| Analyzers | **No analyzers at all** — no OMP Web.Shared hook, no NuGet analyzer packages. |
| `Directory.Build.props` | Yes. Version/company metadata only (`Directory.Build.props:3-11`). |
| `Directory.Packages.props` | Yes. CPM enabled (`Directory.Packages.props:3`). |

### ODVGateway (.NET)

| Area | Finding |
|------|---------|
| `.editorconfig` | Yes. `root = true` at `.editorconfig:1`. Universal `[*]`: `charset = utf-8` (`:4`), `end_of_line = crlf` (`:5`), `insert_final_newline = true` (`:6`), `indent_style = space` (`:7`), `indent_size = 4` (`:8`). JSON/Markdown/YAML 2-space at `:10-11`, C# 4-space at `:13-14`. No naming conventions or diagnostic severities. |
| Nullable | Enabled in `src/ODVGateway/ODVGateway.csproj:4`. |
| `TreatWarningsAsErrors` | **Disabled** repo-wide: `Directory.Build.props:3`. |
| `LangVersion` / `TargetFramework` | `<LangVersion>` not set. `<TargetFramework>net10.0</TargetFramework>` at `src/ODVGateway/ODVGateway.csproj:3`. SDK pinned in `global.json:3` to `10.0.300`. |
| Analyzers | Built-in SDK analyzers enabled via `<AnalysisLevel>latest</AnalysisLevel>` (`Directory.Build.props:4`) and `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>` (`Directory.Build.props:5`). No NuGet analyzer packages and no OMP Web.Shared hook. |
| `Directory.Build.props` | Yes. Sets `TreatWarningsAsErrors`, `AnalysisLevel`, `EnforceCodeStyleInBuild` (`Directory.Build.props:3-5`). |
| `Directory.Packages.props` | **Absent**. |

### OpenDocViewer (JS/npm)

| Area | Finding |
|------|---------|
| `.editorconfig` | **Absent**. |
| ESLint | Yes. Flat-config ESM file `eslint.config.js:1`. Applies `@eslint/js` recommended rules (`eslint.config.js:32`, `:62`), `react-hooks/rules-of-hooks` as `error` (`:33`), `react-hooks/exhaustive-deps` as `warn` (`:34`), `react-refresh/only-export-components` as `warn` (`:35`), custom `no-unused-vars` (`:36-40`) and `no-empty` (`:41`) rules. Ignores `dist` and `docs` (`:11`). Separate test-file block (`:45-70`). |
| Prettier | **No config file**. `prettier` is a dev dependency at `package.json:89` and used by the `format` script at `package.json:34`. |
| TypeScript / `tsconfig.json` | **Absent**. The project is JavaScript-only. |
| Scripts | `lint` at `package.json:32`, `lint:fix` at `package.json:33`, `format` at `package.json:34`. |

### AgentDocMap (JS/npm)

| Area | Finding |
|------|---------|
| `.editorconfig` | **Absent**. |
| ESLint | **Absent**. |
| Prettier | **Absent**. |
| TypeScript / `tsconfig.json` | **Absent**. |
| Scripts | No lint/format scripts. `test` at `package.json:16`, `validate` at `package.json:17`. |

## Step 2 — Comparison table + divergences

| Repository | `.editorconfig` | Nullable | `TreatWarningsAsErrors` | `LangVersion` | Analyzers | CPM | `Directory.Build.props` | ESLint | Prettier | TS strict |
|---|---|---|---|---|---|---|---|---|---|---|
| OpenModulePlatform | Yes | enable | **⚠️ Off** | default | OMPWEB only | **Yes** | Yes | — | — | — |
| IbsPackager | **⚠️ No** | enable | **⚠️ Off** | default | OMPWEB only | **Yes** | Yes | — | — | — |
| LogSearch | **⚠️ No** | enable | **⚠️ false** | default | OMPWEB only | **Yes** | Yes | — | — | — |
| EArkivChecker | **⚠️ No** | enable | **⚠️ false** | default | OMPWEB only | **Yes** | Yes | — | — | — |
| Dokumentbibliotek | **⚠️ No** | enable | **⚠️ Off** | default | OMPWEB only | **⚠️ No** | **⚠️ No** | — | — | — |
| VajSkrivare | Yes | enable | **⚠️ false** | latest | OMPWEB only | **⚠️ No** | Yes | — | — | — |
| iKrock2 | **⚠️ No** | enable | **⚠️ Off** | default | **⚠️ None** | **Yes** | Yes | — | — | — |
| ODVGateway | Yes | enable | **⚠️ false** | default | SDK + EditorConfig | **⚠️ No** | Yes | — | — | — |
| OpenDocViewer | **⚠️ No** | — | — | — | — | — | — | Yes | **⚠️ No config** | **⚠️ No TS** |
| AgentDocMap | **⚠️ No** | — | — | — | — | — | — | **⚠️ No** | **⚠️ No** | **⚠️ No TS** |

**Key divergences**

1. **`.editorconfig` missing** in `IbsPackager`, `LogSearch`, `EArkivChecker`, `Dokumentbibliotek`, `iKrock2`, `OpenDocViewer`, and `AgentDocMap`.
2. **`TreatWarningsAsErrors` is disabled or unset** in every .NET repository.
3. **`LangVersion` is not pinned to `latest`** in most .NET repos; several rely on the `net10.0` default.
4. **Analyzer coverage is minimal**: only OpenModulePlatform, IbsPackager, LogSearch, EArkivChecker, Dokumentbibliotek, and VajSkrivare use the OMP Web.Shared analyzer; no repo uses `StyleCop.Analyzers` or `Microsoft.CodeAnalysis.NetAnalyzers`; `iKrock2` has no analyzers at all.
5. **Central Package Management missing** in `Dokumentbibliotek`, `VajSkrivare`, and `ODVGateway`.
6. **`Directory.Build.props` missing** in `Dokumentbibliotek`.
7. **JS tooling is uneven**: `OpenDocViewer` has ESLint but no Prettier config and no TypeScript; `AgentDocMap` has no style or compiler tooling at all.

## Step 3 — Recommended standard pattern

### .NET standard

Every .NET repository should adopt a single, repo-level standard:

1. `.editorconfig` at the repository root with:
   - `root = true`
   - `charset = utf-8`, `end_of_line = lf`, `insert_final_newline = true`, `trim_trailing_whitespace = true`
   - `indent_style = space`, `indent_size = 4` (C#), `indent_size = 2` (JSON/YAML/Markdown)
   - `dotnet_naming_*` conventions and `dotnet_diagnostic.*` severities where appropriate.
2. `Directory.Build.props` that sets:
   - `<Nullable>enable</Nullable>`
   - `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
   - `<LangVersion>latest</LangVersion>`
   - `<ImplicitUsings>enable</ImplicitUsings>`
   - `<AnalysisLevel>latest</AnalysisLevel>`
   - `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`
   - Shared versioning/company metadata.
3. `Directory.Packages.props` with Central Package Management enabled (`<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`).
4. OMP Web.Shared analyzer (`OMPWEB001`/`OMPWEB002`) for every ASP.NET Core web project.
5. Optional but recommended: add `Microsoft.CodeAnalysis.NetAnalyzers` and/or `StyleCop.Analyzers` via CPM for broader static analysis.

**Motivation:** This pattern is already partially in place across the ecosystem, uses existing infrastructure (CPM, Directory.Build.props, the OMP Web.Shared analyzer), and gives the strongest consistent guarantees with the fewest per-project overrides.

### JS/npm standard

Every JS/npm repository should adopt:

1. `.editorconfig` aligned with the .NET standard.
2. ESLint with a checked-in config (flat config preferred).
3. Prettier with a checked-in config (`.prettierrc.json` or `prettier.config.js`).
4. For TypeScript projects: `tsconfig.json` with `"strict": true`.
5. For JavaScript projects: consider adding `// @ts-check` or JSDoc-based type checking, and at minimum `eslint` rules that catch unused variables and common errors.
6. `package.json` scripts for `lint`, `lint:fix`, and `format`.

## Step 4 — Migration notes per diverging repo

### OpenModulePlatform
- **Current state:** Has `.editorconfig`, CPM, Directory.Build.props, and OMPWEB analyzer. Nullable enabled. Warnings are not errors, `LangVersion` is not pinned, and `.editorconfig` lacks naming conventions.
- **Migration:** Add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and `<LangVersion>latest</LangVersion>` to `Directory.Build.props`; extend `.editorconfig` with naming conventions and diagnostic severities; optionally add `Microsoft.CodeAnalysis.NetAnalyzers`.
- **Priority:** Medium.

### IbsPackager
- **Current state:** No `.editorconfig`, CPM in place, OMPWEB analyzer for web, no `TreatWarningsAsErrors`, `LangVersion` not pinned.
- **Migration:** Add root `.editorconfig`; move nullable/TreatWarningsAsErrors/LangVersion into `Directory.Build.props`; enable warnings-as-errors.
- **Priority:** High (missing `.editorconfig`).

### LogSearch
- **Current state:** No `.editorconfig`, CPM in place, OMPWEB analyzer, `TreatWarningsAsErrors=false`, `LangVersion` not pinned.
- **Migration:** Add root `.editorconfig`; flip `TreatWarningsAsErrors` to `true`; add `<LangVersion>latest</LangVersion>`; consider adding naming conventions.
- **Priority:** High.

### EArkivChecker
- **Current state:** Same pattern as LogSearch: no `.editorconfig`, `TreatWarningsAsErrors=false`, default LangVersion.
- **Migration:** Add root `.editorconfig`; enable warnings-as-errors; pin `LangVersion` to `latest`.
- **Priority:** High.

### Dokumentbibliotek
- **Current state:** No `.editorconfig`, no `Directory.Build.props`, no CPM, single project sets nullable/implicit usings, `TreatWarningsAsErrors` off.
- **Migration:** Create `Directory.Build.props` with the full .NET standard; create `Directory.Packages.props` for CPM; add root `.editorconfig`; migrate package versions into CPM.
- **Priority:** High (largest gap).

### VajSkrivare
- **Current state:** Has `.editorconfig` and `Directory.Build.props` with `LangVersion=latest` and `AnalysisLevel=latest`, but `TreatWarningsAsErrors=false` and no CPM.
- **Migration:** Enable `TreatWarningsAsErrors`; add `Directory.Packages.props` and migrate to CPM; extend `.editorconfig` with naming conventions.
- **Priority:** Medium.

### iKrock2
- **Current state:** No `.editorconfig`, CPM in place, no analyzers at all, `TreatWarningsAsErrors` off, `LangVersion` not pinned.
- **Migration:** Add root `.editorconfig`; add OMPWEB analyzer hook (it has a web project referencing `OpenModulePlatform.Web.Shared`); enable warnings-as-errors; pin `LangVersion`; optionally add NetAnalyzers/StyleCop.
- **Priority:** High (no analyzers and no `.editorconfig`).

### ODVGateway
- **Current state:** Has `.editorconfig`, `Directory.Build.props` with `AnalysisLevel=latest` and `EnforceCodeStyleInBuild=true`, but `TreatWarningsAsErrors=false`, no CPM.
- **Migration:** Enable `TreatWarningsAsErrors`; add `Directory.Packages.props` with CPM; extend `.editorconfig` with naming conventions; optionally add OMPWEB analyzer if it references `OpenModulePlatform.Web.Shared` in the future.
- **Priority:** Medium.

### OpenDocViewer
- **Current state:** ESLint is configured, but no `.editorconfig`, no Prettier config, no TypeScript/`tsconfig.json`.
- **Migration:** Add `.editorconfig`; add `.prettierrc.json` and align it with existing formatting habits; consider introducing TypeScript with `strict: true` or JSDoc-based type checking; add `format:check` script.
- **Priority:** Medium.

### AgentDocMap
- **Current state:** No `.editorconfig`, no ESLint, no Prettier, no TypeScript, no lint/format scripts.
- **Migration:** Add `.editorconfig`, ESLint config, Prettier config, and lint/format scripts; consider adding a minimal `tsconfig.json` with `checkJs`/`strict` for JSDoc-based type checking.
- **Priority:** Medium.
