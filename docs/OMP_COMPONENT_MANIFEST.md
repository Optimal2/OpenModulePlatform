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

The output shape is always:

```text
module-definitions/
artifacts/
host-configs/
config-overlays/
widgets/
```

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
  "artifactConfigurationFiles": [
    {
      "componentKey": "opendocviewer-web",
      "relativePath": "odv.site.config.js",
      "sourcePath": "overlays/vgr-test/odv.site.config.js"
    }
  ],
  "hostConfigurationFiles": [],
  "configOverlayFiles": [],
  "widgetFiles": []
}
```

Paths inside the profile are resolved relative to the profile file unless they
are absolute. This lets the private DEV repository keep sensitive or
customer-specific inputs while public module repositories keep only generic code,
module definitions, and component metadata.
