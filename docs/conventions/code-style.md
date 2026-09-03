# Cross-repository code-style audit

Audit of the ten repositories in the `omp-odv` campaign. Only documentation files were inspected; no source code was changed.

> **Re-measured 2026-09-04 (board-prep).** This is a point-in-time audit and it had drifted:
> Dokumentbibliotek and VajSkrivare have since adopted CPM, Dokumentbibliotek gained an
> `.editorconfig`, a `Directory.Build.props` and a `global.json`, and LogSearch/EArkivChecker moved
> off the preview SDK. Every row below was re-verified against the working trees on this date and
> the corrected values are in place. Treat the file:line anchors as indicative -- they age faster
> than the facts they point at.

## Scope

| Repository | Path | Type |
|------------|------|------|
| OpenModulePlatform | `<workspace>\OpenModulePlatform` | .NET |
| IbsPackager | `<workspace>\IbsPackager` | .NET |
| LogSearch | `<workspace>\LogSearch` | .NET |
| EArkivChecker | `<workspace>\EArkivChecker` | .NET |
| Dokumentbibliotek | `<workspace>\Dokumentbibliotek` | .NET |
| VajSkrivare | `<workspace>\VajSkrivare` | .NET |
| iKrock2 | `<workspace>\iKrock2` | .NET |
| ODVGateway | `<workspace>\ODVGateway` | .NET |
| OpenDocViewer | `<workspace>\OpenDocViewer` | JS/npm |
| AgentDocMap | `<workspace>\AgentDocMap` | JS/npm |

Excluded directories: `bin/`, `obj/`, `artifacts/`, `node_modules/`, `dist/`.

## Per-repo findings

### OpenModulePlatform

- **`.editorconfig`** — present (`:1`). `root = true` (`:1`), `charset = utf-8` (`:4`), `end_of_line = lf` (`:5`), `insert_final_newline = true` (`:6`), `trim_trailing_whitespace = true` (`:7`), `indent_style = space` / `indent_size = 4` (`:8-9`), JSON/YAML/YML use 2 spaces (`:14-15`), SQL 4 spaces (`:17-18`).
- **`Directory.Build.props`** — present, but only contains version/company metadata (`:3-12`). Does **not** set `Nullable`, `TreatWarningsAsErrors`, `LangVersion`, or `AnalysisLevel`.
- **`Directory.Packages.props`** — present, CPM enabled (`:3`).
- **`global.json`** — pins SDK `10.0.400` with `rollForward: latestFeature` (`:3-4`).
- **`.csproj`** — all projects target `net10.0` and set `Nullable`/`ImplicitUsings` to `enable` (e.g. `OpenModulePlatform.Portal.csproj:3-5`). Exception: `OpenModulePlatform.Web.Shared.Analyzers.csproj` targets `netstandard2.0` (`:4`), pins `LangVersion` to `9.0` (`:5`), and disables implicit usings (`:7`). Analyzer project uses `Microsoft.CodeAnalysis.Analyzers` (`:15`) and `EnforceExtendedAnalyzerRules` (`:9`). No repo-wide `TreatWarningsAsErrors` or `AnalysisLevel`.

### IbsPackager

- **`.editorconfig`** — missing.
- **`Directory.Build.props`** — present, only version/company metadata (`:3-12`).
- **`Directory.Packages.props`** — present, CPM enabled (`:3`).
- **`global.json`** — pins SDK `10.0.200` (`:3-4`).
- **`.csproj`** — all projects target `net10.0` with `Nullable`/`ImplicitUsings` enabled (e.g. `IbsPackager.Web.csproj:3-5`). No analyzer references, no `TreatWarningsAsErrors`, no `AnalysisLevel`.

### LogSearch

