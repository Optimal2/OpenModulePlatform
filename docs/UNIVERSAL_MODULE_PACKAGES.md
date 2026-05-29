# Universal Module Packages

Universal module packages are the preferred portable container for OMP deployment
objects. They are ordinary `.zip` files with a small manifest at the root and a
fixed folder layout for the objects they contain.

The format is intentionally sparse: every object type is optional. A package can
contain one artifact, one module definition, a bundle of many modules and
artifacts, host configuration objects, config overlays, dashboard widgets, or no
items at all beyond the manifest.

## Manifest

Every universal package must contain this file at the zip root:

```text
omp-universal-package.json
```

Example:

```json
{
  "formatVersion": 1,
  "objectType": "universal-module-package",
  "packageKey": "omp-core-runtime",
  "packageVersion": "2026.05.25",
  "displayName": "OMP core runtime",
  "description": "Module definitions, artifact packages, and optional overlays.",
  "targetHostProfile": "vgr-test",
  "items": [
    {
      "kind": "module-definition",
      "path": "module-definitions/omp_core__module-definition__0.3.56.json"
    },
    {
      "kind": "artifact-package",
      "path": "artifacts/omp_core__omp_hostagent__host-agent__omp-hostagent__0.3.51.zip"
    },
    {
      "kind": "config-overlay",
      "path": "config-overlays/vgr-test__opendocviewer__2.0.4.json"
    }
  ]
}
```

`items` is optional. If it is omitted or empty, importers discover known object
types from the standard folders. Empty packages are valid as long as the manifest
exists and has `formatVersion` set to `1`.

`targetHostProfile` is optional. Leave it absent or `null` for a global package.
When the package was created for one host profile, set it to that host key or
profile key. Importers treat it as operator-facing metadata; the portable
objects themselves still carry their own identities. Host-specific objects should
also include their host key inside the object document.

## Standard Folders

Use these root folders inside the zip:

```text
module-definitions/
artifacts/
host-configs/
config-overlays/
widgets/
widget-data/
```

Host-specific objects use the same root folders. Put them in a host subfolder
when it makes the package easier to inspect, for example:

```text
host-configs/vgr-test/vgr-test__host-config__2026.05.25.json
config-overlays/vgr-test/vgr-test__opendocviewer__2.0.4.json
widgets/vgr-test/omp-dashboard-widgets-vgr-test.json
```

No extra container folders are needed. The folder tells an operator what the
package was built for; the object JSON is still the source of truth for host key,
overlay key, version, and selectors.

Supported item kinds:

| Kind | Folder | File types | Meaning |
| --- | --- | --- | --- |
| `module-definition` | `module-definitions/` | `.json` | Versioned module definition document. |
| `artifact-package` | `artifacts/` | `.zip` | Standard OMP artifact package envelope. |
| `host-configuration` | `host-configs/` | `.json`, `.zip` | Host bootstrap/configuration object. |
| `config-overlay` | `config-overlays/` | `.json`, `.zip` | Host-specific config files applied outside artifact hashes. |
| `dashboard-widget` | `widgets/` | `.json` | Portal dashboard widget package JSON. |
| `widget-data` | `widget-data/` | `.zip` | Portal shared widget runtime data package containing `widget_data` JSON plus remappable `widget_binary_data` files. |

`widget-data` objects are zip files inside the universal package. They contain
an `omp-widget-runtime-data.json` manifest and binary entries. Runtime JSON may
refer to media with `binaryDataHash`; `binaryDataId` remains supported for
backward compatibility and is remapped on import. New exporters include both
when possible, and new importers can resolve hash-only references before the
target installation has assigned database ids.

Paths in the manifest must be relative package paths. Absolute paths, drive
letters, `..` segments, and invalid file-name characters are rejected.

## Installer Object Archive

The HostAgent-first installer uses the same folder names for its local object
archive. This makes the installer folder a staging area for universal packages,
not a separate format:

