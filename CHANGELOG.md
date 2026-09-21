# Changelog

All notable changes to this repository should be documented in this file.

The format is inspired by Keep a Changelog and the project follows semantic versioning at the repository level.

## [Unreleased]

### Added

- **A picker with a fixed label, a Clear in its panel, and a period that a
  page can put back.** `omp-picker` with `data-omp-picker-static` keeps the
  label the page rendered ("User") and counts the rows that are on in the
  badge; a `data-omp-picker-clear` button in the panel's footer turns every
  row off and fires the change the page listens to. `ompDatetime.applyRangePreset`
  lets a page set a period picker to a preset (the neutral one by default)
  as if picked. The user log's user and module fields say "User" and
  "Module" with the count, each panel ends with Clear, and the bar's Clear
  clears in place: the fetch swaps the results as for any other change, so
  the page no longer reloads. The page's info text is in Swedish again (its
  last sentence had no translation). The period picker's "Today" preset is
  gone from the user log (the calendar's own Today button remains), and the
  Swedish word is spelled "Idag". In the period popup, Today makes the
  current day the period and Clear puts the neutral preset back, both
  applied on the spot as a preset is; a custom period still goes through
  Apply. The user log's user and module badges show "0" when nothing is on.
  A segmented choice's fill glides from the option that was on to the one
  that is (a thumb placed by omp-picker.js; the text fades with it; not when
  the user prefers reduced motion). The user log bar's button is "Reset"
  ("Återställ"): it puts the whole bar back, while "Clear" in a panel
  empties one field.

- **A list message may stand in for a longer text.** A `.list-message`
  element with `data-list-message-text` opens that text in its popover
  instead of its own: a status pill can be the one click away from the
  whole status message, so a list keeps one short line per row.

- **A segmented choice in the picker family, and a confirmation dialog that
  can hold a field.** `omp-picker.css` now also dresses `omp-segmented`: a
  single choice with every option in view (two to four short ones), the
  chosen one filled with the link colour like a chosen picker row, no script;
  the banner page's target choice uses it instead of the portal's own
  `segmented-control`. `ompConfirm` in omp-forms takes a `title` and a
  `content` element: the page's own fields (a note, an optional text field, a
  checkbox) are borrowed into the dialog while it is open and put back where
  they were when it closes, so the page reads the values out of them; a
  checkbox marked `data-omp-confirm-requires` holds the OK button until it is
  checked, an `autofocus` element takes the focus, Enter in a text field is
  OK, and a dialog closed by other means answers no (a question asked right
  after another is not answered by the earlier dialog's queued close event).
  omp-forms.css dresses the building blocks (`__facts`, `__field`,
  `__warning`, `__check`), and a block the page hides inside the content
  stays hidden.

- **A shared choice picker (`omp-picker`) in the period picker's clothes,
  and the user log's four dropdowns as one family.** `omp-picker.css` and
  `omp-picker.js` in Web.Shared give a `<details>` of radio or checkbox rows
  the same bordered field, chevron glyph and floating panel as the period
  picker: a single choice writes its text into the field and a click on a
  row closes the panel (arrow keys browse without closing, and a panel that
  closes with the focus inside hands it back to the field), a click outside or Escape
  closes, and a host field class can be handed in the same way; pages that
  swap a picker's inputs call `window.ompPicker.sync`. The user log page's
  user, module and rows dropdowns use it, so they match the period picker in
  height, border, radius and panel; a change there now waits a beat before
  fetching, so browsing a picker by keyboard is one request, not one per
  step. A multiple choice shows a box with a tick before every row, names
  the first chosen row in the field, keeps naming it while it stays on,
  says "and others" for the rest in the page's own words ("m.fl." in
  Swedish; or "+N" in a badge without them), and lists them all in the
  field's title; the user log's module picker says "All modules"
  when none is chosen. The user log's user filter is a multiple choice too,
  so several users can be followed at once; the page reads `UserIds`. The
  period field takes the same active look as a picker with a choice on
  whenever the period is anything but "Since the start" (`data-neutral`).

- **The service and worker examples' web apps write to the user log.** Each
  records `configuration.saved`, `app_instance.saved` (allowed flag, desired
  state, configuration and artifact ids) and `job.queued` (request type,
  never the payload) in a new `ActivityLog` table in its own schema; the
  service and the worker themselves are not persons and write nothing. A
  save of a missing row answers not found instead of a green message, and
  the queued job's id comes back from the insert so the entry can name it.

