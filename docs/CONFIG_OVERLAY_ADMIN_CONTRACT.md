# Config overlay administration contract

This contract complements [config overlays](CONFIG_OVERLAYS.md) and
[ADR 0007](adr/0007-config-overlay-all-hosts-scope.md). It defines the boundary
for Portal CRUD and effective-source views. The implemented page is
`/admin/configoverlays`; see the [operator guide](CONFIG_OVERLAY_ADMINISTRATION.md).

## Authorization and content

Every GET, POST, export, preview and effective-source handler must use
`RequirePortalAdminAsync`, as the existing `ModulePackageImportModel` does,
before repository reads or writes. Repository methods do not authorize callers.
Use the existing Razor antiforgery behavior for mutations. List/source DTOs
contain metadata only; JSON and file content belong only in authorized detail
or explicit export responses. Do not copy content into status messages or logs.

## Existing repository and portable-object boundary

| Operation | Existing contract |
| --- | --- |
| List all documents | `OmpAdminRepository.GetConfigOverlayDocumentsAsync(ct)` includes enabled and disabled documents, scope and selectors, file count, hash and update time. |
| List host candidates | `GetConfigOverlaysApplicableToHostAsync(hostKey, ct)` returns `ConfigOverlayListItemDto` for that host plus AllHosts. It does not filter artifact selectors, enabled state or minimum versions and is not an effective-source calculation. Supply a concrete host key. |
| Read canonical document | `GetConfigOverlayJsonAsync(documentId, ct)` returns stored JSON and identity, or null. Use the summary for stored HostScope. |
| Read enabled normalized files | `GetConfigOverlayConfigurationFilesAsync(documentId, ct)` returns path, content and nullable merge mode. Disabled file rows are excluded. |
| Reconstruct portable export | `GetConfigOverlayExportJsonAsync(documentId, ct)` preserves stored JSON entries and fills missing legacy configuration files from normalized rows. |
| Parse authoring/import JSON | `ConfigOverlayPackageReader.ReadConfigOverlayAsync(path, sourceName, ct)` validates paths and selectors and normalizes scope, content and SHA-256. Use this boundary before saving; do not construct a mismatched document/hash/file-row tuple in a form handler. |
| Save/import | Both Portal and HostAgent `SaveImportedConfigOverlayAsync(input, replaceExisting, ct)` return `(DocumentId, Created, Replaced, WasIdentical)`. |
| Inline editor JSON | `ReadInlineConfigOverlay(jsonText)` uses the same normalization with no external root/archive, so filesystem references cannot resolve. The administration service also rejects rooted deployment paths before normalization. |
| Universal export | `PortableModulePackageService.ExportUniversalPackageAsync(request, ct)` accepts selected document IDs and uses reconstructed JSON. No direct legacy JSON-export route is enabled. |

The DTO declarations are in
`OpenModulePlatform.Portal/Models/ConfigOverlayAdminDtos.cs`:
`ConfigOverlayListItemDto`, `ConfigOverlayFileDto`, `ConfigOverlayDetailDto`,
`EffectiveConfigurationSourceDto` and `EffectiveConfigurationFileDto`.
The saved-source view uses these metadata contracts; the authorized editor
loads canonical JSON and normalized rows through `IConfigOverlayAdminStore`.

## Save and CRUD semantics

- Identity is `(OverlayKey, HostKey, OverlayVersion)`. Explicit AllHosts stores
  HostKey `*` and HostScope `AllHosts`; Host requires a concrete non-wildcard key.
  The reader rejects unknown scopes and an AllHosts document with a real host key.
- AllHosts save/import requires `omp.ConfigOverlayDocuments.HostScope`, even
  for identical re-imports. A missing column raises `InvalidOperationException`
  with platform schema-upgrade guidance before any sibling, document or file
  writes. Save does not attempt DDL. Host imports still work without the column.
- Changed content at the same identity requires `replaceExisting: true`.
  Identical import is a no-op and never re-enables a disabled row. A newly
  imported older/equal version remains disabled when a newer/equal sibling is
  enabled. A replacement of changed content re-enables its row.
- Document/file replacement and enabled-sibling changes are one transaction.
  Disable enabled siblings before enabling the selected version to satisfy
  `UX_omp_ConfigOverlayDocuments_Enabled_Key_Host`. AllHosts and a concrete host
  are independent sibling groups. Surface conflicts as validation errors.
- An edit must preserve all selectors, including `ModuleDefinitionVersion`,
  even where the list DTO omits them. Changing identity creates a new document;
  do not silently change the identity of a different version or scope.

`IConfigOverlayAdminStore` supplies detail (including disabled files), editor
save and explicit enable/disable/delete operations. The page authorizes each
handler before using this installation-local store. Editor saves additionally
check the document ID and hash/update-time token inside the save transaction;
unlike changed-content imports, they preserve disabled document/file state.
Enabling keeps the same transactional
sibling rule; disabling must not automatically select another version. Delete
by document ID and use the existing cascading file-row foreign key. Do not
implement these operations by re-importing JSON: identical import is a no-op.

