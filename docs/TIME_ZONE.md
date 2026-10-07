# Presentation time zone and local calendar

Campaign: `omp-tidszon-presentation-och-lokal-kalender`, platform track, phases 1–2.

## Inventory before implementation

Line references below describe the baseline before the implementation. All paths
in the first table are relative to `OpenModulePlatform.Portal/`.

| Concern | Baseline locations | Classification |
|---|---|---|
| User and system logs | `Pages/Admin/ActivityLog.cshtml:197`, `SystemLog.cshtml:174,228`, their page models at lines 102 and 111 | Presentation |
| Artifacts | `Pages/Admin/Artifacts.cshtml:73`, `ArtifactEdit.cshtml:118` | Presentation |
| Users | `Pages/Admin/Users/Index.cshtml.cs:44`, `Users/Edit.cshtml.cs:538` | Presentation |
| HostAgent deployments, status and assignments | `Pages/Admin/HostDeployments.cshtml:82,178,182,186,193,309,373,376,377,432,436,525,605,687,688,709,819,1062,1063`, `HostDeploymentAssignments.cshtml:36` | Presentation |
| Host resources | `Pages/Admin/HostResources.cshtml:136,258`, `HostResourceDetail.cshtml:113,118,119` | Presentation of UTC sample buckets |
| Runtime maintenance | `Pages/Admin/Maintenance.cshtml:176,279,301,486`, `Hosts.cshtml:51`, `AppInstances.cshtml:70` | Presentation |
| Messages, notifications, banners | `Pages/Messages/Index.cshtml:65`, `_ThreadMessages.cshtml:54,80,83,118`, `Pages/Notifications.cshtml:58`, `Pages/Admin/Banners.cshtml:209,210` | Server-local presentation |
| Dashboard | `Pages/Shared/_DashboardNotificationFeedWidget.cshtml:49`, `_DashboardMessageConversationWidget.cshtml:29`, `Pages/Admin/DashboardWidgets.cshtml:148`, `wwwroot/js/portal-dashboard.js:1843` | Server/browser-local presentation |
| Date widget and rolling periods | `Pages/Shared/_DashboardDateWidget.cshtml:7`, `Pages/Admin/PeriodPresets.cs:25`, `ActivityLog.cshtml:106`, `SystemLog.cshtml:99` | Calendar logic |
| Log filter boundaries | `Pages/Admin/ActivityLog.cshtml.cs:90,91`, `SystemLog.cshtml.cs:73–101` | Calendar input converted to UTC query boundaries |

Shared topbar presentation: `OpenModulePlatform.Web.Shared/Views/Shared/Components/PortalTopBar/Default.cshtml:26`
and `OpenModulePlatform.Web.Shared/wwwroot/js/portal-topbar.js:991`.
`omp-datetime.js:246,370,534,588,832,1039,1050,1124,1166,1223` provides calendar
controls and originally selected Today/Now and relative limits using UTC.
Date-only field values stay calendar values, without instant conversion.

Example modules shipped in this repository:

| Example | Presentation locations relative to `examples/` |
|---|---|
| WebAppModule | `WebAppModule/WebApp/Pages/Configurations/Index.cshtml:28` |
| WebAppBlazorModule | `WebAppBlazorModule/WebApp/Components/Pages/Configurations/Index.razor:48` |
| ServiceAppModule | `ServiceAppModule/WebApp/Pages/Jobs/Index.cshtml:44`, `AppInstances/Index.cshtml:45` |
| WorkerAppModule | `WorkerAppModule/WebApp/Pages/Jobs/Index.cshtml:44`, `AppInstances/Index.cshtml:68` |

The built-in content module also displays UTC in
`OpenModulePlatform.Web.ContentWebAppModule/Pages/Admin/Edit.cshtml:231,233`.
The iframe module has no timestamp display.

Storage and technical elapsed time stay UTC: SQL reads/writes, event envelopes,
API DTOs, sort keys, deployment leases, heartbeat and refresh intervals in the
example service/worker engines, and retention in
`OpenModulePlatform.HostAgent.Runtime/Services/SystemLogRetentionService.cs:106`
and `ArtifactZipImportService.cs:211`. Bootstrapper package versions, backup names,
portable export timestamps and ZIP DOS timestamps are technical identities, not
user calendar rules; they are generated from UTC because a local timestamp
repeats during the autumn DST hour. Certificate validity compares UTC instants
(native certificate times are machine-local wall times).
No local-calendar daily scheduler was found in the platform or examples.