- **The two example web modules write to the user log.** The Razor example
  records `configuration.saved`, `test_notification.sent` and `banner.sent`
  (level and target kinds, never the text), the Blazor example
  `configuration.saved` from its edit component using the cascading
  authentication state; each in a new `ActivityLog` table in its own
  schema, demanded by the probe so existing installations get it on the
  next import. Together they serve as the reference for wiring a Razor
  Pages module and a Blazor module into the user log.

- **Messages: the group's creator can rename the group, add members and
  hand over the admin seat.** A "Manage group" section under the member
  list holds a name field (an empty name shows the group by its members
  again; saving an unchanged name tells nobody) and a user search with
  checkboxes for adding members; someone who left earlier gets their seat
  back and starts at the current end of the thread, so old messages do not
  come back as unread, though the history stays readable as in any group
  chat. The seat cannot be handed to a disabled account, which could
  neither use it nor give it back, and the succession when the creator
  leaves passes such an account over for the same reason. A badge next to the
  cross on each other member hands them the admin seat after a confirmation
  that says plainly the creator loses the admin rights themselves. Each
  change is told to the group as a system line, everyone gets a push, and
  the Portal's user log records conversation.renamed,
  conversation.participants_added and conversation.admin_transferred. The
  Messages pages now also translate the message service's refusals
  ("Only group conversations…", "The participant is not part of…") instead
  of showing them in English.

- **Messages: delete your own message.** A bin next to the pencil on the
  sender's own messages asks for confirmation and then takes the message
  back: its text is wiped and its attachments removed, and the thread shows
  a placeholder in its place, a dashed, unfilled bubble with a bin icon and
  the italic text "This message was deleted", so nobody mistakes it for a
  message that merely says so. Deleted messages no longer count as unread
  or appear as a conversation's preview. Every participant gets a push, the
  history view deletes through a plain form post, and the Portal's user log
  records message.deleted (never the text). Also: a pasted image is ignored
  while a message is being edited, and no other message can be opened for
  editing while the composer is submitting.

- **Content and iFrame modules in the user log.** The Content module writes
  `content_page.created`, `content_page.updated`, `content_page.enabled`,
  `content_page.disabled`, `content_page.deleted` (a slug conflict is a
  `failed` entry, without the reason text) and `content_files.loaded` to a new
  `omp_content.ActivityLog`. The iFrame module writes `iframe_url.opened` to
  `omp_iframe.ActivityLog` when a configured target is shown, embedded or
  standalone, and a `denied` entry when the active role may not open it; the
  entry names the target by its id and display name and leaves the address
  out. Both probes demand the table, so existing installations get it on the
  next import.

- **Sign-in and sign-out in the user log.** The Auth app writes
  `session.signed_in` (naming the provider), `session.signed_out` and
  `user.registered` to a new `omp_auth.ActivityLog`, read by the Portal's user
  log like every other module's. The Auth module gets its own schema
  `omp_auth` for the purpose (its tables stay in the core `omp` schema): a
  module row must name a schema of its own, or the user log would list the
  core schema once per module sharing it. The probe demands the table, so an
  existing installation gets it on the next import.

- **Messages: edit your own message, leave a group, and (as the group's
  creator) remove a participant.** A pencil on the sender's own text messages
  puts the text in the composer; sending rewrites the message and marks it
  "Edited" with the time in a tooltip (attachments stay as they are, an
  empty text is refused). A group's creator is its admin: the identity column
  lists the members with an admin pill, and the admin can remove any other
  member. Everyone can leave; when the creator leaves, the member who joined
  earliest becomes the new admin. Each change is told to the group as a quiet
  system line, every participant gets a push, and a member who was removed
  while reading the thread is sent back to the list. When the last member
  leaves, the conversation is deleted with its messages and attachments,
  since nobody could ever see it again. The Portal's user log records
  message.edited (never the text), conversation.left,
  conversation.participant_removed, conversation.admin_transferred and
  conversation.deleted.

- **User log envelope version 2: a message key and arguments beside the
  summary.** `ActivityEntry` gains optional `MessageKey` and `Args`; an entry
  that carries them is stored as `v: 2` with `messageKey` and `args`, one
  without stays `v: 1` byte for byte. The reader now accepts every version
  from 1 up, newer ones included, because versions only add fields; only a
  version below 1, malformed JSON or a missing event or summary falls back to
  the raw text. The Portal pilots version 2 on its own events (roles, host
  deployments, maintenance, package import), and the user log's detail view
  shows the version, the key and the arguments. No translation reads the key
  yet; it is recorded so the line can be rendered in another language once a
  catalogue exists.

