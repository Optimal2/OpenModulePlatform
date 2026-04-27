# Example WorkerAppModule Web App

This project is the administrative web application for the sample manager-driven worker module.

It demonstrates:

- module-specific configuration storage
- app-instance-centric administration
- visibility into example jobs and worker runtime state
- how a module web app can surface `omp.AppWorkerDefinitions` and `omp.AppInstanceRuntimeStates` indirectly through module-specific views

The module uses `omp.AppInstances` as the runtime anchor for the worker app.
Each worker app instance can carry its own artifact, configuration and desired state, while the worker manager publishes the observed runtime state separately.
