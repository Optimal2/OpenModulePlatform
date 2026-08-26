# Artifact Package Format

## Purpose

OMP has two portable inputs for module deployment:

- module-definition JSON documents that describe the module, its apps, SQL
  contract, and artifact compatibility
- artifact packages that contain one deployable app artifact and any
  deployment-owned configuration files that are global for that artifact
  version

The package format keeps runtime configuration out of immutable binaries while
still making a single uploaded/imported artifact zip sufficient to register both
the artifact row and the matching `omp.ArtifactConfigurationFiles` rows.
Host-specific configuration belongs in config overlays instead; see
[`CONFIG_OVERLAYS.md`](CONFIG_OVERLAYS.md).

## Filename

The outer zip keeps the existing artifact identity format:

```text
moduleKey__appKey__packageType__targetName__version.zip
```

The filename remains the import identity for both Portal upload and HostAgent
folder import. Existing legacy zip files with deployable files at the zip root
are still supported.

## Manifest Envelope

A manifest-based package contains `omp-artifact-package.json` at the zip root.
When that file is present, the zip is treated as an OMP artifact package
envelope instead of a legacy artifact zip.

Create artifact packages with repository packaging scripts or by exporting an
already imported artifact from Portal. New releases should normally be
transported inside universal packages; Portal's import/export page links to the
universal package builder for browser-based package assembly.

For automated builds, use:

```powershell
.\scripts\deployment\new-omp-artifact-package.ps1 `
  -ModuleKey example_module `
  -AppKey example_module_web `
  -PackageType web-app `
  -TargetName example-module-web `
  -Version 1.2.3 `
  -PayloadPath .\publish\ExampleWeb `
  -OutputPath .\artifacts `
  -ConfigurationFile 'appsettings.json=.\config\appsettings.example.json'
```

Portal can also export an already imported artifact from the artifact edit page.
The exported package uses the artifact store payload and the current enabled
`omp.ArtifactConfigurationFiles` rows.

Recommended layout:

```text
module__app__web-app__target__1.2.3.zip
  omp-artifact-package.json
  payload/
    ... deployable files ...
  configuration/
    app.site.config.js
    extra-settings.json
```

Equivalent nested-payload layout:

```text
module__app__web-app__target__1.2.3.zip
  omp-artifact-package.json
  payload/artifact.zip
  configuration/app.site.config.js
```

Manifest example:

```json
{
  "formatVersion": 1,
  "payload": {
    "type": "directory",
    "path": "payload/"
  },
  "moduleDefinition": {
    "minVersion": "1.2.3"
  },
  "configurationFiles": [
    {
      "relativePath": "odv.site.config.js",
      "source": "configuration/odv.site.config.js"
    },
    {
      "relativePath": "App_Data/site-settings.json",
      "source": "configuration/site-settings.json"
    }
  ]
}
```

`payload.type` can be `directory` or `zip`. If `type` is omitted, paths ending
in `.zip` are treated as nested zip payloads and other paths are treated as
directory prefixes.

`moduleDefinition.minVersion` is optional. Leave it out for normal code-only
artifact releases that are compatible with the currently applied module
definition. Set it only when this artifact requires SQL, OMP metadata, or
another module contract from a newer module definition. Portal and HostAgent
validate the requirement before registering the artifact.

## Runtime Behavior

For a legacy zip, Portal and HostAgent folder import extract the whole zip as
the immutable artifact content and continue to block runtime configuration files
such as `appsettings*.json` and `odv.site.config.js`. This matters for apps
that need host-specific runtime configuration: their settings must be stored as
artifact configuration-file rows or config overlays, never inside the hashed
payload.

For a manifest envelope:

- only the declared payload is extracted to the artifact store and hashed
- runtime configuration files are still blocked inside the payload
- each declared `configurationFiles` entry is read as UTF-8 text
- `relativePath` becomes the deployed file path relative to the artifact root
- the imported files replace the artifact's current
  `omp.ArtifactConfigurationFiles` rows; the packaged content is also stored as
  the row's pristine `PackageFileContent` baseline
- importing the same artifact identity with the same payload hash is still
  allowed to update configuration-file rows from the package; the immutable
  artifact payload is left unchanged. Rows whose packaged content is unchanged
  against the stored baseline keep their operator-edited `FileContent` and
  `IsEnabled` values instead of being overwritten