- **`OMP.Portal.UserLog.View`: read access to the Portal user log on its own.**
  The user log (`/admin/activitylog`) now opens for a Portal administrator or
  for a holder of the new permission, so an auditor can be given the audit
  trail without the rest of the admin area. Seeded by the Portal initialize
  SQL and granted to `PortalAdmins`; the Portal probe demands the row, so an
  upgraded installation gets it on the next import. The shared admin menu
  (portal navbar, dashboard navbar and module top bar), the login return-URL
  check and the favourites filter all read one table in
  `PortalAdminNavigation` that says which permission opens which admin page,
  so a user-log-only user sees an admin menu holding just that page.

- **X.509 certificate key-ring protection for ASP.NET Core Data Protection**, as
  a second encryption-at-rest mode alongside the AD-backed DPAPI-NG descriptor,
  for web farms without Active Directory. Configured with
  `OmpAuth:DataProtectionCertificateThumbprint` (looked up in
  `LocalMachine\My`). Precedence is descriptor > certificate > off > legacy
  DPAPI, and configuring both descriptor and certificate is a loud startup error
  rather than a guessed precedence. A missing certificate, a missing private
  key, a non-RSA private key, a malformed thumbprint (not 40 hex characters), or
  an expired/not-yet-valid certificate each fail startup with the thumbprint
  named -- there is never a silent fallback. Rotation is supported through
  `OmpAuth:DataProtectionRetiredCertificateThumbprints`, which feeds
  `UnprotectKeysWithAnyCertificate` so key files encrypted to an outgoing
  certificate stay readable; an expired retired certificate is accepted by
  design, but a retired entry that cannot be resolved fails startup. The
  protection-choice matrix, the post-deploy verification steps and the
  which-apps-share-a-ring analysis are in `docs/HOST_AGENT.md`.

- **Bulk AD-to-OMP role principal migration** at
  `/admin/security/ad-principal-migration` (a friendly route added in
  `OpenModulePlatform.Portal/Program.cs` for the page
  `Pages/Admin/Rbac/AdPrincipalMigration.cshtml`, which also stays reachable at
  its conventional path). It lists all `ADUser`/`User` role
  rows, previews the resolution per row (including rows that cannot be linked,
  with the reason, and a risk note when the target user has no non-AD sign-in
  link), and executes only after explicit confirmation -- in a serializable
  transaction, idempotently via `WHERE NOT EXISTS`, retried exactly once on a
  deadlock (SQL error 1205). Source AD rows are retained by design and `ADGroup`
  rows are never touched.

- **Opt-in OIDC sign-in diagnostics** (`OmpAuth:Oidc:Diagnostics`, default off;
  claim values only with `IncludeClaimValues`), plus a warn-once report for
  configured AD-mapping claim types that were absent from a validated sign-in.

- WorkerManager now publishes a `Draining` (6) observed state while a worker is
  finishing its in-flight job ahead of a configuration- or host-driven restart,
  instead of continuing to claim `Running`. The Portal worker pages and the
  example worker app label the new state, the app-instance summary ranks it
  deliberately (between Stopping and Starting), and the staleness downgrade
  covers it so a manager that dies mid-drain no longer pins `Draining` forever.
  HostAgent resource telemetry samples the live process of a draining worker too
  (observed states 1, 2, 3 and 6), since the process and its in-flight job's
  resource usage exist until the restart.

### Fixed

- **A date popup near the right edge stays on the page.** The period
  picker's popup (and a date field's calendar) hangs from the field's left
  edge; from a field at the right of a row it ran past the viewport and
  gave the page a horizontal scroll. It now shifts left as far as needed.