```text
installer/
  data/
    global/
      module-definitions/
      artifacts/
      host-configs/
      config-overlays/
      widgets/
      widget-data/
    hosts/
      <host-key>/
        host-configs/
        config-overlays/
        widgets/
        widget-data/
```

The private deployment repository can also keep host profiles beside the
installer, for example `hosts/<host-key>/bootstrap.json` with sibling
`config-overlays/`, `host-configs/`, and `widgets/` folders. The installer GUI
can read those host-specific folders when creating a package for a selected
target host. The bootstrap profile itself is not included automatically, because
it may contain installation paths, environment information, and encrypted
credentials that belong to the installer profile rather than to the portable OMP
object library.

Use the installer action `Refresh object archive` to populate or update
`data/global` from the matched profile's configured source repositories without
starting an installation. The refresh first runs `git pull --ff-only` for every
configured source repository and stops if any repository needs manual
merge/conflict handling. Each pull has a two-minute timeout so a stuck network
or credential prompt does not block the installer indefinitely. A developer
installer that contains profiles for multiple target hosts can use
`Prepare all host profiles` to materialize host-specific package objects for
every readable profile, even when a target profile has no source repository
links. Host-specific output is written to
`data/hosts/<host-profile>/host-configs`, `config-overlays`, `widgets`, and
`widget-data`, using profile-local folders and profile-declared object files.
If local source roots are available on the machine running the installer, the
same action also runs repository-owned host-profile hooks when present. It does
not refresh shared objects; run `Refresh object archive` separately for global
module definitions and artifact packages. Profiles are skipped only when their
config cannot be read or parsed. Then use
`Create universal package` to choose:

- the target host profile, or a global-only package
- global module definitions, artifacts, host configs, overlays, widgets, and widget data
- host-specific host configs, overlays, widgets, and widget data for the selected profile
- the output zip path

The generated zip contains `omp-universal-package.json` and only the selected
objects. This is the recommended way to create a customer or host-specific
transport package from a developer machine and then import it through Portal, the
HostAgent import folder, or another installer instance.

The installer can also import a universal package into its own object archive.
`Import universal package` copies package items into `data/global` and maps
host-specific package paths such as `config-overlays/<host-key>/...` or
`widget-data/<host-key>/...` into `data/hosts/<host-key>/...`. This is a staging
operation only: it does not apply module definitions, copy artifacts to a target
ArtifactStore, or change an installed OMP database. It lets an offline installer
receive objects from another installer and later use them for upgrade or export.

Portal also supports universal package import and export. Universal imports can
be run in two modes: import all supported items immediately, or preview the zip
and select individual manifest paths before importing. Universal export lets an
administrator choose module definitions, artifacts, host configurations, config
overlays, widgets, and optionally the shared runtime data for selected widgets
from the current OMP installation. Widget runtime data is exported under
`widget-data/` as a zip whose inner manifest remaps source `binaryDataId` values
to the target installation during import.

The standalone tool at `tools/universal-package-builder/index.html` creates the
same zip format entirely in the browser. It is available in Portal through the
Universal package builder admin page and can also be opened directly from disk.

## Repository Export Standard

Each OMP-compatible module repository should have the same exporter path:

```text
scripts/omp/export-universal-package.ps1
```

The exporter is the command-line equivalent of the Portal and standalone
builders for repository-owned objects. It reads `omp-components.json`, builds the
current module definitions and artifact packages, optionally applies host
profile inputs, and writes a universal package zip.

Use a global package when the repository only contributes generic module
definitions and binary artifact packages:

```powershell
.\scripts\omp\export-universal-package.ps1 -AllComponents -BuildArtifacts
```

Use a host profile when repository objects need host-specific configuration
files or overlays:

```powershell
.\scripts\omp\export-universal-package.ps1 `
  -AllComponents `
  -BuildArtifacts `
  -HostProfilePath E:\Private\profiles\vgr-test.package-profile.json
