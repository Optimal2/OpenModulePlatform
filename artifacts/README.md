# Local Artifact Output

This folder is intentionally empty in Git.

Some developer scripts use `artifacts/` as a local output or search folder for
generated artifact packages. Those files are build outputs, not source of truth,
and must not be committed to this public repository.

Current installable objects are produced from:

- `omp-components.json`
- module definition JSON files at module roots
- `scripts/omp/build-repository-objects.ps1`
- `scripts/deployment/package-hostagent-first.ps1`
- the installer sync action in `installer/`

If old local payloads are needed for investigation, keep them in the private
DEV repository's ignored generated archive, currently `OLD/G`.
