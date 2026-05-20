# Artifact Package Format

## Purpose

OMP has two portable inputs for module deployment:

- module-definition JSON documents that describe the module, its apps, SQL
  contract, and artifact compatibility
- artifact packages that contain one deployable app artifact and any
  deployment-owned configuration files that belong to that artifact version

The package format keeps runtime configuration out of immutable binaries while
still making a single uploaded/imported artifact zip sufficient to register both
the artifact row and the matching `omp.ArtifactConfigurationFiles` rows.

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

## Runtime Behavior

For a legacy zip, Portal and HostAgent folder import extract the whole zip as
the immutable artifact content and continue to block runtime configuration files
such as `appsettings*.json` and `odv.site.config.js`.

For a manifest envelope:

- only the declared payload is extracted to the artifact store and hashed
- runtime configuration files are still blocked inside the payload
- each declared `configurationFiles` entry is read as UTF-8 text
- `relativePath` becomes the deployed file path relative to the artifact root
- the imported files replace the artifact's current
  `omp.ArtifactConfigurationFiles` rows
- if no configuration files are declared, the existing "copy from previous
  version" behavior can still apply

Configuration paths and source paths must be relative. Rooted paths, `..`
segments, invalid path characters, and duplicate relative configuration paths
are rejected.

## Migration Plan

1. Keep accepting legacy zip files so existing build outputs and artifact
   archives remain usable.
2. Teach artifact builders to emit the manifest envelope when an app has
   deployment-owned configuration files.
3. Move app-specific config files such as ODV site config into the package
   envelope instead of uploading them as a separate step.
4. Keep the artifact edit page as a repair/inspection surface, but prefer
   package-owned configuration for normal releases.
5. Once all module builders produce package envelopes, a complete installation
   should be representable by module-definition documents plus artifact package
   zips.

The HostAgent-first bootstrapper also understands the manifest envelope when it
prepares the initial ArtifactStore. It extracts only the payload to the artifact
path and registers declared configuration files against the matching
`omp.Artifacts.RelativePath` row after the bootstrap SQL has created it.
