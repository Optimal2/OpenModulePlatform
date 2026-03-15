# OpenModulePlatform.Service.ExampleServiceAppModule

Example OMP service app / worker.

Behavior:

- heartbeats against `omp.HostInstallations`
- loads active configuration from `omp_example_serviceapp_module.Configurations`
- claims jobs from `omp_example_serviceapp_module.Jobs`
- writes execution results to `omp_example_serviceapp_module.JobExecutions`
