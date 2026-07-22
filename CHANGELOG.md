# Changelog

All notable changes to this repository should be documented in this file.

The format is inspired by Keep a Changelog and the project follows semantic versioning at the repository level.

## [Unreleased]

> **Note:** This changelog was not maintained per-release after `0.1.0`. The repository has since advanced to the `0.3.x` release line. The authoritative current version is the `repositoryVersion` in `omp-components.json` (and the central metadata in `Directory.Build.props`) — not the newest entry below. Treat the `0.1.0` section as the initial-baseline record, not the current state; future notable changes should be logged here per the Keep a Changelog format.

## [0.1.0] - 2026-04-13

### Added

- initial public beta release line for OpenModulePlatform
- central version metadata in `Directory.Build.props`
- repository hygiene files: `.editorconfig`, `.gitattributes`, improved `.gitignore`
- GitHub Actions CI workflow for restore and release builds
- Dependabot configuration for NuGet packages and GitHub Actions
- worker runtime scaffold projects:
  - `OpenModulePlatform.WorkerManager.WindowsService`
  - `OpenModulePlatform.WorkerProcessHost`
  - `OpenModulePlatform.Worker.Abstractions`
- public contribution guidance and release-oriented documentation

### Changed

- standardized documentation in English for public release preparation
- strengthened public repository guidance and security documentation
- improved portal top bar JavaScript to coalesce resize-driven layout work into a single frame
- tightened several shared web implementation details during release preparation

### Notes

`0.1.0` is the first public beta baseline. The repository is intentionally useful and buildable, but some architectural areas remain under active design, especially template materialization, HostAgent, and the future worker runtime.