Additional presentation sites found in the completeness pass (baseline lines):
`OpenModulePlatform.Web.Shared/Components/Layout/PortalTopBar.razor:743`,
Portal `Pages/Admin/ModuleDefinitions.cshtml:114,362`,
`ModuleDefinitionEdit.cshtml:39,113`, `WorkerRuntime.cshtml:63,66,70`, `Workers.cshtml:181,184,188`,
`_HostResourceChart.cshtml:34`, and Content `Pages/Admin/Index.cshtml:90`.
The banner editor's `datetime-local` fields previously accepted UTC wall time
(`Pages/Admin/Banners.cshtml.cs:269–291`); these are calendar inputs, while the
banner service request fields remain UTC instants.

## Configuration and deployment

Hosts without `icu.dll` (Windows before 10 1903 / Server 2019) run .NET in NLS
globalization mode, where the platform rejects IANA time zone ids.
`OmpTimeZoneLookup` (OpenModulePlatform.Web.Shared) keeps IANA configuration
working there: it tries the platform lookup, then the platform IANA-to-Windows
conversion, then a small built-in IANA-to-Windows table covering the zones OMP
and its consumers use. The table matches IANA ids case-insensitively, like the
ICU path it replaces. The configured IANA id remains the reported id. An
unknown id still stops startup with a clear `TimeZoneNotFoundException`; on a
host without ICU the error for an IANA-shaped id says so explicitly and lists
the ids the built-in table covers.
Production code must never call `TimeZoneInfo.FindSystemTimeZoneById`,
`TimeZoneInfo.TryFindSystemTimeZoneById` or
`TimeZoneInfo.TryConvertIanaIdToWindowsId` directly outside a
`*TimeZoneLookup.cs` file (the exact exemption rule is documented under
validator Check 21 in [VALIDATOR_CHECKS.md](VALIDATOR_CHECKS.md), which
enforces this).

Set `OmpTime:TimeZoneId` in application configuration, for example:

```json
{
  "OmpTime": {
    "TimeZoneId": "Europe/Stockholm"
  }
}
```

All public source and packaging defaults use `UTC`. `AddOmpWebDefaults` registers
`OmpTime` and validates the identifier eagerly during startup. Auth registers it
explicitly because it builds its own pipeline. Nonstandard hosts can call
`services.AddOmpTime(configuration)`. An invalid, empty or Windows-only identifier
stops startup with the configuration key in the error. Zone changes require an
application restart; there is no silent fallback or per-browser override.

Use the existing artifact configuration / host overlay chain described in
[CONFIG_OVERLAYS.md](CONFIG_OVERLAYS.md). Add the section to a host-targeted
`appsettings.json` overlay with JSON merge semantics, consistently for Portal and
each module web application. Host configuration `values` is opaque and does not
automatically become application settings. Private profile generators must emit
the overlay explicitly. Do not rely on editing the deployed file: HostAgent
rewrites it. The packaged Portal/Auth/Content defaults differ from source
appsettings; update the artifact-owned configuration and preserve the host
overlay when refreshing/importing packages. Previous artifact configuration is
copied only to a new artifact with zero configuration files, from its highest
earlier registered version, not necessarily from the active deployment.

## Presentation zone versus source-data calendar

`OmpTime:TimeZoneId` is a presentation setting with a `UTC` default. It decides
how stored UTC instants are shown and how an operator's calendar input in an
OMP page (log filters, banner times, "today" presets) is converted to UTC, so
that a filter always matches the times the same page displays. It is not a
business calendar.

Do not use `OmpTime`, or its configuration key, for rules about the calendar of
external source data: day boundaries, "today", "future date" checks, date
filters in background processing, night/weekend windows, or dates embedded in
file or folder names. Reasons:

- Worker processes never receive it. WorkerManager starts a worker with the
  OMP connection string, `WorkerProcess__ConfigurationJson` and a fixed set of
  `--WorkerProcess:*` command-line arguments (instance identity, plugin path,
  shutdown event), never with `OmpTime`. Universal packaging refuses an
  artifact package that carries a runtime configuration file, and HostAgent
  generates `appsettings.json` only for web applications, service applications
  and WorkerManager, so no configuration file reaches a worker plugin either.
  A worker that reads `OmpTime:TimeZoneId` silently gets `UTC`.
- A source system that writes local wall-clock time without an offset has a
  fixed calendar of its own. Changing how OMP presents time must never move a
  document across a day boundary.