```

Public repositories should not store customer-specific profile files. The
private installer repository can keep those profiles and pass them to the same
exporter so every repository follows the same packaging model.

The host profile can carry module-specific segments. Top-level file lists apply
to the current repository export; `modules.<moduleKey>` applies only when the
repository owns that module key:

```json
{
  "targetHostProfile": "vgr-test",
  "modules": {
    "vajskrivare": {
      "settings": {
        "printerListPath": "\\\\vgregion.se\\app\\Vaj.SkaS\\Printer_List\\Printer_List_test.json"
      },
      "configOverlayFiles": [
        {
          "destinationName": "vgr-test-vajskrivare-appsettings.json",
          "sourcePath": "generated/vgr-test/vajskrivare-appsettings.json"
        }
      ]
    },
    "opendocviewer": {
      "artifactConfigurationFiles": [
        {
          "componentKey": "opendocviewer-web",
          "relativePath": "odv.site.config.js",
          "sourcePath": "generated/vgr-test/odv.site.config.js"
        }
      ]
    }
  }
}
```

The generic exporter understands the common file-list fields in each module
segment. If a module needs to transform arbitrary settings into generated
overlays, it can add `scripts/omp/build-host-profile-objects.ps1` in its own
repository. That hook receives the full host profile path and writes generated
portable objects under the exporter output root.

## Plugins

Plugins are not a separate universal-package object folder. OMP transports
plugin code as artifact packages, using the package type that matches the
runtime contract:

- `worker` for WorkerManager-loaded worker plugins.
- `worker-host` for the generic WorkerProcessHost runtime.
- `channel-type` for module-private plugin models such as IbsPackager channel
  types.
- `service-app` or `web-app` when a module implements extension behavior as a
  normal hosted app.

Plugin metadata belongs to the owning module definition or module-specific
database/configuration. For example, WorkerManager plugin metadata is expressed
through app worker definitions in the module definition, while IbsPackager
channel-type metadata belongs to the IbsPackager module. Host-specific plugin
configuration should be emitted as config overlays or artifact configuration
files, not embedded into the binary artifact hash.

## Import Behavior

Importers process universal packages item by item.

- One corrupt object should not stop the rest of the package.
- Objects that are already present with identical identity and content should be
  skipped or reported as already current.
- Module definitions are processed before artifact packages.
- Artifact packages that match a module definition in the same package are
  evaluated against that module definition's compatibility rules.
- For each app slot, the latest compatible artifact in the package is selected
  for immediate desired-state updates. Older compatible artifacts are retained as
  historical packages.
- Desired-state updates should move forward automatically to newer compatible
  versions. Older versions can be imported as library/history objects, but a
  deliberate downgrade should be made explicitly from the Portal installation
  administration pages.
- Standalone artifacts are imported independently and may update matching app or
  HostAgent desired state when compatibility allows it.
- Host configuration and config overlay objects are stored in the available
  object library.
- Dashboard widgets are imported by both Portal and HostAgent. HostAgent writes
  the same portable widget format into the Portal widget tables so import-folder
  packages can carry complete common OMP objects.

The result should report imported, skipped, and failed item counts. Failed items
belong to the package item, not to the whole package, unless the zip or manifest
itself is unreadable.

## Migration Policy

The old object formats remain readable during the transition:

- standalone module definition JSON files
- standalone artifact package zips
- legacy module package zips
- host configuration packages
- config overlay packages
- dashboard widget JSON packages

New tooling should prefer universal module packages for exchange between
repositories, the HostAgent import folder, Portal import, and installation
package staging. Older formats should only be kept as compatibility inputs.

## Configuration Rule

Runtime configuration files do not belong inside artifact payload hashes. Put
host-specific application settings and similar files in config overlay objects or
in the artifact package `configuration-files` section so HostAgent can materialize
them separately from the binary artifact content.
