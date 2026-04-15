# OpenModulePlatform.WorkerProcessHost

This project is the generic child process host for plugin-based OMP workers.

Current state:

- runs as a real worker process host
- accepts `AppInstanceId`, `WorkerTypeKey`, and `PluginAssemblyPath` from configuration providers, including command-line overrides
- loads a worker plugin assembly dynamically and resolves a matching `IWorkerModuleFactory`
- creates a runtime context and executes the worker module inside the child process
- supports an optional named OS event for graceful external shutdown requests from the worker manager
- returns `0` for normal completion and `1` for startup or execution failure

Current limitations:

- does not yet hydrate runtime state from OMP persistence on its own
- does not yet expose a richer lifecycle contract beyond `RunAsync`
- does not yet implement plugin isolation beyond assembly load context boundaries
