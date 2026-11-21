# OpenModulePlatform.Worker.Abstractions

This project contains the contracts shared by the worker manager, the worker process host, and worker plugins.

Current contents:

- `IWorkerModule` and `IWorkerModuleFactory` define plugin execution and construction.
- `WorkerExecutionContext` carries the app-instance and worker-instance identity, the worker type key, the plugin assembly path, the configuration JSON, the start time, and the optional drain coordinator.
- `IWorkerDrainCoordinator` lets a plugin participate in coordinated draining.
- `WorkerPluginCompatibilityManifest` and `WorkerHostCompatibilityRequirement` define the portable worker-plugin/worker-host compatibility contract.

The assembly contains contracts and models, not the worker supervisor or process host. Those implementations live in `OpenModulePlatform.WorkerManager.WindowsService` and `OpenModulePlatform.WorkerProcessHost`.

Worker plugin components that use a newer host contract declare
`minWorkerHostVersion` in `omp-components.json`. Repository packaging writes the
requirement as `omp-worker-plugin.json` in the plugin artifact; do not hand-edit
that generated file.