- **`.editorconfig`** — missing.
- **`Directory.Build.props`** — present. Sets `TargetFramework` `net10.0` (`:3`), `Nullable` `enable` (`:4`), `ImplicitUsings` `enable` (`:5`), and explicitly `TreatWarningsAsErrors` `false` (`:6`). Does not set `LangVersion` or `AnalysisLevel`.
- **`Directory.Packages.props`** — present, CPM enabled (`:3`).
- **`global.json`** — pins SDK `10.0.302` with `rollForward: latestFeature`. (Was the preview
  build `10.0.100-preview.4.25258.110` at the original audit; corrected 2026-09-04.)
- **`.csproj`** — no project-level overrides found; all inherit from `Directory.Build.props`.

### EArkivChecker

- **`.editorconfig`** — missing.
- **`Directory.Build.props`** — identical shape to LogSearch: `TargetFramework` `net10.0` (`:3`), `Nullable` `enable` (`:4`), `ImplicitUsings` `enable` (`:5`), `TreatWarningsAsErrors` `false` (`:6`).
- **`Directory.Packages.props`** — present, CPM enabled (`:3`).
- **`global.json`** — pins SDK `10.0.302` with `rollForward: latestFeature`, same as LogSearch.
  (Both were on that preview build at the original audit; corrected 2026-09-04.)
- **`.csproj`** — no project-level overrides found.

### Dokumentbibliotek

- **`.editorconfig`** — present. `root = true`, `charset = utf-8`, `end_of_line = crlf`,
  `insert_final_newline = true`, `trim_trailing_whitespace = true`, space indent 4.
- **`Directory.Build.props`** — present. Sets `TargetFramework` `net10.0`, `Nullable` `enable`,
  `ImplicitUsings` `enable`, `LangVersion` `latest`, `AnalysisLevel` `latest`,
  `TreatWarningsAsErrors` `false`, plus the shared
  `IncludeSourceRevisionInInformationalVersion` `false` block that keeps the git commit out of
  assembly bytes (OMP artifact identity is manifest version + payload SHA-256, so an embedded
  commit made every build look like new artifact content).
- **`Directory.Packages.props`** — present, CPM enabled.
- **`global.json`** — pins SDK `10.0.302` with `rollForward: latestFeature`.
- **`.csproj`** — single project `OpenModulePlatform.Web.eArkivDokumentbibliotek.RazorPages.csproj`,
  now inheriting framework and analysis settings from `Directory.Build.props` and package versions
  from CPM. References `OpenModulePlatform.Web.Shared`.

  (All five bullets above read "missing" in the original audit. Corrected 2026-09-04.)

### VajSkrivare

- **`.editorconfig`** — present (`:1`). `root = true` (`:1`), `charset = utf-8` (`:4`), `end_of_line = crlf` (`:5`), `insert_final_newline = true` (`:6`), space indent 4 (`:7-8`, `:12`), JSON/MD/YAML 2 spaces (`:14`).
- **`Directory.Build.props`** — present. Sets `Nullable` `enable` (`:3`), `ImplicitUsings` `enable` (`:4`), `AnalysisLevel` `latest` (`:5`), `TreatWarningsAsErrors` `false` (`:6`), `LangVersion` `latest` (`:7`). Does **not** set `EnforceCodeStyleInBuild`.
- **`Directory.Packages.props`** — present, CPM enabled. (Read "missing" in the original audit;
  corrected 2026-09-04.)
- **`global.json`** — missing. VajSkrivare is the only .NET repository in the family without an SDK
  pin, so its build floats to whatever SDK the host has installed.
- **`.csproj`** — `src/Skrivarkoppling.Web/Skrivarkoppling.Web.csproj` targets `net10.0`;
  its `PackageReference` entries are **versionless** and resolved through CPM. The test projects
  `tests/Skrivarkoppling.Web.Tests` and `tests/Skrivarkoppling.Web.UiTests` are likewise versionless;
  the central versions are `Microsoft.NET.Test.Sdk` `18.9.0`, `xunit` `2.9.3`,
  `xunit.runner.visualstudio` `4.0.0` and `Microsoft.Playwright` `1.62.0` — in line with the rest of
  the family. (The original audit reported pinned `17.14.0`/`2.8.2` versions here; that predates CPM
  adoption. Corrected 2026-09-04.)

