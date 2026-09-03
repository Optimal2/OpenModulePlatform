# Host Configurations And Config Overlays

OMP keeps global deployment objects separate from host-specific configuration.
That separation avoids duplicating module definitions and artifact binaries when
only a server name, path, account, URL, or customer value differs.

## Object Types

There are two host-specific portable object types:

- **Host configuration** is one JSON document per host. It contains the host key
  and an opaque `values` object that generator scripts can use to create
  overlays.
- **Config overlay** is a JSON document or zip package that targets one host and
  optionally narrows itself to a module, app, package type, target name, or
  artifact version. HostAgent applies matching overlay configuration files on
  top of artifact-owned configuration files during deployment.

Both object types can be imported through Portal or copied into an installer
package library. For unattended HostAgent import, put the objects inside a
universal module package zip. The HostAgent import folder does not accept raw
JSON objects or standalone config-overlay zips.

## Host Configuration JSON

```json
{
  "formatVersion": 1,
  "objectType": "host-configuration",
  "hostKey": "DESKTOP-EXAMPLE",
  "configurationVersion": "1.0.0",
  "displayName": "Example host",
  "description": "Host-level input for overlay generation.",
  "values": {
    "paths": {
      "dataRoot": "E:\\OMP\\Data"
    },
    "identity": {
      "defaultServiceAccountKey": "default-service"
    }
  }
}
```

Host configuration imports are stored in `omp.HostConfigurationDocuments`.
OMP core does not interpret the `values` object directly. Repository-level or
customer-specific generation scripts decide which values are meaningful.

## Config Overlay JSON

```json
{
  "formatVersion": 1,
  "objectType": "config-overlay",
  "overlayKey": "opendocviewer-site-config",
  "overlayVersion": "1.0.0",
  "hostKey": "DESKTOP-EXAMPLE",
  "moduleKey": "opendocviewer",
  "appKey": "opendocviewer_webapp",
  "packageType": "web-app",
  "targetName": "opendocviewer",
  "configurationFiles": [
    {
      "relativePath": "odv.site.config.js",
      "fileContent": "window.OpenDocViewerSiteConfig = { apiBaseUrl: '/OpenDocViewer' };"
    }
  ]
}
```

The selectors are intentionally optional except for `overlayKey`,
`overlayVersion`, and `hostKey`.

- If `moduleKey` is omitted, the overlay can match any module on that host.
- If `appKey` is omitted, it can match any app in the selected module scope.
- If `packageType`, `targetName`, or `artifactVersion` are omitted, those
  fields do not constrain the match.

**Leave `artifactVersion` out unless the overlay is deliberately tied to one
artifact build.** A pinned overlay matches that version only and silently stops
applying at the next artifact upgrade, and the next deployment then falls back to
the artifact's own configuration file (or, for apps that ship no configuration
file, to the HostAgent's built-in defaults). Measured on a customer test host in
August 2026: an Auth overlay pinned to one version lost its OIDC section at the
following upgrade. An environment overlay should describe the host, not the
build - key it on `hostKey`, `moduleKey`, `appKey`, `packageType` and
`targetName`, and bump `overlayVersion` when its content changes.

Configuration files are stored in `omp.ConfigOverlayConfigurationFiles`.
HostAgent loads artifact-owned configuration first, then matching overlay files.
If both define the same `relativePath`, the overlay wins for that host.

## The other layer: artifact-owned configuration

Overlays are only half of the model, and the other half is easy to miss because
it is never shipped inside the artifact zip. Artifact-owned configuration files
live in `omp.ArtifactConfigurationFiles`, keyed by `ArtifactId` + `RelativePath`.
HostAgent loads those first and then applies matching overlay files on top, so
an operator editing a deployed `appsettings.json` on disk is editing something
that HostAgent will overwrite on the next deployment.

**A new artifact version does not inherit configuration automatically - it is
copied, and only under one narrow condition.** The Bootstrapper's
`CopyMissingArtifactConfigurationFilesFromPreviousVersionAsync`
(`OpenModulePlatform.Bootstrapper/Program.cs:4525`, current `main` `b181adad`)
behaves as follows:

