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
user calendar rules. Certificate validity compares native certificate times.
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
each module web application. Use the same zone for non-web components that apply
business calendar rules. Host configuration `values` is opaque and does not
automatically become application settings. Private profile generators must emit
the overlay explicitly. Do not rely on editing the deployed file: HostAgent
rewrites it. The packaged Portal/Auth/Content defaults differ from source
appsettings; update the artifact-owned configuration and preserve the host
overlay when refreshing/importing packages. Previous artifact configuration is
copied only to a new artifact with zero configuration files, from its highest
earlier registered version, not necessarily from the active deployment.

## Shared helper contract

- Inject `OmpTime`. `Format(utc)` and `ToDisplayTime(utc)` accept UTC instants.
  SQL `DateTimeKind.Unspecified` is interpreted as UTC; `Local` is rejected so
  the server's machine zone cannot silently change a stored timestamp.
- `Today` comes from the configured zone and an injectable `TimeProvider`.
- `StartOfDayUtc(date)` creates query boundaries. For an inclusive calendar
  range, use the start of the following calendar day as an exclusive upper
  bound. Do not add 24 hours to a UTC instant: DST days can contain 23 or 25 hours.
- `ToUtc(wallTime)` accepts unspecified calendar input. Nonexistent clock
  times are rejected; ambiguous filter lower bounds use the first occurrence,
  upper bounds use the last. Banner input uses the first occurrence.
- Central European zones use CET/CEST according to the instant's offset.
  Other server-rendered zones use explicit UTC offsets; no abbreviation is
  guessed. Browser text uses Intl's short zone label with an explicit IANA zone.
- `UtcIso` supplies UTC sort keys. Stored values, event/API DTOs, retention,
  sampling buckets, leases and elapsed-time scheduling remain UTC.
- Topbar JavaScript exposes `window.OmpTime.formatUtc`. It reads the layout's
  `omp-time-zone` meta element or the shared topbar's `data-omp-time-zone` value.
  ISO API values without an offset are treated as UTC. No browser-zone fallback.
  Include the meta element in layouts even when the topbar is disabled. The
  date picker reads the same zone for Today/Now and relative calendar limits.

The system-log and activity-log pickers now describe local calendar dates. Old
bare-date bookmarks therefore follow the configured zone after upgrade. Explicit
system-log instants are converted for the picker. Raw log detail timestamps are
formatted for display; raw log payloads and exported portable objects stay intact.

## Consumer migration (separate phase)

After integrating this Web.Shared revision, replace presentation-only UTC or
machine-local formatting and business `UtcNow.Date` rules with `OmpTime`. Preserve
storage/API contracts. Set the same host overlay on each app and restart it.

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
node --test tests/time-presentation.test.cjs
```

The focused .NET suite contains 16 cases, including a skipped-midnight boundary.
The three Node tests use a Los Angeles browser zone with a Stockholm platform
zone, covering both timestamp rendering and the calendar picker. An actual
Portal process launched with `--OmpTime:TimeZoneId=Invalid/Zone` also exits with
code 1 before accepting requests and identifies `OmpTime:TimeZoneId` in its error.