With a `UTC` calendar, local 00:00–02:00 during summer time (00:00–01:00 in
winter) belongs to the previous date, so "today" is one day behind and a record
stamped just after local midnight looks like it is in the future.

A module that needs a source-data calendar declares its own setting in its own
configuration contract (for a worker: the worker configuration JSON that
WorkerManager passes through), documents it in the module's packaged
configuration template and operator documentation, defaults it to the source
system's zone, and validates it at startup so an unknown zone is a clear error
rather than a fallback. Regression tests for such a rule should build the
production composition (for example the worker factory from a configuration
without any `OmpTime` section) and cover local 00:30 and 01:59 in summer time,
00:30 in winter time, and both DST transition nights.

The OMP platform and the examples in this repository contain no source-data
calendar rule; every `OmpTime` calendar use is an operator-facing Portal filter
or input over OMP's own UTC data.

## Shared helper contract

- Inject `OmpTime`. `Format(utc)` and `ToDisplayTime(utc)` accept UTC instants.
  SQL `DateTimeKind.Unspecified` is interpreted as UTC; `Local` is rejected so
  the server's machine zone cannot silently change a stored timestamp.
  In hosted Production/Staging, `Format` instead shows `[Invalid time: Local]`
  and `UtcIso` returns an empty sort key, logging a warning for each rejected
  value. Development, test environments and direct construction keep fail-fast
  behavior. `ToDisplayTime` always rejects `Local`, in every environment.
- `Today` comes from the configured zone and an injectable `TimeProvider`.
- `StartOfDayUtc(date)` creates query boundaries. For an inclusive calendar
  range, use the start of the following calendar day as an exclusive upper
  bound. Do not add 24 hours to a UTC instant: DST days can contain 23 or 25 hours.
- `ToUtc(wallTime)` accepts unspecified calendar input. Nonexistent clock
  times are rejected; ambiguous filter lower bounds use the first occurrence,
  upper bounds use the last. Banner input uses the first occurrence; an
  edited banner whose start or expiry field still shows the stored value keeps
  the stored UTC instant, so a repeated autumn minute is not moved.
  Missing banner times produce field validation errors; both start and expiry
  choose the earliest occurrence of a repeated minute.
- Central European zones use CET/CEST according to the instant's offset.
  Other server-rendered zones use explicit UTC offsets; no abbreviation is
  guessed. Browser text uses Intl's short zone label with an explicit IANA zone.
- `UtcIso` supplies UTC sort keys. Stored values, event/API DTOs, retention,
  sampling buckets, leases and elapsed-time scheduling remain UTC.
- Topbar JavaScript exposes `window.OmpTime.formatUtc`. It reads the layout's
  `omp-time-zone` meta element or the shared topbar's `data-omp-time-zone` value.
  ISO API values without an offset are treated as UTC. No browser-zone fallback.
  Include the meta element in layouts even when the topbar is disabled. Unless
  the page sets `omp-calendar-time-zone` (below), the date picker reads the same
  zone for Today/Now and relative calendar limits.
  Missing or empty meta content causes one console warning per page, using
  `data-omp-time-zone` if available, otherwise UTC. Invalid configured zone
  identifiers still fail explicitly.
- A page whose business calendar differs from the presentation zone, for
  example a module whose business days follow a local zone while `OmpTime` is
  UTC, sets `<meta name="omp-calendar-time-zone" content="Europe/Stockholm">`.
  It controls only the date picker's calendar: which day is Today, the Now
  time, the month the calendar opens on, and relative presets and limits.
  It does not control `window.OmpTime.formatUtc`, the topbar's message times,
  server-rendered `OmpTime` text, or how the server converts submitted calendar
  values; the module's own server code must interpret those in the same
  business zone. The picker resolves its zone in this order:
  1. non-empty `omp-calendar-time-zone` meta content;
  2. non-empty `omp-time-zone` meta content;
  3. the first `data-omp-time-zone` attribute in the document, with the
     one-time console warning;
  4. UTC, with the same warning.

  Empty `omp-calendar-time-zone` content counts as absent and produces no
  warning of its own; an invalid identifier fails explicitly like the other
  sources. Without the tag, every page behaves exactly as before.
  Do not overwrite `omp-time-zone` to change the picker's calendar: that also
  moves `formatUtc` output, so client-refreshed timestamps would disagree with
  server-rendered ones on the same page.

