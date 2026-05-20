# OMP Component Manifest

This repository uses `omp-components.json` to list deployable components that
can be registered as OMP artifacts.

The manifest separates repository releases from component artifact versions. A
repository can contain one deployable component or many. Each component has its
own `version`, and that value is the version that belongs in
`omp.Artifacts.Version`.

## Fields

- `repositoryKey` identifies the source repository.
- `repositoryVersion` is an optional coordinated release version for the whole
  repository.
- `moduleDefinitions` optionally lists versioned module definition documents
  included in the repository. These are database/metadata contracts, not
  deployable artifact zips. Each path should point at the module root definition
  file, such as `content_webapp.module-definition.json` next to the module
  project or `my_module.module-definition.json` in a standalone module repo.
- `componentKey` is the stable key used by scripts and release tooling.
- `moduleKey`, `appKey`, `packageType`, and `targetName` identify the OMP
  artifact target.
- `registrationMode` may be `bootstrap` for deployable infrastructure
  components that are packaged by the repository but are not yet represented by
  normal OMP app metadata. Omit it for normal OMP artifact components.
- `relativePathTemplate` describes the artifact-store path. Replace
  `{version}` with the component version.
- `packageFileTemplate` describes the expected package payload path when a
  package is produced.

## Bumping Versions

Use `scripts/bump-component-version.ps1` to update manifest versions:

```powershell
.\scripts\bump-component-version.ps1 -ComponentKey content-webapp -Part patch
.\scripts\bump-component-version.ps1 -ComponentKey content-webapp,iframe-webapp -Version 0.4.0
.\scripts\bump-component-version.ps1 -All -Part minor
```

The script updates `omp-components.json` only. The HostAgent-first package script
consumes the manifest directly when it prepares ArtifactStore payloads and SQL
artifact-version overrides. Older suite scripts still have separate package
version fields, so keep those aligned if you use the legacy installer.
