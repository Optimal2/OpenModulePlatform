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
- `registrationMode` may be `bootstrap` only for installer-critical components
  that need special bootstrap handling before HostAgent can manage normal OMP
  artifacts. Omit it for runtime code. A bootstrap component can still be
  emitted as a standard artifact package when it has `moduleKey` and `appKey`;
  Auth uses this so HostAgent can deploy it as the `/auth` web app while the
  installer still treats it as bootstrap-required.
- `relativePathTemplate` describes the artifact-store path. Replace
  `{version}` with the component version.
- `packageFileTemplate` describes the expected package payload path when a
  package is produced.
- `projectPath` should point at the component project or project folder when the
  component can be published directly with `dotnet publish`. The graphical
  bootstrapper uses this to build only missing .NET artifact packages during
  `Sync package objects`.
- `minModuleDefinitionVersion` is optional and is written into generated
  artifact package manifests as `moduleDefinition.minVersion`. Use it only when
  that component build requires SQL, OMP metadata, or another module contract
  from a newer module definition. Leave it empty for normal code-only artifact
  releases so the artifact can be imported and selected without bumping the
  module definition.

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

Components with a complete artifact identity (`moduleKey`, `appKey`,
`packageType`, `targetName`, and `version`) are packaged as manifest-based OMP
artifact package objects. HostAgent is the only runtime component that also has
a direct installer payload, because the first HostAgent service installation
must work before artifact deployment exists. WorkerManager and WorkerProcessHost
are normal `omp_core` artifacts.

## Generating Current Objects

Module definitions and artifact packages are generated from this manifest rather
than from one script per module or artifact. Keeping generation manifest-driven
avoids a second list of component names that can drift from the actual OMP
identity fields. The HostAgent-first package builder consumes
`omp-components.json`, embeds current SQL into module-definition JSON, publishes
component projects, and emits standard artifact package zips.

The graphical bootstrapper exposes the same workflow for development machines.
It can load multiple bootstrap JSON profiles from the package-local `configs`
folder so one installer package can be reused across local and private
environments. `Check source objects` compares the selected profile's package and
database with the source manifests. `Create updated installer package` starts a
detached refresh process that rebuilds the HostAgent-first package from the
manifest, replaces the current package after the GUI exits, and restarts the
updated installer. `Sync package objects` is narrower: it copies updated
module-definition JSON and standard artifact package zips into the current
installer package, and when a missing artifact has a resolvable .NET
`projectPath`, it publishes only that component and stores the generated
artifact package in `RuntimeRoot\ArtifactArchive`.

Repositories can also build their own portable objects without a full installer
refresh:

```powershell
.\scripts\omp\build-repository-objects.ps1 -OutputRoot E:\OMP\ObjectBuild -AllComponents
.\scripts\omp\build-repository-objects.ps1 -OutputRoot E:\OMP\ObjectBuild -ComponentKey content-webapp -BuildArtifacts
.\scripts\omp\build-repository-objects.ps1 `
  -OutputRoot E:\OMP\ObjectBuild `
  -ComponentKey opendocviewer-web `
  -BuildArtifacts `
  -ArtifactConfigurationFile 'opendocviewer-web:odv.site.config.js=E:\Secure\odv.site.config.js'
```

Every OMP-compatible module repository should expose a root wrapper with the
same command shape:

```text
build-omp-objects.ps1
```

The wrapper should delegate to the canonical OpenModulePlatform
`scripts/omp/build-repository-objects.ps1` implementation and support the
shared object parameters:

- `-ArtifactConfigurationFile`
- `-HostConfigurationFile`
- `-ConfigOverlayFile`
- `-WidgetFile`

Sibling repositories should also accept `-OmpRepositoryRoot` and the
`OMP_REPOSITORY_ROOT` environment variable so CI and non-sibling checkouts can
locate the canonical OMP scripts without hardcoded local paths.

The output shape is always:

```text
module-definitions/
artifacts/
host-configs/
config-overlays/
widgets/
```

When `-ComponentKey` is used, the generated object set is scoped to the selected
components. Artifact packages are emitted only for those components, and module
definition files are emitted only for the matching component module keys. This
keeps a HostAgent-only or single web-app package from importing unrelated module
definitions and accidentally repairing or changing desired state for unrelated
apps. Use `-AllComponents` when the package is meant to refresh the full
repository.

Point `OutputRoot` at an installer package's `data/global` folder to refresh the
shared package library directly. Customer or host-specific configuration is
passed as arguments and must not be committed to source repositories; the DEV
repository is the appropriate place to keep private profile files and the
commands that pass those files into object generation.

## Repository Universal Package Export

Every OMP-compatible module repository should expose this command path:

```text
scripts/omp/export-universal-package.ps1
```

The command reads `omp-components.json`, builds the repository's current
portable objects, and emits one universal package zip with
`omp-universal-package.json` plus the standard object folders.

Global package:

```powershell
.\scripts\omp\export-universal-package.ps1 -AllComponents -BuildArtifacts
```

Host-specific package:

```powershell
.\scripts\omp\export-universal-package.ps1 `
  -AllComponents `
  -BuildArtifacts `
  -HostProfilePath E:\Private\profiles\vgr-test.package-profile.json `
  -OutputPath E:\Packages\openmoduleplatform__vgr-test__20260525.zip
```

The optional host profile is JSON. It may contain:

```json
{
  "targetHostProfile": "vgr-test",
  "modules": {
    "opendocviewer": {
      "artifactConfigurationFiles": [
        {
          "componentKey": "opendocviewer-web",
          "relativePath": "odv.site.config.js",
          "sourcePath": "overlays/vgr-test/odv.site.config.js"
        }
      ]
    }
  },
  "hostConfigurationFiles": [],
  "configOverlayFiles": [],
  "widgetFiles": []
}
```

Paths inside the profile are resolved relative to the profile file unless they
are absolute. This lets the private DEV repository keep sensitive or
customer-specific inputs while public module repositories keep only generic code,
module definitions, and component metadata.

Top-level file lists apply to the current repository export. Values under
`modules.<moduleKey>` apply only when the repository owns that module key in
`omp-components.json`. This lets one shared host profile contain data for
OpenDocViewer, VajSkrivare, IbsPackager, and other modules while each repository
consumes only its own segment.

If a repository needs to generate portable host-specific objects from arbitrary
module-private settings, it can add this optional hook:

```text
scripts/omp/build-host-profile-objects.ps1
```

The exporter calls the hook after the generic object build and before the
universal zip is created. The hook receives `-RepositoryRoot`, `-OutputRoot`,
`-HostProfilePath`, `-TargetHostProfile`, `-ModuleKey`, and `-Configuration`.
It should write generated `host-configs`, `config-overlays`, or `widgets` under
`OutputRoot`. The hook is owned by the module repository, so the module decides
how to interpret its `modules.<moduleKey>` settings.

## Repository Conformance Checklist

A well-formed OMP-compatible module repository should normally provide:

- `AGENTS.md` with repository-specific agent and safety rules.
- `README.md` or equivalent development documentation.
- `omp-components.json` for module definitions and artifact components owned by
  the repository.
- `build-omp-objects.ps1` as the root portable-object build wrapper.
- `scripts/omp/export-universal-package.ps1` as the universal package export
  wrapper.
- `scripts/omp/README.md` or equivalent notes when repository-local OMP scripts
  exist.

The root wrapper and export wrapper should stay thin. Keep the canonical
implementation in OpenModulePlatform so parameter behavior, host-profile
handling, and object folder layout stay consistent across module repositories.
