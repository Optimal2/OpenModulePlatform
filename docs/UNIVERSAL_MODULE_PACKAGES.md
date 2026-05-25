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

## Standard Folders

Use these root folders inside the zip:

```text
module-definitions/
artifacts/
host-configs/
config-overlays/
widgets/
```

Supported item kinds:

| Kind | Folder | File types | Meaning |
| --- | --- | --- | --- |
| `module-definition` | `module-definitions/` | `.json` | Versioned module definition document. |
| `artifact-package` | `artifacts/` | `.zip` | Standard OMP artifact package envelope. |
| `host-configuration` | `host-configs/` | `.json`, `.zip` | Host bootstrap/configuration object. |
| `config-overlay` | `config-overlays/` | `.json`, `.zip` | Host-specific config files applied outside artifact hashes. |
| `dashboard-widget` | `widgets/` | `.json` | Portal dashboard widget package JSON. |

Paths in the manifest must be relative package paths. Absolute paths, drive
letters, `..` segments, and invalid file-name characters are rejected.

## Import Behavior

Importers process universal packages item by item.

- One corrupt object should not stop the rest of the package.
- Module definitions are processed before artifact packages.
- Artifact packages that match a module definition in the same package are
  evaluated against that module definition's compatibility rules.
- For each app slot, the latest compatible artifact in the package is selected
  for immediate desired-state updates. Older compatible artifacts are retained as
  historical packages.
- Standalone artifacts are imported independently and may update matching app or
  HostAgent desired state when compatibility allows it.
- Host configuration and config overlay objects are stored in the available
  object library.
- Dashboard widgets are imported by Portal. HostAgent skips widget items because
  Portal owns UI metadata imports.

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
