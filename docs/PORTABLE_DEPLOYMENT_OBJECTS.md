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
- idempotent repair SQL embedded directly in the JSON document

Every OMP module should keep its latest source definition file at the module
root and list it in that repository's `omp-components.json`. The generated
HostAgent-first installer copies those files into the package-local
`module-definitions` import folder. Portal stores imported definitions in
`omp.ModuleDefinitionDocuments`.

HostAgent is bootstrap infrastructure and is currently the only accepted
exception. It can be packaged by the installer without being represented as a
normal module definition.

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
- `/admin/artifacts` and `/admin/artifactupload` import artifact packages.
- `/admin/artifactedit` can download an installed artifact as a standard package
  object, including registered configuration files.
- `/admin/artifactpackageeditor` opens the browser-based artifact package editor.

Standalone browser tools:

- `tools/module-definition-editor/index.html`
- `tools/artifact-package-editor/index.html`

Command-line helpers:

- `scripts/dev/embed-module-definition-sql.ps1` refreshes embedded SQL in a
  module definition from its source `.sql` files.
- `scripts/deployment/new-omp-artifact-package.ps1` creates a standard artifact
  package object from a payload folder or payload zip plus optional config files.
- `scripts/deployment/package-hostagent-first.ps1` builds an installer payload
  that contains module definitions and artifact package objects.

## Export And Import Flow

To move a module from one OMP installation to another:

1. Download the module definition JSON from Portal or take it from source.
2. Download or build the compatible artifact package zips.
3. Import the module definition into the target OMP.
4. Validate the module definition matrix and run required safe repairs.
5. Import the artifact packages.
6. Update the desired template state so HostAgent deploys the new versions.

Portal-driven import can offer operator choices for conflicts. HostAgent folder
import is intentionally strict and unattended: invalid filenames, incompatible
module/app combinations, duplicate versions, and changed hashes for an existing
version are recorded as import errors instead of silently changing database
state.

