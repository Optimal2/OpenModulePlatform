# Portable Deployment Objects

OMP uses two portable object types for everything that should move between
installations:

- **Module definition objects** describe database and metadata state.
- **Artifact package objects** describe one deployable runtime package and the
  runtime configuration files that belong to that artifact version.

Together they replace module-specific installation scripts for normal
application deployment. A new environment should first import module
definitions, then import artifact packages that are compatible with those
definitions, then let HostAgent materialize the desired runtime state. When a
module should move as one unit, use a complete module package: one zip that
contains the module definition plus the outer artifact package zips for that
module.

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

A portable module-definition package zip can contain one definition JSON plus
optional SQL files:

```text
example_module__module-definition__1.2.3.zip
  module-definition/example_module.module-definition.json
  sql/repair.sql
```

The JSON references SQL files through `sqlScripts[].path`. Portal and HostAgent
resolve those paths inside the package and normalize the SQL into the stored
definition before execution. Modules that only need OMP module/app metadata and
artifact slots do not need SQL files.

Modules with their own schema or complex seed rules can add a read-only
validation script using `phase: "validate"`. It returns one row with an
`IsHealthy` value and optional `Message`. If validation reports unhealthy state,
Portal and HostAgent run the module's idempotent non-validation SQL scripts as
the repair path. Generic metadata checks for the OMP module/app rows still run
for every module, so simple modules can omit validation SQL entirely.

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

## Complete Module Package

A complete module package is the preferred handoff object when a whole module
should be installed, completed, or moved between OMP installations:

```text
example_module__module-package__1.2.3.zip
  module-definition/example_module.module-definition.json
  sql/validate.sql
  sql/repair.sql
  artifacts/example_module__example_web__web-app__example-web__1.2.3.zip
  artifacts/example_module__example_worker__worker__example-worker__1.2.3.zip
```

The package must contain exactly one module definition JSON document. Artifact
zips inside the package keep the standard outer artifact filename format. Portal
and HostAgent normalize package-local SQL references before storing the module
definition, then import compatible artifact packages for the same `moduleKey`.

Conflict handling is deterministic:

- identical module definitions and identical artifact content are skipped rather
  than duplicated
- a package with an older module definition version is stored for review but
  does not replace a newer applied module definition
- artifact versions outside the active module definition compatibility range are
  skipped so exported historical packages remain safe to re-import
- an existing artifact identity with different content is an error unless an
  operator explicitly chooses replacement in Portal
- when a package contains multiple compatible versions for the same
  app/package/target slot, only the highest compatible version is selected as
  the desired runtime version; older compatible versions are registered or kept
  as historical packages

## Tooling

Portal:

- `/admin/moduledefinitions` validates all active module definitions and can run
  safe repairs.
- `/admin/moduledefinitionedit` can download the stored JSON definition.
- `/admin/moduledefinitioneditor` opens the browser-based module definition
  editor.
- `/admin/modulepackageimport` can upload one complete module package zip,
  upload one module definition JSON together with one or more artifact package
  zips, import package-library files from `ArtifactStoreRoot\_available`, and
  export an applied module definition with its active artifact packages. The
  package-library view only offers artifact packages that match the selected
  module definition's declared compatibility range and shows whether the current
  installation already has the same, newer, older, or missing artifact versions.
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
import is intentionally strict for unattended decisions, but complete module
packages are processed item by item. Identical existing artifacts are skipped,
compatible missing artifacts are imported, and only the highest compatible
artifact version per app slot is selected automatically. Invalid filenames,
unknown module/app/package combinations, and changed hashes for an existing
artifact identity are recorded as import errors instead of silently changing
database state.
