# OpenModulePlatform.Worker.Abstractions

This project contains the minimal contracts shared by the worker manager, the worker process host, and future worker plugins.

Current state:

- contains only small interface and model placeholders
- defines no behavior beyond the basic shape of a worker module

Planned responsibility:

- provide stable cross-project contracts for worker execution
- minimize coupling between the manager, host, and plugin projects
- expose the portable worker-plugin/worker-host compatibility metadata contract

Worker plugin components that use a newer host contract declare
`minWorkerHostVersion` in `omp-components.json`. Repository packaging writes the
requirement as `omp-worker-plugin.json` in the plugin artifact; do not hand-edit
that generated file.
