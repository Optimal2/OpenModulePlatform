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

Both object types can be imported through Portal, imported by the HostAgent
folder watcher, or copied into an installer package library.

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
  "artifactVersion": "2.0.3",
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

Configuration files are stored in `omp.ConfigOverlayConfigurationFiles`.
HostAgent loads artifact-owned configuration first, then matching overlay files.
If both define the same `relativePath`, the overlay wins for that host.

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
objects for later import. HostAgent import-folder processing accepts the same
object formats directly.

## Tooling

- Portal: `/admin/modulepackageimport` imports and exports host configurations
  and config overlays through universal packages.
- Standalone: `tools/universal-package-builder/index.html` can assemble
  universal packages that include host configurations and config overlays.

Repository build scripts should create global module definitions and artifact
packages from neutral source data. Customer or host-specific values should live
in the private installation repository and be passed to generator scripts that
write host configurations or config overlays.
