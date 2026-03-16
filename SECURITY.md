<!-- File: SECURITY.md -->
# Security Policy

Security issues should be reported privately to the maintainers before public disclosure.

This repository is the open source baseline of OMP and intentionally contains no
customer-specific integrations, credentials or environment-specific deployment secrets.

## Scope

The repository still includes placeholder values such as `REPLACE_ME` in SQL bootstrap
scripts. Those placeholders are deliberate and must be replaced by the operator during
installation. They are not working credentials.

## Operational notes

- protect the `OmpDb` connection string with normal secret-management practices
- avoid `ForwardedHeadersTrustAllProxies` outside tightly controlled environments
- review bootstrap RBAC principals before exposing Portal to real users
- do not treat the example service app as production-hardened automation
