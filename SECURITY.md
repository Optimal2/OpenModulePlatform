<!-- File: SECURITY.md -->
# Security Policy

OpenModulePlatform aims to provide a safe public baseline for modular platform work.
Security issues should be reported privately before public disclosure.

## Supported versions

| Version | Supported |
| --- | --- |
| 0.1.x | Yes |
| < 0.1.0 | No |

## Reporting a vulnerability

Use GitHub private vulnerability reporting for the repository if that feature is enabled.
If private vulnerability reporting is not enabled, contact the project maintainers through a private channel before disclosing details publicly.

Please include, when possible:

- a clear description of the issue
- affected components and versions
- reproduction steps or a proof of concept
- impact assessment
- any suggested remediation

## What to expect

Maintainers should aim to:

- acknowledge receipt within a reasonable timeframe
- assess whether the report is valid and in scope
- coordinate a fix and release plan before public disclosure when practical

No specific response-time SLA is guaranteed for the public beta release line.

## Scope and assumptions

This repository intentionally contains no customer-specific integrations, credentials, or environment-specific deployment secrets.

The SQL bootstrap scripts require operator-provided values such as `@BootstrapPortalAdminPrincipal`. These values are installation inputs, not working credentials. Prefer the local PowerShell installer for automated bootstrap runs because it escapes principal values before invoking `sqlcmd`.

## Operational guidance

- protect the `OmpDb` connection string with standard secret-management practices
- avoid `ForwardedHeadersTrustAllProxies` outside tightly controlled environments
- review bootstrap RBAC principals before exposing the Portal to real users
- do not treat the example service app as production-hardened automation
- review authentication, proxy, and cookie settings before exposing any OMP web application to the internet
