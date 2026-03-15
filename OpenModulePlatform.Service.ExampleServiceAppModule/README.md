# OpenModulePlatform.Service.ExampleServiceAppModule

This project is a reference implementation of an OMP service application.

## Runtime responsibilities

- heartbeat and verification against `omp.HostInstallations`
- configuration refresh from `omp_example_serviceapp_module.Configurations`
- job claim and completion in `omp_example_serviceapp_module.Jobs`
- execution logging in `omp_example_serviceapp_module.JobExecutions`

## Installation

Publish the project and run `Deploy/Install-Service.ps1` from an elevated PowerShell session. The script can be executed either from the publish folder or directly from the `Deploy` folder in the project tree.