The system-log and activity-log pickers now describe local calendar dates. Old
bare-date bookmarks therefore follow the configured zone after upgrade. Explicit
system-log instants are converted for the picker. Raw log detail timestamps are
formatted for display; raw log payloads and exported portable objects stay intact.
System-log `From` and `To` query parameters bind as strings, preserving the wire
representation instead of relying on a model-bound `DateTime.Kind`. Accepted
formats are ISO dates (`yyyy-MM-dd`) and ISO date-times with `T`, hours and
minutes, optional seconds and up to seven fractional second digits. Date-times
with `Z` or an explicit `+/-HH:mm` offset are parsed as `DateTimeOffset` instants.
Their exact UTC values supply the query boundaries, including the chosen
occurrence of a repeated clock hour; only the picker display is converted to
`OmpTime:TimeZoneId`. Encode a URL's plus sign as `%2B`.

Zone-free date-times are calendar input in the configured zone. Missing minutes
are invalid; repeated minutes use the first occurrence for `From` and the last
for `To`. A date-only range uses the start of the first day and the start of the
day after the last, so DST days may span 23 or 25 hours. Timed `To` values keep
the existing inclusive-minute convention: the exclusive query bound is exactly
one minute after the supplied time, including for explicit instants. Known
period presets override valid supplied boundaries with configured calendar days.
Empty bounds are unrestricted. Invalid syntax, nonexistent calendar times and
out-of-range boundaries produce a localized date/time validation message. The
process list is still loaded, but no log search executes when validation fails.
The shared helper's rejection of machine-local `DateTime` values is unchanged.

## Consumer migration (separate phase)

After integrating this Web.Shared revision, replace presentation-only UTC or
machine-local formatting with `OmpTime`. Do not move business or source-data
calendar rules (`UtcNow.Date`, "today", day filters in background processing)
to `OmpTime`: give them a module-owned calendar setting as described in
"Presentation zone versus source-data calendar". Preserve storage/API contracts. Set the same host overlay on each app and restart it.

Bump every artifact that ships the updated shared assembly using the owning
repository's `scripts/omp/bump-version.ps1 -ComponentKey <affected-keys>`, then
run its `scripts/omp/validate-component-versions.ps1 -UpdateSharedDependencies`
against this integrated OMP revision. Commit the resulting dependency lock with
the component bumps (Check 14). Run that repository's local CI, rebuild/import
those artifacts and the corresponding module definitions together. Do not rebuild
the shared binary into an existing artifact version. Repository versioning can
also change other shared binary identities through the existing build targets;
follow the canonical bump script's complete cascade output.

## Regression evidence

Before implementation, two Stockholm presentation assertions and invalid-zone
startup validation failed (3 failures, test command exit 1). After implementation,
the focused presentation/calendar suite passed, including:

- `2026-09-24T22:30:00Z` → `2026-09-25 00:30:00 CEST`.
- `2026-01-14T23:30:00Z` → `2026-01-15 00:30:00 CET`.
- Spring/fall DST boundaries, missing and repeated minutes, UTC defaults and SQL
  unspecified kinds, plus rolling period presets.

Run the focused tests with:

```powershell
dotnet test OpenModulePlatform.Portal.Tests --filter 'FullyQualifiedName~PresentationTimeTests|FullyQualifiedName~PeriodPresetsTests'
dotnet test OpenModulePlatform.Portal.Tests --filter FullyQualifiedName~SystemLogCalendarInputTests
node --test tests/time-presentation.test.cjs
```

The focused .NET suite contains 16 cases, including a skipped-midnight boundary.
The system-log query-binding suite adds 19 cases using MVC binder providers and
real query-string decoding, with a recording log reader. Its initial 14-case
regression run failed six cases (exit 1); after the fix all 14 passed (exit 0).
The extended suite covers explicit offsets through repeated clock hours,
fractional seconds, 23/25-hour calendar days, missing minutes, presets, empty
bounds, invalid formats, localized errors and process-list availability without
a log search. This Windows validation does not change the machine time zone;
it does not claim a two-machine-zone execution.
The three Node tests use a Los Angeles browser zone with a Stockholm platform
zone, covering both timestamp rendering and the calendar picker. Three more
cover `omp-calendar-time-zone`: without it the picker and `formatUtc` share
`omp-time-zone`; with it the picker follows the business zone while
`formatUtc` on the same page stays in the presentation zone. An actual
Portal process launched with `--OmpTime:TimeZoneId=Invalid/Zone` also exits with
code 1 before accepting requests and identifies `OmpTime:TimeZoneId` in its error.
