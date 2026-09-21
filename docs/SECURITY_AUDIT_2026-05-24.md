# Security Audit 2026-05-24

> **Status note (2026-09-21).** This document is a dated baseline and is kept
> as the record of what was reviewed and fixed on 2026-05-24. It is not the
> current state. Since then:
>
> - A Content Security Policy is emitted by every OMP web app through the
>   shared pipeline (`OpenModulePlatform.Web.Shared/Security/OmpContentSecurityPolicy.cs`,
>   `OmpWebHostingExtensions.UseOmpSecurityHeaders`), in report-only mode by
>   default with enforcement as a configuration flip; see
>   `docs/CONTENT_SECURITY_POLICY.md`. The "full CSP not enabled" finding
>   below is therefore superseded.
> - `Strict-Transport-Security` (`max-age=31536000; includeSubDomains`) is
>   emitted on HTTPS requests by the same middleware; it is not in the header
>   list under "Fixes Applied".
> - The localization endpoint `/localization/set-language` is deliberately
>   `AllowAnonymous` in both the shared pipeline and the Auth app (a
>   sign-in-gated language switch redirected the language GET to the login
>   page); its protection is the local-return-URL check, not authentication.
>   Read the "localization return URL endpoint is protected by the normal auth
>   flow" line below with that in mind.
> - Code scanning runs as GitHub CodeQL default setup on the repository
>   (languages `csharp`, `python`, `actions`; weekly schedule plus
>   push/pull-request analysis), configured in the repository settings rather
>   than in a workflow file, so `.github/workflows/` does not show it. Code
>   comments that cite CodeQL alert numbers (for example
>   `OpenModulePlatform.Web.Shared/Security/OmpLogSanitizer.cs`) refer to
>   those findings. `gitleaks` and `semgrep` are still not part of CI.

This document records the security audit baseline performed for the OMP-related repositories on 2026-05-24.

This public copy generalizes private consumer repository names where the exact
repository identity is not needed to understand the audit outcome.

## Scope

Reviewed repositories:

- OpenModulePlatform
- OpenDocViewer
- selected private OMP consumer repositories

The private DEV repository was treated as installation material only. It was scanned for accidental secret exposure patterns, but this report does not include customer-specific values.

## Checks Performed

- Dependency vulnerability checks:
  - `dotnet list <solution> package --vulnerable --include-transitive`
  - `npm audit --audit-level=moderate`
- Secret and configuration review using repository-wide pattern searches for password, token, connection string, and customer account markers.
- Manual review of security-sensitive code paths:
  - authentication and return URL handling
  - portal admin upload/import flows
  - artifact and module-definition ZIP processing
  - SQL execution boundaries for module repair/import
  - runtime configuration handling
  - HTML rendering and script injection surfaces
- Basic local HTTP checks against `http://localhost:8088/`:
  - unauthenticated admin access redirects to login
  - external login return URLs are not immediately redirected to off-site targets
  - localization return URL endpoint is protected by the normal auth flow

## Fixes Applied

### OpenModulePlatform

- Added shared baseline HTTP security headers for OMP web applications:
  - `X-Content-Type-Options: nosniff`
  - `Referrer-Policy: same-origin`
  - `X-Frame-Options: SAMEORIGIN`
  - `Permissions-Policy: camera=(), microphone=(), geolocation=()`
- Enabled the same header middleware in the Auth app.
- Hardened artifact package extraction by limiting manifest and configuration file sizes before reading ZIP entries into memory.
- Hardened config overlay detection and reading:
  - oversized JSON files are no longer parsed during kind detection
  - malformed JSON during kind detection is treated as unknown input
  - external config overlay file entries are size-limited before loading
- Bumped affected OMP module definitions and component manifest versions so the changes can flow into new runtime artifacts.

### OpenDocViewer

- Updated the vulnerable transitive `qs` package from `6.15.0` to `6.15.2` through `npm audit fix`.
- Sanitized site-local manual HTML with DOMPurify before rendering it with `dangerouslySetInnerHTML`.
- Kept relative URL rewriting for manual assets and links, but the final HTML is now sanitized after rewriting.
- Bumped the OpenDocViewer package, component manifest, module definition, and SQL registration version to `2.0.4`.

### One private consumer module

- Removed avoidable `innerHTML` use in the collisions list UI and replaced it with DOM node creation.
- Bumped the affected private consumer module definition and related artifact versions to keep the security fix deployable through OMP artifacts.

## Findings Not Changed

- A full Content Security Policy was not enabled yet. Several legacy OMP module/admin pages still rely on trusted inline scripts and styles. The new shared header middleware intentionally documents this so a future CSP rollout can be done as a planned compatibility task instead of a partial break.
- Customer-specific SQL and artifact config overlays can legitimately contain environment-specific values. The audit focused on keeping those values out of public repositories and on ensuring they are handled through installation/config channels.

## Tooling Gaps

- `gitleaks` was not installed in this environment.
- `semgrep` was not installed in this environment.

The repository pattern scan did not replace those tools; adding both to CI would improve repeatability.

## Validation

Successful validation commands:

- `dotnet build OpenModulePlatform.slnx`
- `dotnet build <private-consumer-solution>` for each reviewed private consumer repository
- `npm audit --audit-level=moderate`
- `npm run lint`
- `npm run build`

Dependency vulnerability checks reported no vulnerable NuGet packages for the reviewed .NET solutions after the audit. OpenDocViewer reported zero moderate-or-higher npm vulnerabilities after the `qs` update.
