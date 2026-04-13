# Contributing

Thank you for contributing to OpenModulePlatform.

## Before you open a pull request

1. Build the solution locally:
   - `dotnet restore OpenModulePlatform.slnx`
   - `dotnet build OpenModulePlatform.slnx --configuration Release`
2. Update documentation when behaviour, terminology, or public guidance changes.
3. Review SQL scripts when schema or bootstrap data changes.
4. Verify that no secrets, local IDE files, or generated output are included.

## Coding and documentation expectations

- keep public-facing documentation and code comments in English
- keep examples neutral and free from customer-specific or environment-specific data
- prefer small, reviewable changes with explicit intent
- avoid broad refactors unless they clearly improve correctness, clarity, or maintainability

## Security

Do not open a public issue for a suspected security vulnerability.
Follow the process described in [SECURITY.md](SECURITY.md).

## Versioning and release discipline

The repository is currently prepared for the `0.1.x` public beta line.
Breaking changes should be documented clearly and coordinated with release notes.

## Repository hygiene

Do not commit:

- local IDE folders such as `.idea`, `.vs`, or `.vscode`
- generated output such as `bin`, `obj`, logs, coverage files, or release archives
- secrets, certificates, environment-specific credentials, or connection strings with real values
