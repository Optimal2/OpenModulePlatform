# Example ServiceAppModule Web App

This project is the administrative web application for the sample service-backed OMP module.

It demonstrates:

- module-specific configuration storage
- app-instance-centric administration
- visibility into example jobs and runtime state

The module uses `omp.AppInstances` as the runtime anchor for the service app. Each service app instance can carry its own artifact, configuration and verification policy.