- when a new artifact version is registered with packaged configuration files,
  import runs a three-way carry-forward against the latest previous artifact in
  the same app/package-type/target slot: if the previous row was
  operator-edited and the packaged file is unchanged against that row's
  baseline, the operator content follows the new version automatically. If the
  packaged file changed over an operator edit, the package file wins and the
  import result carries a warning naming the affected file so the operator can
  merge manually. A row with no baseline (operator-created, or predating the
  `PackageFileContent` column added 2026-08-12) is carried forward instead when
  the new version's row is untouched package content, and reported separately —
  which means a package change to such a file does NOT take effect; see
  ADMIN_CONFIGURATION.md for why that direction was chosen.
  Operator-edited files that the new package no longer ships are also reported
- if metadata for the same artifact identity and payload hash already exists
  but the artifact store payload folder is missing, Portal and HostAgent import
  repair the missing folder from the package instead of treating the import as
  a no-op
- if no configuration files are declared, the existing "copy from previous
  version" behavior can still apply
- matching config overlays can override these artifact-owned files for one host
  during HostAgent deployment

Configuration paths and source paths must be relative. Rooted paths, `..`
segments, invalid path characters, and duplicate relative configuration paths
are rejected.

### Identical content under a new version (empty-diff bumps)

There is a content-dedup gate on import, and what it refuses is narrower than it
looks. It rejects **identical content under a DIFFERENT component**, which is a
repackaging mistake. It does **not** reject identical content under a new
version of the **same** component.

That distinction is deliberate. Deterministic builds combined with lockstep
consumer bumps legitimately produce a new version of the same component whose
extracted content is byte-identical to a version already imported. When the gate
refused those, module deploy-sets split: the parts of a module with real changes
imported under the new version while the empty-diff parts stayed behind on the
old one — observed on 2026-08-24 with an `example_serviceapp` web/service pair
landing on two different versions.

The rule is the same on all three import paths, and they were aligned
deliberately after the first fix only covered one of them:

- HostAgent import (`HostAgent.Runtime/Services/ArtifactZipImportService.cs`)
- Portal portable module package import
  (`Portal/Services/PortableModulePackageService.cs`) — this one used to
  silently **Skip** an empty-diff bump, which split the deploy-set it was in the
  middle of importing
- Portal manual artifact upload (`Portal/Pages/Admin/ArtifactUpload.cshtml.cs`)
  — this one used to reject it outright

Two details matter when reading the code or a result message:

- The same-component check compares **`AppId`**, not the `AppKey` string.
  `AppKey` uniqueness across modules is not guaranteed, so the string comparison
  could match two different apps.
- An accepted empty-diff bump is **not silent**. The HostAgent import result
  carries an explicit note saying the content was identical to an
  already-imported version and was accepted as an empty-diff version bump, so an
  operator can tell it apart from a real rebuild.

## Migration Plan

1. Keep accepting legacy zip files so existing build outputs and artifact
   archives remain usable.
2. Teach artifact builders to emit the manifest envelope when an app has
   artifact-owned configuration files that are the same in every environment.
3. Move host-specific config files such as ODV site config into config overlays
   instead of uploading them as a separate artifact-owned step.
4. Keep the artifact edit page as a repair/inspection surface, but prefer
   package-owned configuration for normal releases.
5. Once all module builders produce package envelopes, a complete installation
   should be representable by module-definition documents plus artifact package
   zips.

The HostAgent-first bootstrapper also understands the manifest envelope when it
prepares the initial ArtifactStore. It extracts only the payload to the artifact
path and registers declared configuration files against the matching
`omp.Artifacts.RelativePath` row after the bootstrap SQL has created it. The
HostAgent-first package builder emits this envelope for every component in
`omp-components.json` that has a complete OMP artifact identity
(`moduleKey`, `appKey`, `packageType`, `targetName`, and `version`).
HostAgent is special: the bootstrapper still includes a direct HostAgent zip
for first install and repair, but it also emits a standard `host-agent`
artifact package so later HostAgent self-upgrades can be driven by OMP metadata.
Manifest-based artifact packages produced by the HostAgent-first package
builder strip runtime `appsettings*.json` files from their payload before the
envelope is written. Runtime configuration stays outside the binary artifact
and is generated by the installer, HostAgent, artifact configuration-file rows,
or matching config overlays.