1. It runs **only if the target artifact has zero configuration files**. If the
   new version already carries even one file, nothing is copied and the rest of
   the previous version's files are simply absent.
2. The source is chosen from artifacts whose version compares **strictly lower**
   than the target's; among those it takes the **highest** version, breaking ties
   on the higher `ArtifactId`.
3. The copy is `INSERT ... WHERE NOT EXISTS` on `RelativePath`, so it never
   overwrites a file the target already has.

The consequence worth internalising: **the source is the newest earlier version,
which is not necessarily the version currently deployed.** If a host is running
an older artifact than the newest one registered, the configuration carried into
the next version comes from that newer registered artifact, not from what the
host actually runs. Anything that must follow the environment rather than the
build belongs in a config overlay keyed on `hostKey` - see the `artifactVersion`
warning above.

## Config Overlay Package Zip

Use a zip package when the overlay contains JavaScript, HTML, XML, or other text
that security products may block as raw form posts, or when the overlay should
keep reviewable sidecar files.

```text
DESKTOP-EXAMPLE__opendocviewer-site-config__overlay__1.0.0.zip
  omp-config-overlay.json
  files/odv.site.config.js
  sql/repair.sql
```

The manifest can reference files with `source` or `path`:

```json
{
  "formatVersion": 1,
  "objectType": "config-overlay",
  "overlayKey": "opendocviewer-site-config",
  "overlayVersion": "1.0.0",
  "hostKey": "DESKTOP-EXAMPLE",
  "moduleKey": "opendocviewer",
  "appKey": "opendocviewer_webapp",
  "packageType": "web-app",
  "configurationFiles": [
    {
      "relativePath": "odv.site.config.js",
      "source": "files/odv.site.config.js"
    }
  ]
}
```

Portal and HostAgent normalize referenced files into the stored JSON before the
object is saved. The stored object is therefore self-contained even if the
source zip used separate files for code review.

## Config overlay SQL scripts

Config overlays may contain a `sqlScripts` array for legacy compatibility, but
**OMP does not execute SQL scripts from config overlays**. Database changes
belong in module-definition `sqlScripts` or in dedicated DBA-run scripts.

Portal and HostAgent imports now surface a clear warning when a config overlay
contains `sqlScripts`, while still storing the overlay and its configuration
files normally.

## Installer Package Layout

HostAgent-first packages use one global portable object library:

```text
data/global/module-definitions
data/global/artifacts
data/global/host-configs
data/global/config-overlays
```

The selected host profile contains all host-specific installer settings. Private
universal installer packages should keep profiles outside generated package
content:

```text
hosts/<profile>/bootstrap.json
hosts/<profile>/package.psd1
hosts/<profile>/sql
hosts/<profile>/host-configs
hosts/<profile>/config-overlays
```

`bootstrap.json` is the profile selected by the GUI. `package.psd1` and the
optional `sql`, `host-configs`, and `config-overlays` folders are source inputs
used by developer refresh tooling. Generated package folders such as `sql` and
`data` are build output and should not be treated as the source of truth.

Older packages may still place bootstrap JSON files directly below `configs`;
the bootstrapper continues to support that layout for compatibility. If a
package must carry host-only helper files for the bootstrapper itself, place
them below:

```text
data/hosts/<config-file-name-without-extension>
```

The bootstrapper copies the global library into:

```text
ArtifactStoreRoot\_available\module-definitions
ArtifactStoreRoot\_available\artifacts
ArtifactStoreRoot\_available\host-configs
ArtifactStoreRoot\_available\config-overlays
```

Portal reads these folders from the `ArtifactUpload` settings and offers the
objects for later import. The HostAgent import folder instead accepts only a
top-level universal module package zip containing `omp-universal-package.json`.
Use Portal or the universal package builder to wrap host configurations and
config overlays before dropping them into the watched folder.

## Tooling

- Portal: `/admin/modulepackageimport` imports and exports host configurations
  and config overlays through universal packages.
- Standalone: `tools/universal-package-builder/index.html` can assemble
  universal packages that include host configurations and config overlays.

Repository build scripts should create global module definitions and artifact
packages from neutral source data. Customer or host-specific values should live
in the private installation repository and be passed to generator scripts that
write host configurations or config overlays.
