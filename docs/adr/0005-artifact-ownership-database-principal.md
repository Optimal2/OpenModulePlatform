# ADR 0005: Artifact ownership enforced by a database principal

## Status

Proposed (2026-09-02). **Decision support only — the permission change is
operator-gated and is intentionally not implemented here.**

## Context

Commit `edf72d49` ("Guard module SQL artifact ownership") added a text-based
guard, mirrored in three places
(`OpenModulePlatform.Bootstrapper/Program.cs`,
`OpenModulePlatform.HostAgent.Runtime/Services/OmpHostArtifactRepository.cs`,
`OpenModulePlatform.Portal/Services/OmpAdminRepository.Editor.cs`), that blocks
module-definition SQL from writing `omp.Artifacts` and the artifact pointer
columns `omp.AppInstances.ArtifactId` and
`omp.InstanceTemplateAppInstances.DesiredArtifactId`.

An independent review (2026-09-01) ran a probe harness against the compiled
validators and found multiple independent bypasses. The 2026-09-02 hardening
round closed the regex-reachable ones (OUTPUT INTO, positional INSERT, compound
assignment, CTE writes, truncated assignment scans, DELETE, and the
`GO n`/DELETE-FK mirror divergence). Two families remain open **by design**,
because no text-based scanner can close them:

1. **Dynamic SQL.** `EXEC(N'INSERT INTO omp.Artifacts ...')` is invisible to
   the guard because string literals are blanked before scanning. Blocking
   `EXEC` is not an option: the platform's own scripts use
   `EXEC(N'CREATE SCHEMA ...')` as an idiom.
2. **The stored-module-body exemption.** Batches that start with
   `CREATE/ALTER PROCEDURE|TRIGGER|FUNCTION` are excluded from the ownership
   scan because the platform's own `omp.MaterializeInstanceTemplate`
   legitimately writes the pointer columns. Any module can therefore hide a
   write inside a procedure body — including an `ALTER PROCEDURE` that
   overwrites the platform procedure itself.

Smaller residual holes exist for the same reason (writes through views,
synonyms, or inline table-valued functions; `UPDATE`-with-JOIN shapes whose
`FROM` starts on another table; `ALTER TABLE ... SWITCH`). A regular-expression
guard cannot see through indirection; only the database engine can.

## Problem

As long as the text guard is treated as the security boundary, it promises a
protection it cannot keep. The boundary has to move to a layer where bypassing
it requires defeating the database engine's permission checks, not a regex.

## Proposal

Run module-definition SQL under a database principal that **lacks write
permission** on the owned surface:

- `INSERT`, `UPDATE`, `DELETE` on `omp.Artifacts`
- `UPDATE` on `omp.AppInstances.ArtifactId`
- `UPDATE` on `omp.InstanceTemplateAppInstances.DesiredArtifactId`

Concretely (column-level `DENY` on the pointer columns, table-level `DENY` on
`omp.Artifacts`, applied to a dedicated role such as `omp_module_sql_executor`,
with Portal's repair path and any HostAgent-side module-SQL execution wrapped
in `EXECUTE AS` / `REVERT` around a user in that role).

The text guard stays, but its role changes: it becomes **early validation with
a friendly error message**, not the security mechanism. A module author gets
the clear "artifact registration is owned by the artifact import path" message
at import/validation time instead of an engine permission error mid-script.

### The auto-apply exemption must be a GRANT, not a text exemption

The platform's own writes are:

- the artifact import path (registers rows in `omp.Artifacts`), and
- artifact auto-apply
  (`ApplyImportedArtifactToMatchingApplicationsAsync`, which sets
  `DesiredArtifactId`), plus the materialization procedure
  `omp.MaterializeInstanceTemplate`.

These must keep working when module SQL runs under the restricted principal:

- **Import and auto-apply run in C# under the HostAgent/Portal identities**,
  not inside module SQL, so they are unaffected as long as those identities
  hold explicit `GRANT`s. The exemption is expressed as permissions on the
  platform identities — never as a text carve-out in the guard.
- **`omp.MaterializeInstanceTemplate` is invoked from module SQL.** If the
  procedure and the `omp` tables share the same owner (both `dbo` today),
  ownership chaining lets the restricted principal execute the procedure and
  write through it without holding table permissions. This is the intended
  owner path and should be preserved deliberately: keep procedure and tables
  under one owner, and do not add dynamic SQL inside the procedure (dynamic
  SQL breaks ownership chaining and would silently fail under the restricted
  principal).

## What this breaks / costs

- **Setup and upgrade SQL must grant the new role correctly.** Existing
  databases need an upgrade script; fresh installs need the role, user, and
  `DENY`/`GRANT` statements in `sql/1-setup-openmoduleplatform.sql`.
- **Portal repair currently runs as the Portal identity**, which today can do
  anything its login allows. Wrapping repair execution in `EXECUTE AS` changes
  which engine permissions apply to module scripts (for example cross-schema
  writes to the module's own schema must be granted to the role, not to the
  Portal login). Every shipped module script must be re-verified under the
  restricted principal before rollout.
- **Ownership chaining is subtle.** A future refactor that moves
  `omp.MaterializeInstanceTemplate` to a differently-owned schema, or
  introduces dynamic SQL inside it, breaks the model. This needs a regression
  test at the database level, not just a code comment.
- **Bootstrapper setup runs as a high-privilege principal** (it creates the
  database). Only the module-definition import/apply path should switch to the
  restricted principal; setup scripts are platform-owned and stay unrestricted.
- **Operational gate.** Changing permissions on customer databases is a
  managed change (customer change windows, rollback plan). That is why this
  ADR ships as decision support only.

## Alternatives considered

- **Keep hardening the regex guard.** Rejected for the open families: dynamic
  SQL and procedure bodies cannot be analyzed without a full T-SQL parser, and
  even a parser cannot resolve synonyms/views without catalog access.
- **Ship a real T-SQL parser** (for example ScriptDom) and analyze batches
  including procedure bodies. Raises the bar significantly but still cannot
  resolve dynamic SQL built at runtime, and adds a dependency to three mirrors.
  A reasonable intermediate step; not a boundary.
- **Do nothing beyond the 2026-09 hardening.** Leaves the two known holes
  documented but open. Acceptable only if the guard is explicitly framed as
  early validation, which is what the hardening round does.

## Decision

TBD — pending owner decision and customer change window (operator-gated, B2).
Until then the text guard is documented as early validation, not a security
boundary; see `docs/MODULE_DEFINITIONS.md`, "Module SQL safety guard".