### iKrock2

- **`.editorconfig`** — missing.
- **`Directory.Build.props`** — present, only version/company metadata (`:3-12`).
- **`Directory.Packages.props`** — present, CPM enabled (`:3`).
- **`global.json`** — pins SDK `10.0.200` (`:3-4`).
- **`.csproj`** — all projects target `net10.0` with `Nullable`/`ImplicitUsings` enabled (e.g. `iKrock2.Web.csproj:3-5`). No analyzer settings, no `TreatWarningsAsErrors`, no `AnalysisLevel`.

### ODVGateway

- **`.editorconfig`** — present (`:1`). `root = true` (`:1`), `charset = utf-8` (`:4`), `end_of_line = crlf` (`:5`), `insert_final_newline = true` (`:6`), space indent 4 (`:7-8`), JSON/MD/YAML 2 spaces (`:10`).
- **`Directory.Build.props`** — present. Sets `TreatWarningsAsErrors` `false` (`:3`), `AnalysisLevel` `latest` (`:4`), and `EnforceCodeStyleInBuild` `true` (`:5`). Does **not** set `Nullable`, `ImplicitUsings`, or `LangVersion`.
- **`Directory.Packages.props`** — missing (CPM not enabled).
- **`global.json`** — pins SDK `10.0.300` (`:3-4`).
- **`.csproj`** — `ODVGateway.csproj` targets `net10.0` (`:3`), `Nullable`/`ImplicitUsings` enabled (`:4-5`), and includes package metadata (`:8-12`). Inline `PackageReference` versions.

### OpenDocViewer

- **`.editorconfig`** — missing.
- **ESLint** — `eslint.config.js` present, flat-config ESM (`:10`). Lints `src/**/*.{js,jsx}` (`:13`), ignores `dist`/`docs` (`:11`). Uses `@eslint/js` recommended rules, `react-hooks`, `react-refresh`. Test files have a separate override (`:45`).
- **Prettier** — no `.prettierrc*` file found. Prettier is a devDependency (`package.json:89`) and a `format` script exists (`package.json:34`), but configuration is implicit.
- **TypeScript** — no `tsconfig.json` found; project is plain JS/JSX.
- **`package.json`** — `type: "module"` (`:7`), Node engines `^22.18.0 || >=24.11.0` (`:26`), scripts `lint`/`lint:fix`/`format` (`:32-34`).

### AgentDocMap

- **`.editorconfig`** — missing.
- **ESLint** — no `eslint.config.*` or `.eslintrc.*` found.
- **Prettier** — no `.prettierrc*` found.
- **TypeScript** — no `tsconfig.json` found; project is plain JS (uses `@babel/parser` for parsing other code).
- **`package.json`** — `type: "module"` (`:7`), Node engine `>=22.18.0` (`:12`), scripts `test` and `validate` (`:16-17`). No lint/format scripts.

## Comparison table

| Repository | `.editorconfig` | CPM | `Nullable` | `ImplicitUsings` | `TreatWarningsAsErrors` | `LangVersion` | `AnalysisLevel` | `EnforceCodeStyleInBuild` | Analyzers | `global.json` | JS lint | JS format | TS config |
|------------|-----------------|-----|------------|------------------|--------------------------|---------------|-----------------|---------------------------|-----------|---------------|---------|-----------|-----------|
| OpenModulePlatform | yes (LF) | yes | enable | enable | — | default* | — | — | analyzer project only | 10.0.400 | — | — | — |
| IbsPackager | no | yes | enable | enable | — | default | — | — | none | 10.0.200 | — | — | — |
| LogSearch | no | yes | enable | enable | false | default | — | — | none | 10.0.302 | — | — | — |
| EArkivChecker | no | yes | enable | enable | false | default | — | — | none | 10.0.302 | — | — | — |
| Dokumentbibliotek | yes (CRLF) | yes | enable | enable | false | latest | latest | — | none | 10.0.302 | — | — | — |
| VajSkrivare | yes (CRLF) | yes | enable | enable | false | latest | latest | — | none | none | — | — | — |
| iKrock2 | no | yes | enable | enable | — | default | — | — | none | 10.0.200 | — | — | — |
| ODVGateway | yes (CRLF) | no | enable | enable | false | default | latest | true | none | 10.0.300 | — | — | — |
| OpenDocViewer | no | — | — | — | — | — | — | — | — | — | ESLint flat | implicit Prettier | none |
| AgentDocMap | no | — | — | — | — | — | — | — | — | — | none | none | none |

