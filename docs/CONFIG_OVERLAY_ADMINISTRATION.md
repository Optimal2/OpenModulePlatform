# Administer configuration overlays

Open **System > Installation > Configuration overlays**, or follow the
Configuration overlays link on Hosts or Import/export. The page route is
`/admin/configoverlays`; `?id=<document-id>` opens an authorized editor.
Every handler requires the existing Portal administration permission and
mutations use the normal Razor Pages antiforgery mechanism.

## Installation and scope

An installation is the connected Portal database/runtime set. There is no
connection string, database selector, filesystem root or remote installation
parameter on this page. Instance rows are not separate installation boundaries.

The saved list includes disabled versions and overlays whose original host is
no longer registered. Choose **One exact host** and a registered host, or
explicitly choose **All hosts in this installation**. AllHosts stores one
document with the canonical `*` key, including for future hosts; it does not
create copies per host. No missing host key silently broadcasts an overlay.
Overlay identity contains a host key, not an instance ID. If legacy instance
rows register the same key more than once, the page warns and refuses ambiguous
exact-host saves/readback. Resolve that inventory ambiguity first; existing
runtime matching and stored overlays are unchanged.

## Saved list, filters and deleted history

The saved list shows every saved overlay: enabled and disabled versions,
exact-host and AllHosts. Columns cover identity, scope, selectors, origin
(source name), per-file relative paths with their stored merge mode and
enabled state, state, and actions. Filters narrow by host, module, app,
package type, target, state and scope; a host filter also lists AllHosts rows
because they are the defaults that apply to that host. A host key that no
longer exists in this installation is marked in the list, never silently
dropped. Context links from the Artifact and app instance editors open this
page; the artifact link pre-filters by package type and target. The list
shows metadata only; file content and secrets stay in the authorized detail
editor.

**Deleted overlays** is a separate read-only section listing overlays that were
deleted. Each row is a metadata-only tombstone from the audit log: overlay
identity, scope, selectors, file count, who deleted it and when. Deleted rows
are historical records only — they are never applicable overlays and never
editable live documents. Deletion keeps this metadata before the cascading
delete removes the document and its files.

## Add, edit and remove

1. Open a saved overlay or choose **New overlay / discard draft**.
2. Choose the explicit scope and host. These controls supply `hostScope` and
   `hostKey`; values for those fields inside the JSON are overwritten by the
   controls before validation. During edit the scope, key, version and host
   identity must remain unchanged. Create a new document for a new identity.
3. Edit the inline JSON, retaining any selectors you need, including
   `moduleDefinitionVersion`. Optional `moduleKey`, `appKey`, `packageType` and
   `targetName` narrow matching. `artifactVersion` is a minimum version.
4. Supply configuration files with safe `relativePath`, inline `fileContent`
   and optional `mergeMode`. Referenced sidecar paths cannot be resolved in the
   editor. Use Import/export for portable packages with sidecars. The editor
   accepts at most 1,048,576 characters and a 2 MiB request body. Alternatively
   upload a single UTF-8 text file of at most 1 MiB, or a zip containing one
   such file, into a chosen relative path; the content is added to the draft
   and must still be saved to persist. The upload control is useful when
   request filters block script-like inline text.
5. Save, then reload the saved-source view. Saving never edits a live file.

The authorized detail editor contains configuration content and may contain
secrets. The list and effective-source view contain metadata only; resolved
content is never returned by effective readback. Responses disable caching.
Validation feedback does not echo parser, SQL or content exceptions.
Legacy `sqlScripts` metadata is preserved with a warning; overlay SQL is never executed.

Edits preserve the existing document enabled state and disabled file paths.
Files not present in the draft are removed; new file paths are enabled.
The file-state list beneath the editor includes disabled files; there is no
file-state toggle in this page. New documents follow the existing import version
selection policy: an older/equal version remains disabled if a newer/equal
sibling is enabled. Explicit **Enable** disables enabled sibling versions of
the same overlay key and host/scope in one transaction. **Disable** and
**Delete** do not automatically select another version. Delete removes the
document and its cascading file rows. Action forms require confirmation.