- **Deployed Portal, Auth and Content run with the repository's CSP and log
  to file.** The checked-in `appsettings.json` never reaches a host: the
  payload strips it and HostAgent writes `Packaging/appsettings.json` over a
  built-in template that has neither a `SecurityHeaders` nor an `NLog`
  section, so every deployed web app fell back to the baseline policy with
  `script-src 'unsafe-inline'` and wrote no log file. The three packaging
  files now carry the same policy and `NLog` section as the checked-in
  files; `PortalInlineScriptGuardTests` pins the packaged Portal policy to
  the checked-in one and `PackagedNLogConfigurationTests` parses every
  section the way NLog does at startup. Because the package baseline of
  these three files changes, the first import of the new artifacts reports
  a carry-forward conflict for an operator-edited `appsettings.json` row
  (the package file wins); re-apply such edits once from the artifact's
  configuration page. The Portal, Content and iFrame
  layouts render the request correlation id
  (`${scopeproperty:item=CorrelationId}`) that the shared middleware has
  opened a scope for since it was introduced. The HostAgent template lists
  `ResourceTelemetry:RetainDays` and `CollectWorkerProcesses` with their
  defaults; `ADMIN_CONFIGURATION.md` documents the IIS request-filtering
  copy of the upload limit in `web.config`, the `MusicPlayerWidget` and
  `ArtifactUpload` folder keys; `HOST_AGENT.md` documents that a
  service-app instance needs a host or a host template and that the
  post-deploy health check reads IIS through `appcmd` unless
  `DeploymentHealthUrls` is set.

- **Sentinel's database heartbeat can look up a configured HostKey.** The
  optional `omp.Hosts.LastSeenUtc` check queried `Environment.MachineName`
  only, so a host whose `HostAgent:HostKey` differs from its machine name got
  event 103 while HostAgent was alive. A new `HostKey` app setting names the
  key; empty keeps the machine name. The Bootstrapper no longer counts
  `OMP.HostAgent.Sentinel` as a HostAgent service when upgrade/complete decides
  whether a missing HostAgent must be installed (self-upgrade already excluded
  it), parses `net localgroup` output by shape instead of by English or Swedish
  completion text, reads an unquoted service `BINARY_PATH_NAME` up to the `.exe`
  that ends a token rather than the first `.exe` anywhere in the path, refuses
  to remove a runtime directory that is or contains Windows, Program Files,
  ProgramData or the user profile, and saves a host profile from the GUI
  without a UTF-8 BOM like the two merge writes already did. HostAgent's
  IIS/appcmd, sc.exe, service-name and directory-deletion helpers, the shared
  data-protection key path fallback, the artifact directory hash and the
  expected-deployment-fault list each have one definition instead of two to
  five copies; the import-side hash now refuses to follow reparse points like
  the packaging and provisioning hashes do.

- **The confirmation dialog's OK button is filled again.** The base rule for
  the dialog's buttons outweighed the primary button's own class, so OK sat
  next to Cancel in the same grey; the primary rule now carries the same
  weight and comes after.

- **The OIDC/ADFS sign-in path no longer depends on a single claim mapping for
  the `DOMAIN\name` principal form.** The claim resolver reads `unique_name` and
  `windowsaccountname` (short names and WS URIs) as user-principal candidates
  and, when `TranslateSidClaimsToAccountNames` is enabled (the default),
  translates SID claims to account names on the domain-joined auth server,
  mirroring the Windows path. Group claims are enriched in both directions, so a
  role row matches whether the provider sends a SID or a `DOMAIN\Group` name.
  Translation is fail-safe and cached per sign-in.

- **Ambiguous AD-user links now fail closed instead of guessing.** The RBAC role
  admin page resolved an AD user principal with `SELECT TOP (1) ... ORDER BY
  user_id`. Uniqueness on `omp.user_auth` is `(provider_id,
  provider_user_hash)`, a SHA-256 over the raw key and therefore case-sensitive,
  so two active AD links differing only in letter case can point at different
  OMP users -- and the page silently rewrote the principal to the lowest user
  id. The lookup now counts distinct active linked users and abstains when more
  than one resolves, reporting the ambiguity with the same wording as the bulk
  move. No role is assigned on a guess. The page also reports every
  `ADUser -> OmpUser` rewrite with its reason and offers a preserve-literal
  checkbox for storing the exact `DOMAIN\name` principal when that is intended.

- **Module-definition SQL is deferred when an artifact in the same import
  fails.** A failed artifact item (a version conflict, for example) previously
  still let the definition SQL run, recording a `Succeeded` execution over the
  pre-failure artifact state; the version gate would then never re-run it after
  the artifact was repaired and re-imported. With the SQL deferred, no execution
  is recorded and the next clean import runs the scripts. Portal defers only
  when `ExecuteSqlRepairs` is on and the module is not platform core.