\* `OpenModulePlatform.Web.Shared.Analyzers.csproj` pins `LangVersion` to `9.0` because it targets `netstandard2.0`.

## Key divergences

1. **`.editorconfig` coverage** — OpenModulePlatform, VajSkrivare, ODVGateway and Dokumentbibliotek have one. IbsPackager, LogSearch, EArkivChecker, iKrock2 and both JS repos lack it.
2. **Line endings** — OpenModulePlatform enforces LF; VajSkrivare and ODVGateway enforce CRLF. The rest have no rule.
3. **Central Package Management** — Enabled in seven of the eight .NET repositories: OpenModulePlatform, IbsPackager, LogSearch, EArkivChecker, iKrock2, Dokumentbibliotek and VajSkrivare. **ODVGateway is the only one without it**, which is also the mechanism behind its package-pin drift: with no `Directory.Packages.props` there is no single place to lift a version.
4. **Warning/error handling** — LogSearch, EArkivChecker, VajSkrivare, ODVGateway and Dokumentbibliotek explicitly set `TreatWarningsAsErrors` to `false`. OpenModulePlatform, IbsPackager and iKrock2 do not set it at all. None treat warnings as errors.
5. **Static analysis** — `AnalysisLevel` is set in VajSkrivare, Dokumentbibliotek and ODVGateway (all `latest`). `EnforceCodeStyleInBuild` is set only in ODVGateway (`true`).
6. **SDK pinning** — Seven of eight .NET repositories pin an SDK in `global.json`, all with `rollForward: latestFeature`: OpenModulePlatform `10.0.400`, ODVGateway `10.0.300`, LogSearch/EArkivChecker/Dokumentbibliotek `10.0.302`, IbsPackager/iKrock2 `10.0.200`. **VajSkrivare is the only repository with no pin** and therefore floats to the host SDK. The pins span four feature bands; today they all roll forward to the same installed SDK, so the spread is latent rather than active.
7. **JS/TS tooling** — Only OpenDocViewer has ESLint. Neither JS repo has an `.editorconfig`, a Prettier configuration file, or a `tsconfig.json`. AgentDocMap has no lint or format scripts.

## Recommended standard

### .NET repositories

1. **`.editorconfig`** at repository root based on the OpenModulePlatform template:
   - `root = true`
   - `charset = utf-8`
   - `end_of_line = lf`
   - `insert_final_newline = true`
   - `trim_trailing_whitespace = true`
   - `indent_style = space`, `indent_size = 4` for C#, CSHTML, SQL
   - `indent_size = 2` for JSON, YAML, Markdown
2. **`Directory.Build.props`** at repository root with shared build policy:
   - `TargetFramework` `net10.0`
   - `Nullable` `enable`
   - `ImplicitUsings` `enable`
   - `LangVersion` `latest` (or default SDK version unless a project needs a lower language version)
   - `AnalysisLevel` `latest`
   - `EnforceCodeStyleInBuild` `true`
   - `TreatWarningsAsErrors` `true` for CI/release builds; `false` for local inner-loop builds can be allowed via a condition.
3. **`Directory.Packages.props`** at repository root with `ManagePackageVersionsCentrally` `true`.
4. **`global.json`** pinning a stable SDK (e.g. `10.0.200` or `10.0.300`) with `rollForward: latestFeature`.
5. **Analyzers** — use the built-in SDK analyzers via `AnalysisLevel`/`EnforceCodeStyleInBuild`. Add project-specific analyzer packages (e.g. Roslyn analyzers, xUnit analyzers) only where needed.

