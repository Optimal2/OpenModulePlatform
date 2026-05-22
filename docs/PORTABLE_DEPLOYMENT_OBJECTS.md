# Portable Deployment Objects

OMP uses two portable object types for everything that should move between
installations:

- **Module definition objects** describe database and metadata state.
- **Artifact package objects** describe one deployable runtime package and the
  runtime configuration files that belong to that artifact version.

Together they replace module-specific installation scripts for normal
application deployment. A new environment should first import module
definitions, then import artifact packages that are compatible with those
definitions, then let HostAgent materialize the desired runtime state.

## Module Definition Object

The module definition object is a JSON document with `formatVersion: 1`. It is
the source of truth for:

- module and app metadata in the `omp` schema
- valid artifact slots and compatibility rules
- module dependencies
- required schemas, tables, seed rows, settings, permissions, and portal entries
- optional idempotent repair SQL carried as package-local `.sql` files

Every OMP module should keep its latest source definition file at the module
root and list it in that repository's `omp-components.json`. The generated
HostAgent-first installer copies those files into the package-local
`module-definitions` import folder. Portal stores imported definitions in
`omp.ModuleDefinitionDocuments`.

A portable module package zip can contain one definition JSON plus optional SQL
files:

```text
example_module__module-definition__1.2.3.zip
  module-definition/example_module.module-definition.json
  sql/repair.sql
```

The JSON references SQL files through `sqlScripts[].path`. Portal and HostAgent
resolve those paths inside the package and normalize the SQL into the stored
definition before execution. Modules that only need OMP module/app metadata and
artifact slots do not need SQL files.

HostAgent, WorkerManager, and WorkerProcessHost are represented by the
`omp_core` module definition. The remaining bootstrap exception is operational:
the first HostAgent service install and repair flow still needs a direct
HostAgent zip because artifact deployment is not available until a HostAgent
service is running.

## Artifact Package Object

The artifact package object is an outer zip named:

```text
moduleKey__appKey__packageType__targetName__version.zip
```

The zip should contain `omp-artifact-package.json` at its root. That manifest
points to the immutable payload and optional runtime configuration files:

```text
example__example_web__web-app__example-web__1.2.3.zip
  omp-artifact-package.json
  payload/artifact.zip
  configuration/site.config.json
```

Legacy root-payload zips are still accepted for compatibility, but new packages
should use the manifest envelope. Components with `moduleKey`, `appKey`,
`packageType`, `targetName`, and `version` in `omp-components.json` are emitted
as standard artifact package objects by `package-hostagent-first.ps1`.

## Tooling

Portal:

- `/admin/moduledefinitions` validates all active module definitions and can run
  safe repairs.
- `/admin/moduledefinitionedit` can download the stored JSON definition.
- `/admin/moduledefinitioneditor` opens the browser-based module definition
  editor.
- `/admin/modulepackageimport` can upload one portable module package zip,
  upload one module definition JSON together with one or more artifact package
  zips, import package-library files from `ArtifactStoreRoot\_available`, and
  export an applied module definition with its active artifact packages. The
  package-library view only offers artifact packages that match the selected
  module definition's declared compatibility range.
- `/admin/artifacts` and `/admin/artifactupload` import artifact packages.
- `/admin/artifactedit` can download an installed artifact as a standard package
  object, including registered configuration files.
- `/admin/artifactpackageeditor` opens the browser-based artifact package editor.

Standalone browser tools:

- `tools/module-definition-editor/index.html`
- `tools/artifact-package-editor/index.html`
- `tools/bootstrap-config-editor/index.html`

Command-line helpers:

- `scripts/dev/embed-module-definition-sql.ps1` refreshes embedded SQL in a
  legacy JSON-only module definition from its source `.sql` files.
- `scripts/deployment/new-omp-artifact-package.ps1` creates a standard artifact
  package object from a payload folder or payload zip plus optional config files.
- `scripts/deployment/package-hostagent-first.ps1` builds an installer payload
  that contains module definitions and artifact package objects.

## Export And Import Flow

To move a module from one OMP installation to another:

1. Export a module package from Portal, or collect one module definition JSON
   and the compatible artifact package zips from source/package output.
2. Import the module package into the target OMP through
   `/admin/modulepackageimport`.
3. Validate the module definition matrix and run required safe repairs when the
   import did not execute them immediately.
4. Review the desired template state. When "use imported artifacts immediately"
   was enabled during import, HostAgent deploys the selected versions on its
   next cycle.

Portal-driven import can offer operator choices for conflicts. HostAgent folder
import is intentionally strict and unattended. The same import folder can accept
module definition JSON files, standard artifact package zips, and module
package zips containing one module definition JSON plus one or more artifact
package zips for that module. Invalid filenames, incompatible module/app
combinations, duplicate versions, and changed hashes for an existing version
are recorded as import errors instead of silently changing database state.
