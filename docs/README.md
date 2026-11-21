# OpenModulePlatform Documentation

This folder contains the technical, operator, and development documentation for
OpenModulePlatform. Start here when you need the platform model, deployment
objects, HostAgent automation, installation, or local development workflow.

## Recommended reading order

For a quick orientation:

1. [TERMINOLOGY.md](TERMINOLOGY.md)
2. [ARCHITECTURE.md](ARCHITECTURE.md)
3. [CODEX_DEVELOPMENT.md](CODEX_DEVELOPMENT.md)

For deployment and packaging work:

1. [PORTABLE_DEPLOYMENT_OBJECTS.md](PORTABLE_DEPLOYMENT_OBJECTS.md)
2. [MODULE_DEFINITIONS.md](MODULE_DEFINITIONS.md)
3. [ARTIFACT_PACKAGES.md](ARTIFACT_PACKAGES.md)
4. [CONFIG_OVERLAYS.md](CONFIG_OVERLAYS.md)
5. [UNIVERSAL_MODULE_PACKAGES.md](UNIVERSAL_MODULE_PACKAGES.md)
6. [HOST_AGENT_FIRST_INSTALL.md](HOST_AGENT_FIRST_INSTALL.md)
7. [HOST_AGENT.md](HOST_AGENT.md)

For repository and release work:

1. [OMP_COMPONENT_MANIFEST.md](OMP_COMPONENT_MANIFEST.md)
2. [VERSIONING_AND_IDENTITIES.md](VERSIONING_AND_IDENTITIES.md)
3. [CODEX_DEVELOPMENT.md](CODEX_DEVELOPMENT.md)

## Platform model

- [TERMINOLOGY.md](TERMINOLOGY.md) — platform, deployment, and operations glossary.
- [ARCHITECTURE.md](ARCHITECTURE.md) — the architecture description: scope, components, runtime flows, data, integrations, configuration, security, build and deployment, operations, and known limits.
- [PROJECT_STATUS.md](PROJECT_STATUS.md) — current maturity, limitations, and priorities.
- [AUTHENTICATION_AND_RBAC.md](AUTHENTICATION_AND_RBAC.md) — shared authentication, users, role principals, and RBAC.
- [VERSIONING_AND_IDENTITIES.md](VERSIONING_AND_IDENTITIES.md) — artifact versioning and stable identity policy.
- [TIME_ZONE.md](TIME_ZONE.md) — presentation time zone, calendar rules, and UTC boundaries.

## Deployment objects and packages

- [PORTABLE_DEPLOYMENT_OBJECTS.md](PORTABLE_DEPLOYMENT_OBJECTS.md) — portable-object overview and transport flow.
- [MODULE_DEFINITIONS.md](MODULE_DEFINITIONS.md) — versioned module definitions, SQL ownership, repairs, and runtime-maintenance steps.
- [ARTIFACT_PACKAGES.md](ARTIFACT_PACKAGES.md) — artifact package envelope and configuration-file packaging.
- [CONFIG_OVERLAYS.md](CONFIG_OVERLAYS.md) — host configurations, exact-host and AllHosts overlay objects, selection, and precedence.
- [CONFIG_OVERLAY_ADMINISTRATION.md](CONFIG_OVERLAY_ADMINISTRATION.md) — operator guide for `/admin/configoverlays`.
- [CONFIG_OVERLAY_ADMIN_CONTRACT.md](CONFIG_OVERLAY_ADMIN_CONTRACT.md) — Portal administration service and authorization contract.
- [UNIVERSAL_MODULE_PACKAGES.md](UNIVERSAL_MODULE_PACKAGES.md) — universal zip manifest, folders, import/export, and package-builder behavior.

## Runtime and modules

- [HOST_AGENT.md](HOST_AGENT.md) — HostAgent runtime, configuration, deployment, self-upgrade, jobs, and diagnostics.
- [HOST_AGENT_TEMPLATE_AUTOMATION.md](HOST_AGENT_TEMPLATE_AUTOMATION.md) — installation-profile materialization and placement.
- [WORKER_RUNTIME.md](WORKER_RUNTIME.md) — worker runtime contracts, provisioning, placement, and observed state.
- [worker-runtime-windows.md](worker-runtime-windows.md) — Windows process and identity model.
- [PUSH_EVENTS.md](PUSH_EVENTS.md) — SQL outbox, dispatcher, SignalR delivery, and fallback polling.
- [CONTEXT_GUARD_MODULE.md](CONTEXT_GUARD_MODULE.md) — authenticated one-time hand-offs, live-context scopes, settings, and v1 limits.
- [CHANNEL_TYPE_RECONCILE.md](CHANNEL_TYPE_RECONCILE.md) — module-owned channel-type reconciliation contract and template.
- [OPEN_DOC_VIEWER_EXAMPLES.md](OPEN_DOC_VIEWER_EXAMPLES.md) — host-side CSP requirements for the OpenDocViewer embed examples.
- [LOGGING.md](LOGGING.md) — NLog configuration, files, process separation, and the central system log.
- [CONTENT_SECURITY_POLICY.md](CONTENT_SECURITY_POLICY.md) — CSP rollout, per-app policies, and documented exceptions.
- [THEME_CONTRACT.md](THEME_CONTRACT.md) — light/dark tokens, stored preference, and app adoption.

## Installation and operations