### JavaScript / npm repositories

1. **`.editorconfig`** at repository root:
   - `charset = utf-8`
   - `end_of_line = lf`
   - `indent_style = space`, `indent_size = 2` for JS/JSON/MD/CSS
2. **ESLint** flat config (`eslint.config.js`) covering `src/`, `server/`, and tests with a consistent rule set.
3. **Prettier** explicit configuration file (`.prettierrc.json`) even if defaults are acceptable, so editors and CI behave the same.
4. **`package.json`** scripts for `lint`, `lint:fix`, and `format`.
5. **`tsconfig.json`** when introducing TypeScript; plain-JS repos should document the decision.

## Migration notes per diverging repo

### IbsPackager
- Add `.editorconfig` (LF, 4-space C#, 2-space JSON/YAML).
- Move common build settings into `Directory.Build.props`: `TargetFramework`, `Nullable`, `ImplicitUsings`, `AnalysisLevel`, `EnforceCodeStyleInBuild`, and a conditional `TreatWarningsAsErrors`.
- Keep CPM; consider adding xUnit analyzer package centrally.

### LogSearch / EArkivChecker
- Add `.editorconfig`.
- Decide on `TreatWarningsAsErrors`: recommend `true` in CI, `false` locally. Add `AnalysisLevel` `latest` and `EnforceCodeStyleInBuild` `true`.
- Update `global.json` from the preview SDK to a stable `10.0.200`/`10.0.300` pin.
- Keep CPM.

### Dokumentbibliotek
- Add `.editorconfig`.
- Add root `Directory.Build.props` with `TargetFramework`, `Nullable`, `ImplicitUsings`, `AnalysisLevel`, `EnforceCodeStyleInBuild`, and conditional `TreatWarningsAsErrors`.
- Add `Directory.Packages.props` and migrate inline `PackageReference` versions to CPM.
- Add `global.json` matching the rest of the ecosystem.

### VajSkrivare
- Adopt CPM by adding `Directory.Packages.props`; move package versions out of `.csproj` files and align test package versions with the ecosystem (e.g. `Microsoft.NET.Test.Sdk` 18.x, `xunit.runner.visualstudio` 3.x).
- Add `global.json`.
- Consider switching `.editorconfig` line endings from CRLF to LF to match OpenModulePlatform, or document a deliberate Windows-only exception.
- Change `TreatWarningsAsErrors` from unconditional `false` to a CI-only `true` condition.

### iKrock2
- Add `.editorconfig`.
- Move `TargetFramework`, `Nullable`, `ImplicitUsings`, `AnalysisLevel`, `EnforceCodeStyleInBuild`, and conditional `TreatWarningsAsErrors` into `Directory.Build.props` so individual `.csproj` files only declare project-specific items.
- Keep CPM.

### ODVGateway
- Adopt CPM by adding `Directory.Packages.props`; move inline package versions from `ODVGateway.csproj`.
- Add `Nullable`, `ImplicitUsings`, and `LangVersion` to `Directory.Build.props` so the single project does not need to repeat them.
- Align `.editorconfig` line endings with the chosen ecosystem standard (LF recommended).
- Change `TreatWarningsAsErrors` to `true` in CI.

### OpenDocViewer
- Add `.editorconfig` (LF, 2-space JS/JSON/CSS).
- Add explicit `.prettierrc.json` (even if it only restates defaults).
- Expand ESLint coverage to `server/**/*.js` and ensure tests are linted consistently.
- Add a `lint`/`format` CI step if not already present.

### AgentDocMap
- Add `.editorconfig`.
- Add ESLint flat config and Prettier config.
- Add `lint`, `lint:fix`, and `format` scripts to `package.json`.
- Consider adding a `tsconfig.json` if the project moves toward TypeScript; otherwise document the plain-JS decision.

## Output

- **File written:** `docs/conventions/code-style.md`
- **Source changes:** none (documentation-only audit)
- **Version bump:** none
- **Follow-up jobs:** none
