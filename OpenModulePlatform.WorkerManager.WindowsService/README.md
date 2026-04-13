# OpenModulePlatform.WorkerManager.WindowsService

This project is the Windows Service host for the future OMP worker runtime.

Current state:

- compiles as a minimal Windows Service scaffold
- contains no worker orchestration logic yet
- exists to reserve the project structure, package references, and naming conventions

Planned responsibility:

- discover worker app instances assigned to the current host
- supervise child worker processes
- restart or stop workers based on desired state and health
