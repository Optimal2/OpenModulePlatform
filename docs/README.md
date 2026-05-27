# OpenModulePlatform Documentation

This folder contains the main technical documentation for OpenModulePlatform.
Start here when you need to understand the platform model, deployment objects,
HostAgent automation, or local development workflow.

## Recommended Reading Order

For a quick orientation:

1. `TERMINOLOGY.md`
2. `ARCHITECTURE.md`
3. `CODEX_DEVELOPMENT.md`

For deployment and packaging work:

1. `PORTABLE_DEPLOYMENT_OBJECTS.md`
2. `MODULE_DEFINITIONS.md`
3. `ARTIFACT_PACKAGES.md`
4. `CONFIG_OVERLAYS.md`
5. `UNIVERSAL_MODULE_PACKAGES.md`
6. `HOST_AGENT_FIRST_INSTALL.md`
7. `HOST_AGENT.md`

For repository and release work:

1. `OMP_COMPONENT_MANIFEST.md`
2. `VERSIONING_AND_IDENTITIES.md`
3. `CODEX_DEVELOPMENT.md`

## Platform Model

- `TERMINOLOGY.md` - glossary for platform, deployment, and operations terms.
- `ARCHITECTURE.md` - platform model and request flows.
- `AUTHENTICATION_AND_RBAC.md` - shared OMP authentication, users, role principals, and RBAC.
- `VERSIONING_AND_IDENTITIES.md` - artifact versioning and stable identity policy.

## Deployment Objects

- `PORTABLE_DEPLOYMENT_OBJECTS.md` - module-definition and artifact-package object standards.
- `MODULE_DEFINITIONS.md` - versioned module definition documents and SQL ownership.
- `ARTIFACT_PACKAGES.md` - manifest-based artifact package envelope and configuration-file packaging.
- `CONFIG_OVERLAYS.md` - host configuration and host-specific config overlay object standard.
- `UNIVERSAL_MODULE_PACKAGES.md` - universal zip container for OMP portable objects.

## Runtime And Hosting

- `HOST_AGENT.md` - HostAgent runtime behavior and automation responsibilities.
- `WORKER_RUNTIME.md` - worker runtime concepts.
- `worker-runtime-windows.md` - Windows-specific worker runtime notes.
- `HOSTING_WINDOWS_IIS.md` - IIS hosting guidance.
- `LOGGING.md` - logging conventions.

## Installation And Operations

- `HOST_AGENT_FIRST_INSTALL.md` - HostAgent-first package and bootstrapper flow.
- `HOST_AGENT_TEMPLATE_AUTOMATION.md` - installation profile and host automation notes.
- `ADMIN_CONFIGURATION.md` - Portal administration guidance.
- `PROJECT_STATUS.md` - current project status notes.
- `SECURITY_AUDIT_2026-05-24.md` - security audit notes from May 24, 2026.

## Development

- `CODEX_DEVELOPMENT.md` - agent-friendly repository map, validation ladder, and local publish workflow.
- `OMP_COMPONENT_MANIFEST.md` - repository component manifest and version-bump helper usage.
