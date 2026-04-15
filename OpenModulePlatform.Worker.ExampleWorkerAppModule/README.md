# Example WorkerAppModule worker plugin

This project is a sample OMP worker plugin for the manager-driven Windows worker runtime.

It demonstrates a worker app that:

- uses `AppInstanceId` as its runtime identity
- runs inside `OpenModulePlatform.WorkerProcessHost`
- reads desired configuration from `omp.AppInstances`
- loads module-owned configuration from its own schema
- claims and processes example jobs
- relies on the worker manager for process supervision, heartbeat, and observed runtime state

## Runtime model

The plugin does not run as its own Windows Service.
Instead, it is loaded dynamically by the generic worker process host after the worker manager resolves the app instance from OMP.

The expected `omp.AppWorkerDefinitions` mapping for this example uses:

- `RuntimeKind = windows-worker-plugin`
- `WorkerTypeKey = omp.example.workerapp_module`
- `PluginRelativePath = OpenModulePlatform.Worker.ExampleWorkerAppModule.dll`

## Publishing

Publish the project as part of the worker plugin artifact for the target host.
The published output must be placed under the install root used by the worker manager so that the plugin DLL is reachable through the configured `PluginRelativePath`.