Concurrent changes reject stale editor/action tokens. Reload and reconcile
the saved document before retrying; rejected saves retain the submitted draft.

## Understand saved effective sources

Choose a concrete registered host and artifact, then **Read saved sources**.
This action uses persisted rows, not the unsaved editor draft. It is a separate
GET that discards the current draft. Compare each relative path's sources:

1. Artifact-owned configuration is the base (priority 0).
2. AllHosts overlays apply next (priority 1).
3. Exact-host overlays apply last (priority 2), regardless of an AllHosts
   overlay's newer version.

Within each priority, the greatest overlay version wins, followed by the
document update time (artifact row update time for the base), then file row ID.
JSON files default to merge; other files default to replace. Explicit modes
override these defaults. A source is **Final winner**, **Contributes through
merge**, or **Shadowed**. Replace discards preceding inputs. Merge failures
are displayed without exposing content and that path has no effective result.
Disabled documents/files, minimum-version exclusions, missing files and empty
results are shown separately. An unregistered host cannot be used for readback.

This is overlay provenance, not a disk inspection or a final deployment preview.
HostAgent still applies connection-string protection and other deployment
transformations afterwards. Nothing on this page reads or writes live files.
Readback spans several repository reads; reload if another operator imports or
changes configuration concurrently.

## Compare base and effective content

When a host and artifact are selected, the **Base vs effective content** section
shows, per relative path, whether an overlay changes the artifact-owned base
content. The base and resolved effective content are displayed as inert,
HTML-encoded text with a bounded line diff, and content is truncated for
display when it exceeds the display limit. This section is admin-only and
responses are never cached. It compares saved rows only — never live files,
disk contents or final deployment configuration — and notes when an overlay
supersedes the artifact base for a path.

## Preview before disabling or deleting

Open the overlay (`?id=<document-id>`), choose the same host and artifact as
the saved-source view, and read the **Removal impact preview**. If no host and
artifact are chosen yet, the page says so and keeps the opened overlay while
you choose them in the saved-source view. The preview lists each enabled file
owned by the overlay, the current winner, and the winner that would remain
after removal: a fallback source, no saved source, no change when the overlay
does not affect the effective content, or a changed merged result when the
same winner remains but the overlay contributed content below it (for
example a distinct key from an AllHosts merge layer). The preview covers only
the selected host and artifact and shows the token it was computed against.
The preview section's own Disable and Delete buttons re-validate every saved
row the preview depends on — the selected document, contributing and fallback
overlays, and the artifact and its files — and reject a stale preview, so
reload before changing when another administrator may have edited any of
them. Disable and Delete are only offered in this preview section. The
saved-overlays list keeps Enable as a direct operation bound to the selected
row's token and offers a **Preview removal** link instead of direct
disable/delete, so the installation impact is visible before any removal.

## Audit and deletion history

Create, update, enable, disable and delete write a metadata-only entry to the
existing `omp.AuditLog`: actor, action, overlay identity and safe before/after
metadata (identity, scope, selectors, file count, enabled state and disabled
sibling ids). No configuration content, file content, secrets or hashes are
written to the audit log. Delete records the before metadata as a tombstone
before the cascading delete removes the document and its files; the same entry
feeds the **Deleted overlays** section.

## Older schemas and local acceptance

Schema readiness is read-only. When required overlay tables/columns are absent,
mutations are disabled and upgrade guidance is displayed. Missing HostScope uses
exact-host fallback; no Portal operation attempts DDL. A missing artifact file
table produces no base rows. Use the normal operator-controlled platform
upgrade before editing.

Before controller activation, run operator-controlled local acceptance with an
isolated test installation: verify admin/non-admin access and antiforgery,
create one exact-host and one AllHosts document, edit and reload, exercise
disabled and stale edits, confirm enable selects one sibling, and delete a test
document. Compare saved source ordering with deployment resolution for merge,
replace and minimum-version exclusion. Exercise the pre-HostScope and current
schemas, transactional rollback and concurrent enable/save conflicts. These
database/runtime checks require a separate authorized local integration step;
they are not established by in-memory tests.