## Effective-source calculation

The input is a concrete host key and artifact ID. Match the same connected
installation, enabled document/file rows, and module/app/package/target selectors
as `OmpHostArtifactRepository.BuildConfigurationFilesSql`. An overlay's
`artifactVersion` is a minimum, compared with `ArtifactVersionComparer`; exclude
versions below it and surface the reason separately. `ModuleDefinitionVersion`
is retained metadata, not an extra runtime filter.

For each relative path (case-insensitive), select one row per priority:

| SourceKind | SourcePriority | HostScope |
| --- | --- | --- |
| Artifact | 0 | null |
| ConfigOverlay | 1 | AllHosts |
| ConfigOverlay | 2 | Host |

Within a priority, compare OverlayVersion using `ArtifactVersionComparer`, then
UpdatedUtc, then configuration-file row ID, greatest first. Scope priority takes
precedence over version. Apply only those scope winners in ascending priority.
Default merge mode is `merge` for `.json`, `replace` otherwise; explicit modes
override it. Merge requires JSON objects and must surface errors. Replace
discards all earlier content. A lower-priority merge input contributes to the
result even though it is not the final winner.

For `EffectiveConfigurationSourceDto`, set `IsWinner` only on the final
highest-priority winner and `IsShadowed` on rows that do not contribute (same
scope losers, or earlier winners discarded by a later replace). A lower scope
winner retained through merge has both flags false. `EffectiveMergeMode` is
the computed mode; `MergeMode` remains the stored nullable value. Artifact
content is the starting layer. The parent `EffectiveConfigurationFileDto.Winner`
is the final winner and `Sources` retains all matching candidates for explanation.
Source metadata needs the true update time and file row ID for ordering; DTO
display order or document-list order cannot substitute for them.

`ConfigOverlayAdministration` returns these effective-source DTOs using the runtime's
`ConfigurationFileCandidateWins` and `ResolveLayeredConfigurationContent`
semantics, not label the host candidate list as an effective view. Keep this
overlay result distinct from final deployment configuration, which also applies
runtime connection-string protection and other deployment transformations.

## Removal-impact preview

`ConfigOverlayAdministration.PreviewRemovalAsync(snapshot, documentId, hostKey,
artifactId, ct)` is a read-only pre-disable/pre-delete preview. It resolves the
effective sources with the existing contracts above, then resolves them again
excluding the selected document, and compares the winner per owned file path.
It never enables, disables or deletes anything and never changes deletion
persistence, schema or HostAgent behavior.

- Input is a saved document ID plus the same concrete host key and artifact ID
  as the effective-source calculation. Unknown documents, foreign hosts and
  unknown artifacts throw `InvalidOperationException` before any source reads,
  and the page maps them to the same registered host/artifact validation error.
- The `OverlayRemovalPreview` carries document identity, the selected
  host/artifact, the hash/timestamp the preview was computed against, a
  `DetailUnavailable` flag and one `OverlayRemovalFilePreview` per enabled
  owned file. File previews contain winner metadata only (`CurrentWinner`,
  `WinnerAfterRemoval`); file content is never included.
- `Outcome` is `Unchanged` (another source still wins, or the document does not
  contribute to this host/artifact), `FallsBack` (a named artifact, AllHosts or
  exact-host source becomes the winner), `NoEffectiveFile` (no saved source
  remains for the path) or `Unknown` (no saved effective file; genuinely
  unknown coverage). The preview covers only the selected host and artifact;
  other hosts or artifacts using the document are not enumerated, and the
  response states the saved-rows-only boundary explicitly.
- The page renders the preview token (`OverlayEditToken` over the preview
  hash/timestamp, identical to the Disable/Delete form token). Mutations keep
  the exact optimistic token binding: `ChangeStateAsync` rejects a stale token,
  so a preview computed against older rows cannot silently mutate. The page
  requires Portal administration and antiforgery exactly like the existing
  handlers.

## Schema and validation limits

Runtime HostScope Ensure uses a guarded column addition plus the setup CHECK
`HostScope IN (N'Host', N'AllHosts')`; it also repairs a missing, disabled or
untrusted named CHECK on an existing column. Legacy rows default to Host.
Read fallback remains exact-host-only when the column cannot be added. Runtime
DDL is not initiated by Portal save/import.

The native export tests call the production ZIP entry/manifest writers and both
readers. Schema-policy tests exercise pre-column and current schema states,
save-path wiring, fallback query generation and SQL parsing without opening a
database. In-memory tests exercise the actual runtime winner/merge functions.
Database permission, concurrent DDL and transactional SQL execution still need
operator-controlled integration verification before activation.
