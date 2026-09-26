# Contributing

Thank you for contributing to OpenModulePlatform.

## Before you open a pull request

1. Build the solution locally:
   - `dotnet restore OpenModulePlatform.slnx`
   - `dotnet build OpenModulePlatform.slnx --configuration Release`
2. Run the local CI gate, `scripts/local-ci.ps1` (it also runs on `git push`
   once `scripts/setup-hooks.ps1` has activated the tracked hooks).
3. Update documentation when behaviour, terminology, or public guidance changes.
4. Review SQL scripts when schema or bootstrap data changes. When SQL owned by
   a module definition changes, bump that definition's `definitionVersion` and
   re-embed the SQL with `scripts/dev/embed-module-definition-sql.ps1` in the
   same change (see [AGENTS.md](AGENTS.md)).
5. Bump the version of every component whose deployable output changes, with
   `scripts/omp/bump-version.ps1`.
6. Verify that no secrets, local IDE files, or generated output are included.

## Coding and documentation expectations

- keep public-facing documentation and code comments in English
- keep examples neutral and free from customer-specific or environment-specific data
- prefer small, reviewable changes with explicit intent
- avoid broad refactors unless they clearly improve correctness, clarity, or maintainability

## Test gates and script analysis

- Changing the `dotnet test --filter` in `.github/workflows/ci.yml`, or
  otherwise excluding or skipping tests from a gate, requires a same-commit
  update of [docs/TEST_DEBT.md](docs/TEST_DEBT.md).
- Run `scripts/omp/run-script-analyzer.ps1` before committing PowerShell
  changes. It enforces security rules and Windows PowerShell 5.1
  compatibility over every committed `.ps1`/`.psm1`/`.psd1` file and also
  runs as a CI gate.

## Security

Do not open a public issue for a suspected security vulnerability.
Follow the process described in [SECURITY.md](SECURITY.md).

## Versioning and release discipline

The repository is on the `0.3.x` line; the current version is the
`repositoryVersion` value in `omp-components.json` (bumped automatically on
push by `scripts/omp/push-with-rebump.ps1`, never by hand).
Breaking changes should be documented clearly and coordinated with release notes.

## Repository hygiene

Do not commit:

- local IDE folders such as `.idea`, `.vs`, or `.vscode`
- generated output such as `bin`, `obj`, logs, coverage files, or release archives
- secrets, certificates, environment-specific credentials, or connection strings with real values
