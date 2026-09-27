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
`omp-datetime.js` provides calendar controls; date-only field values must stay
calendar values, without instant conversion.

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
