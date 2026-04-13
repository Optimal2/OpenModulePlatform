# Worker runtime scaffold

This document describes the initial worker runtime scaffold added to the public repository.

## Purpose

The new scaffold prepares OpenModulePlatform for a future runtime model where:

- one host can run one WorkerManager process for a worker category
- the WorkerManager supervises multiple child worker processes
- each child process runs one app instance
- the app-specific logic can later be delivered through dedicated worker assemblies

## Current projects

### OpenModulePlatform.WorkerManager.WindowsService
Windows Service scaffold for the future manager layer.

### OpenModulePlatform.WorkerProcessHost
Executable scaffold for the future child process host.

### OpenModulePlatform.Worker.Abstractions
Shared contracts for the future worker runtime.

## Non-goals for the current commit

The current scaffold does **not** yet implement:

- host discovery
- database-driven worker scheduling
- process supervision
- dynamic assembly loading
- plugin execution

Those parts should be added in later changes once the contract model is finalized.
