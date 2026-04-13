# OpenModulePlatform.WorkerProcessHost

This project is the future child process host for OMP worker plugins.

Current state:

- compiles as a minimal executable scaffold
- contains no plugin loading or worker execution logic yet
- exists to make the runtime split explicit in the solution

Planned responsibility:

- start one worker runtime for one app instance
- load a worker implementation from a dedicated assembly
- isolate worker execution from the WorkerManager process
