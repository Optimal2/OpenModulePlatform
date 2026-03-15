# Example ServiceAppModule Service App

This project is a sample OMP service app. It demonstrates a service-backed app instance that:

- reads its runtime state from `omp.AppInstances`
- resolves its configuration from the module schema
- claims and processes example jobs
- reports heartbeat and observed identity back to OMP

## Runtime identity

The service identifies itself by `Worker:AppInstanceId` in `appsettings.json`.
That app instance row controls the effective artifact, configuration, desired state and expected identity for the running service.

## Installation

Publish the service app, then run `Deploy/Install-Service.ps1` from the published output folder or directly from the project `Deploy` folder.
The script copies the published files to `Program Files` and registers the Windows service.
