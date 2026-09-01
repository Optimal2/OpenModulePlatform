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
- `PUSH_EVENTS.md` - push event model for web apps, service apps, workers, and UI refresh hints.
- `OPEN_DOC_VIEWER_EXAMPLES.md` - host-side CSP requirements for the OMP OpenDocViewer embed examples.
- `LOGGING.md` - logging conventions.

## Installation And Operations

- `HOST_AGENT_FIRST_INSTALL.md` - HostAgent-first package and bootstrapper flow.
- `HOST_AGENT_TEMPLATE_AUTOMATION.md` - installation profile and host automation notes.
- `ADMIN_CONFIGURATION.md` - Portal administration guidance.
- `PROJECT_STATUS.md` - current project status notes.
- `SECURITY_AUDIT_2026-05-24.md` - security audit notes from May 24, 2026.
- `CONTENT_SECURITY_POLICY.md` - CSP rollout model, per-app policies, and documented exceptions.

## Development

- `CODEX_DEVELOPMENT.md` - agent-friendly repository map, validation ladder, and local publish workflow.
- `OMP_COMPONENT_MANIFEST.md` - repository component manifest and version-bump helper usage.
- `CODE_SIGNING.md` - signing of the installer runner and what the gate refuses.
- `TEST_DEBT.md` - deliberately excluded or skipped tests, with the reason for each. Kept in
  the same commit as the change that adds an entry (see `CONTRIBUTING.md`/`AGENTS.md`).

## Subdirectories

Three folders under `docs/` hold material this index used to omit entirely. If you are
looking for "how do we do X across the whole platform" or "why is it built this way",
the answer is more likely here than in a top-level file.

### `conventions/` - one standard per cross-cutting concern

Eight audit documents that map the current state (file:line) and then set ONE standard per
cross-cutting concern across all ten repositories. Use them as the template for a new module
and as the reference when hardening an existing one.

`code-style.md`, `configuration.md`, `data-access.md`, `dependency-injection.md`,
`error-handling.md`, `http-clients.md`, `logging.md`, `unit-testing.md`.

### `adr/` - architecture decision records

Decisions with their reasoning and status, so a later reader can tell a deliberate design
from an accident.

- `0001-module-configid-bridge.md` - typed `ModuleConfigId` bridge with opt-in validation.
- `0002-deploy-set-consistency-check.md` - HostAgent deploy-set consistency check.
- `0003-webshared-private-consumer-cascade.md` - Web.Shared private-consumer cross-repo
  cascade awareness. This is the reasoning behind Check 14.
- `0004-channel-type-reconcile-generalization.md` - channel-type reconcile generalization.

### `runbooks/` - what to do when a specific thing has gone wrong

- `schema-ligger-efter-efter-import.md` - the `ConfigSchemaJson` lagging a generation behind
  the applied module definition after an import, and how to tell it apart from a real gap.
