# OMP Object Builder

`build-repository-objects.ps1` reads a repository's `omp-components.json` and
creates portable OMP objects:

```text
module-definitions/
artifacts/
host-configs/
config-overlays/
```

Use this script when a module repository needs to publish objects for Portal,
HostAgent import folders, or installer package libraries. Runtime or
customer-specific configuration should be supplied through command-line
mappings, not committed to the public repository.