- [HOST_AGENT_FIRST_INSTALL.md](HOST_AGENT_FIRST_INSTALL.md) — HostAgent-first package, bootstrapper, upgrade, and maintenance flow.
- [FIRST_INSTALLER.md](FIRST_INSTALLER.md) — first-install-only installer checks, operation, and dry-run exit codes.
- [FRESH_INSTALL.md](FRESH_INSTALL.md) — build and operate a prepared new-server package.
- [HOSTING_WINDOWS_IIS.md](HOSTING_WINDOWS_IIS.md) — Windows, IIS, runtime, proxy, and hosting guidance.
- [ADMIN_CONFIGURATION.md](ADMIN_CONFIGURATION.md) — Portal administration and runtime configuration guidance.
- [CODE_SIGNING.md](CODE_SIGNING.md) — optional Azure Trusted Signing pipeline and verification.
- [SECURITY_AUDIT_2026-05-24.md](SECURITY_AUDIT_2026-05-24.md) — dated security-audit baseline; its status note identifies superseded findings.

## Development and validation

- [CODEX_DEVELOPMENT.md](CODEX_DEVELOPMENT.md) — repository map, validation ladder, and local publish workflow.
- [OMP_COMPONENT_MANIFEST.md](OMP_COMPONENT_MANIFEST.md) — component manifest and version-bump tooling.
- [VALIDATOR_CHECKS.md](VALIDATOR_CHECKS.md) — canonical numbered checks for `scripts/omp/validate-component-versions.ps1`.
- [DETERMINISTIC_BUILDS.md](DETERMINISTIC_BUILDS.md) — deterministic publish and package inputs, probes, and CI coverage.
- [TEST_DEBT.md](TEST_DEBT.md) — deliberate test-gate exclusions, reasons, and re-enable criteria.

## `conventions/` — standards and dated audits

Each document defines a cross-cutting standard and records the ecosystem audit on
which it was based. Audit tables and `file:line` anchors are point-in-time
evidence, not current version references. Use the linked source files and
manifests for current facts.

- [conventions/README.md](conventions/README.md) — naming scheme and audit index.
- [conventions/code-style.md](conventions/code-style.md) — editor, build, SDK, analyzer, and JS/TS conventions.
- [conventions/configuration.md](conventions/configuration.md) — options, validation, connection strings, secrets, and overlays.
- [conventions/data-access.md](conventions/data-access.md) — ADO.NET, factories, SQL location, transactions, and schemas.
- [conventions/dependency-injection.md](conventions/dependency-injection.md) — registrations, lifetimes, hosted services, and options.
- [conventions/error-handling.md](conventions/error-handling.md) — exception policy, problem details, and correlation.
- [conventions/http-clients.md](conventions/http-clients.md) — HTTP clients, handlers, timeouts, and retries.
- [conventions/logging.md](conventions/logging.md) — NLog layouts, scopes, sinks, and levels.
- [conventions/unit-testing.md](conventions/unit-testing.md) — xUnit, fixtures, naming, parallelism, and test gates.

## `adr/` — architecture decision records

ADRs record why a design was chosen. Their status and linked current guides show
whether the decision is implemented.

- [0001-module-configid-bridge.md](adr/0001-module-configid-bridge.md) — typed `ModuleConfigId` bridge with opt-in validation.
- [0002-deploy-set-consistency-check.md](adr/0002-deploy-set-consistency-check.md) — HostAgent deploy-set consistency check.
- [0003-webshared-private-consumer-cascade.md](adr/0003-webshared-private-consumer-cascade.md) — Web.Shared private-consumer cascade awareness (validator Check 14).
- [0004-channel-type-reconcile-generalization.md](adr/0004-channel-type-reconcile-generalization.md) — generalized channel-type reconciliation.
- [0005-artifact-ownership-database-principal.md](adr/0005-artifact-ownership-database-principal.md) — operator-gated proposal for database-principal ownership enforcement.
- [0006-config-overlay-artifactversion-minimum.md](adr/0006-config-overlay-artifactversion-minimum.md) — overlay `artifactVersion` as a minimum.
- [0007-config-overlay-all-hosts-scope.md](adr/0007-config-overlay-all-hosts-scope.md) — installation-wide AllHosts overlay scope.

## `runbooks/` — focused incident guidance

- [schema-ligger-efter-efter-import.md](runbooks/schema-ligger-efter-efter-import.md) — diagnose and repair a schema that lags an imported module definition.

## `quality/` — dated finding-disposition evidence

These files record specific review campaigns and commits. They are historical
evidence, not a current list of open findings.

- `github-fynd-ai-forslag-alla-repon-omp.md`
- `github-fynd-standardfynd-publika-repon-final-integration.md`
- `github-fynd-standardfynd-publika-repon-wp1.md`
- `github-fynd-standardfynd-publika-repon-wp2.md`
- `github-fynd-standardfynd-publika-repon-wp2-console-rationale.md`
- `github-fynd-standardfynd-publika-repon-wp3.md`
- `github-fynd-standardfynd-publika-repon-wp4-missed-using.md`
- `github-fynd-standardfynd-publika-repon-wp4-readonly-rationale.md`
- `github-fynd-standardfynd-publika-repon-wp4-select.md`
- `github-fynd-standardfynd-publika-repon-wp4-where-rationale.md`

## `img/` — README screenshots

Portal screenshots come from a local development installation. Non-public module
rows are removed and the host name is masked before capture.