- **A definition-SQL failure in Portal no longer causes a double artifact
  import.** The reordered SQL phase threw out of `ImportAsync` after the
  artifacts had imported, losing their results; the standalone artifact
  fall-through then imported the same artifacts a second time, rewriting
  configuration rows and reporting the fresh import as an identical skip.
  `ImportAsync` now reports the failure in the result (`DefinitionSqlError`)
  when the universal loop asks it to, and the loop marks the module-definition
  item failed while keeping the artifact results. Legacy single-module import
  paths keep the throwing contract.

- **`bump-version.ps1` now carries the module definition's own
  `definitionVersion` with a component bump.** A component bump rewrites
  `compatibleArtifacts.maxVersion` in the module definition, which changes the
  definition -- and HostAgent rejects a re-imported definition carrying the same
  `definitionVersion` with different content. The bump left that version
  untouched, so `local-ci` and the pre-push gate refused the result with a
  message that never named the second command the operator then had to find. A
  definition touched by a component bump is now added to the normal selection
  and goes through exactly the same path as an explicit `-ModuleKey`.

- WorkerManager robustness (review findings R7-F4–F7): the runtime-observation
  upsert now guards both foreign keys of `omp.WorkerInstanceRuntimeStates` (and
  the `omp.AppInstanceRuntimeStates` fallback write) instead of only the one the
  MERGE matches on, so an observation arriving after its app instance was
  deleted is dropped instead of faulting the publish; the HostAgent RPC caller
  identity WMI lookup disposes its result collection and every enumerated
  `ManagementObject`; and a broken OMP database worker catalog row (duplicate
  id, incompatible package type, unresolvable plugin path, unreadable value) is
  skipped per row instead of failing reconciliation for every worker on the
  host. The drain lifecycle (begin/cancel/timeout) is now covered by unit tests
  against the three historical drain defects (R5-F1, R6-F6/W6, R7-F1).

- Operator-edited artifact configuration files (`omp.ArtifactConfigurationFiles`)
  are no longer lost silently when a new artifact version is imported with
  packaged configuration files. Each package-registered row now stores the
  pristine packaged content in the new `PackageFileContent` baseline column, and
  HostAgent import, Portal upload, Portal universal import, and the Bootstrapper
  run a shared three-way carry-forward: when the packaged file is unchanged
  against the previous version's baseline, the operator-edited content and
  enabled state follow the new version automatically. When the packaged file
  changed over an operator edit, or the row predates the baseline column, the
  package file wins and the import result warns about the affected files instead
  of dropping the edits silently. Re-registering the same artifact version also
  preserves operator edits while the packaged file is unchanged.

- HostAgent now redeploys web apps and service apps when the artifact content
  SHA-256 changes behind an unchanged artifact id and version. The already-applied
  check compares the desired `omp.HostArtifactStates.ContentSha256` with the
  deployed `omp.HostAppDeploymentStates.ContentSha256`, so replaced artifact
  content no longer requires a version bump to reach the runtime.

> **Note:** This changelog was not maintained per-release after `0.1.0`. The repository has since advanced to the `0.3.x` release line. The authoritative current version is the `repositoryVersion` in `omp-components.json` — not the newest entry below, and not `Directory.Build.props`, whose assembly version is intentionally static and decoupled from the component versions. Treat the `0.1.0` section as the initial-baseline record, not the current state; future notable changes should be logged here per the Keep a Changelog format.

## [0.1.0] - 2026-04-13

### Added

- initial public beta release line for OpenModulePlatform
- central version metadata in `Directory.Build.props`
- repository hygiene files: `.editorconfig`, `.gitattributes`, improved `.gitignore`
- GitHub Actions CI workflow for restore and release builds
- Dependabot configuration for NuGet packages and GitHub Actions
- worker runtime scaffold projects:
  - `OpenModulePlatform.WorkerManager.WindowsService`
  - `OpenModulePlatform.WorkerProcessHost`
  - `OpenModulePlatform.Worker.Abstractions`
- public contribution guidance and release-oriented documentation

### Changed

- standardized documentation in English for public release preparation
- strengthened public repository guidance and security documentation
- improved portal top bar JavaScript to coalesce resize-driven layout work into a single frame
- tightened several shared web implementation details during release preparation

### Notes

`0.1.0` is the first public beta baseline. The repository is intentionally useful and buildable, but some architectural areas remain under active design, especially template materialization, HostAgent, and the future worker runtime.
