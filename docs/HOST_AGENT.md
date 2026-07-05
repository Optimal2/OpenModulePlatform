# HostAgent

HostAgent is host-local OMP/ODV infrastructure. It is intentionally generic and
is not tied to any specific consumer module.

Artifact versions and stable identifiers follow
`docs/VERSIONING_AND_IDENTITIES.md`. HostAgent deploys the artifact selected by
OMP metadata; it does not infer the latest version from file or folder names.

## Implemented

- Windows Service project: `OpenModulePlatform.HostAgent.WindowsService`
- Shared runtime project: `OpenModulePlatform.HostAgent.Runtime`
- Host heartbeat via `omp.Hosts.LastSeenUtc`
- Desired artifact discovery from:
  - app instances assigned to the host
  - worker instances assigned to the host
  - explicit `omp.HostArtifactRequirements`
- Immutable local artifact cache under `HostAgent:LocalArtifactCacheRoot`
- Central artifact root resolution via `HostAgent:CentralArtifactRoot`
- File and directory SHA-256 verification
- Provisioning state in `omp.HostArtifactStates`
- IIS web app deployment for provisioned `web-app` artifacts when
  `HostAgent:DeployWebApps` is enabled
- Windows service deployment for provisioned `service-app` artifacts when
  `HostAgent:DeployServiceApps` is enabled
- Provisioning for `worker-host` artifacts such as WorkerProcessHost; these are
  cached for WorkerManager rather than deployed as standalone services
- HostAgent self-upgrade preparation and takeover for desired
  `host-agent` artifacts registered in `omp.HostAgentDesiredStates`
- Web app deployment state in `omp.HostAppDeploymentStates`
- Per-host Portal health state in `omp.WebAppHealthStates`
- Database-backed HostAgent job queue in `omp.HostAgentJobs`, currently used
  for central artifact store, host-local artifact cache cleanup, and
  maintenance scan/cleanup tasks requested from Portal maintenance and
  Portal web-app health operations
- Local named-pipe RPC for synchronous artifact provisioning:
  - operation: `ensureArtifact`
  - operation: `quiesce`
  - request fields: `artifactId`, optional `desiredLocalPath`
  - response fields: `success`, `state`, `localPath`, `contentSha256`, `errorMessage`

## SQL objects

- `omp.HostArtifactRequirements`
- `omp.HostArtifactStates`
- `omp.HostAppDeploymentStates`
- `omp.HostAgentDesiredStates`
- `omp.HostAgentRuntimeStates`
- `omp.HostAgentLeases`
- `omp.HostAgentJobs`
- `omp.MaintenanceFindings`
- `omp.WebAppHealthStates`
- `omp.WorkerInstances`
- `omp.WorkerInstanceRuntimeStates`

`omp.WorkerInstances` introduces process-level desired state below an `omp.AppInstances` row. This supports modules where one app instance needs multiple isolated worker processes, for example one process per channel.

## HostAgent job queue

Portal and other trusted platform components can enqueue host-specific or
global work in `omp.HostAgentJobs`. Host-specific jobs have `HostId` set and are
claimed only by that host. Global jobs have `HostId = NULL` and can be claimed
by the first enabled HostAgent that sees the job. HostAgent records a lease
while it runs a job and marks the final state as succeeded, warning, or failed.
Expired running jobs may be claimed again until `MaxAttempts` is reached; after
that they are marked failed.

The cleanup job types are:

- `ArtifactRetentionCleanup` - global maintenance job created by Portal. The
  first HostAgent that claims it recomputes retention candidates, deletes only
  unreferenced old artifact rows in the database, deletes central
  `ArtifactStore` payload folders, and queues follow-up store/cache cleanup jobs
  in the same database transaction as a crash-safe fallback.
  This keeps the database and filesystem cleanup under the HostAgent security
  context, which normally has the broader database and filesystem permissions
  required for maintenance.
- `ArtifactStoreCleanup` - lower-level global cleanup job for deleting specific
  central artifact store paths below `HostAgent:CentralArtifactRoot`.
  HostAgent refuses to delete the artifact store root, `_available`, `.staging`,
  rooted paths, or paths that escape the configured root. It also skips a path if
  any remaining `omp.Artifacts` row still references the same relative path.
- `ArtifactCacheCleanup` - host-specific job created by artifact retention
  cleanup, one payload per host cache that may contain now-orphaned local
  artifact folders.
  HostAgent only deletes paths inside `HostAgent:LocalArtifactCacheRoot`, refuses
  to delete the cache root or `.staging`, and skips paths that are still
  referenced by current host state.
- `MaintenanceScan` - global or host-specific scan requested by Portal
  maintenance. The global scan records stale HostAgent runtime-state rows that
  are inactive, unleased, and no longer match desired state. The host-specific
  scan records stopped old HostAgent Windows services and HostAgent install or
  staging directories below the configured HostAgent install root. Scan jobs only
  create or reopen rows in `omp.MaintenanceFindings`; they do not delete
  anything.
- `MaintenanceCleanup` - global or host-specific cleanup request for selected
  `omp.MaintenanceFindings` rows. HostAgent revalidates every target immediately
  before cleanup. It refuses to delete the active HostAgent service, running
  services, directories outside the configured install root, the install root
  itself, the active process directory, credential-store directories, and
  directories still referenced by HostAgent services.
- `WebAppHealthProbe` - host-specific Portal health probe requested from the
  operations page. It calls the configured Portal readiness endpoint and writes
  `omp.WebAppHealthStates`. The request can optionally ask HostAgent to recycle
  the Portal app pool when the probe is unhealthy.
- `RecycleWebAppAppPool` - host-specific manual recycle of a managed web-app
  app pool. The first implementation targets the Portal health row and records
  the action in `omp.WebAppHealthStates`.
- `CollectWebAppLogs` - host-specific diagnostic job that reads a bounded tail
  of the latest Portal log file and stores the result on the job row for review
  from Portal.

Artifact retention cleanup protects artifact versions that are still referenced
by desired state, current app deployment state, host requirements, templates, or
active HostAgent runtime state. Portal only previews candidates and queues the
global job; the HostAgent recomputes the candidate set at execution time before
it changes the database or filesystem.

Maintenance findings use these statuses:

- `Open` - visible in Portal and eligible for cleanup or ignore.
- `Ignored` - hidden until a future scan reports the same target again.
- `CleanupQueued` - selected for a queued cleanup job.
- `Cleaned` - cleanup completed or the target was already missing.
- `Failed` - cleanup attempted but failed; the row is visible again.
- `Skipped` - HostAgent intentionally skipped the target because a safety check
  failed.

The job loop is enabled by default:

```json
{
  "HostAgent": {
    "ProcessHostAgentJobs": true,
    "MaxHostAgentJobsPerCycle": 5
  }
}
```

## Worker Manager integration

Worker Manager can be configured to call HostAgent before starting a worker process if the plugin artifact is missing locally.

```json
{
  "WorkerManager": {
    "HostAgentRpc": {
      "Enabled": true,
      "PipeName": "",
      "TimeoutSeconds": 60
    }
  }
}
```

An empty `PipeName` resolves to:

```text
OpenModulePlatform.HostAgent.{HostKey}
```

HostAgent creates the pipe with an explicit ACL. By default it allows local
Administrators, `LocalSystem`, `LocalService`, `NetworkService`, and the running
HostAgent service identity. Add custom service accounts with
`RpcAllowedClientAccounts`, or allow Windows service SIDs by service name with
`RpcAllowedClientServiceNames`:

```json
{
  "HostAgent": {
    "EnableRpc": true,
    "RpcAllowedClientAccounts": [
      "DOMAIN\\OmpWorkerManager"
    ],
    "RpcAllowedClientServiceNames": [
      "OMP.WorkerManager"
    ]
  }
}
```

HostAgent also logs the connected caller identity after each RPC pipe
connection for audit. The pipe ACL remains the authoritative enforcement
boundary.

The manager still owns process lifecycle. HostAgent only provisions artifacts and reports host/artifact state.

## IIS web app deployment

HostAgent can deploy IIS-hosted web apps after their artifacts have been
provisioned to the local artifact cache. This is enabled with:

```json
{
  "HostAgent": {
    "DeployWebApps": true,
    "IisSiteName": "OpenModulePlatform",
    "EnsureIisSite": true,
    "IisBindingProtocol": "http",
    "IisBindingPort": 8088,
    "WebAppsRoot": "D:\\OMP\\WebApps",
    "PortalPhysicalPath": "D:\\OMP\\Sites\\Portal",
    "UseAppOfflineForWebAppDeployment": true
  }
}
```

The deployment handler consumes enabled `omp.AppInstances` on the local host
whose artifact has `PackageType = 'web-app'` and a successful
`omp.HostArtifactStates` row. It resolves the target path from
`AppInstances.InstallPath` when set, otherwise from the IIS route path under
`HostAgent:WebAppsRoot`. The site-root portal app uses
`HostAgent:PortalPhysicalPath`.

When `HostAgent:EnsureIisSite` is enabled, HostAgent also creates or updates the
configured IIS site, app pools, and IIS applications before it mirrors files.
This is the normal HostAgent-first bootstrap path for a blank machine. Existing
installations can leave it disabled when IIS is managed outside HostAgent.
App pool names are generated from `HostAgent:IisAppPoolNamePrefix` and the OMP
app instance key. When app pools must run as a specific Windows identity, set
`HostAgent:IisAppPoolUserName` plus
`HostAgent:IisAppPoolPasswordCredentialKey`. The key is resolved from the local
HostAgent credential store. The legacy `HostAgent:IisAppPoolPassword` setting is
accepted only for old appsettings files and should not be written by new
installations.
HostAgent also clears the IIS Anonymous Authentication username and password for
managed sites and applications. That makes anonymous requests execute as the app
pool identity instead of the machine-level IUSR account, which keeps static
apps and ASP.NET apps on the same filesystem permission model.

## Portal health monitoring

Portal exposes `/health/live` and `/health/ready`. HostAgent can probe the
Portal readiness endpoint on each web host and write the latest result to
`omp.WebAppHealthStates`. This is intentionally application-specific health, not
a whole-node load-balancer decision. A broken Portal instance should answer with
a non-success status, while unrelated module applications on the same IIS node
can still be routed independently.

Default HostAgent settings:

```json
{
  "HostAgent": {
    "PortalHealthCheck": {
      "Enabled": true,
      "HealthKey": "portal",
      "DisplayName": "OMP Portal",
      "Path": "/health/ready",
      "TimeoutSeconds": 10,
      "FailureThreshold": 3,
      "AutoRecycleAppPool": false,
      "AutoRecycleCooldownMinutes": 15
    }
  }
}
```

When `HostName` is empty, HostAgent probes `localhost` on the configured IIS
binding port. Use `HostHeader` when the web server needs the public host header
for routing, and set `AllowInvalidTlsCertificate` only for environments where a
load balancer terminates the public certificate and the local server certificate
does not match the probed host name.

Automatic app-pool recycle is disabled by default. Operators can use the Portal
operations page to trigger a probe, recycle the Portal app pool, or collect a
small log tail from the affected host. Enable `AutoRecycleAppPool` only when the
environment has agreed that repeated readiness failures should be remediated by
HostAgent without an operator click.

An app instance with `HostId = NULL` is treated as host-neutral. HostAgent
deploys that same logical app instance on every enabled host that runs the
agent, while `omp.HostAppDeploymentStates` still tracks one deployment state per
host. First-party load-balanced IIS apps use this model so the portal menu
contains one logical app entry even when two or more web servers serve the same
public URL.

Host-neutral placement is only for apps that are intentionally identical on each
web host. Runtime apps and non-load-balanced web apps should use one concrete
app instance per host so each runtime has its own `AppInstanceId`. Active
desired rows cannot mix host-neutral and host-specific placement for the same
module/app definition.

Before copying files, HostAgent writes an `app_offline.htm` marker by default,
waits briefly for ASP.NET Core to release loaded files, mirrors the provisioned
artifact into the runtime folder, removes the marker, and records the result in
`omp.HostAppDeploymentStates`. This default path does not require HostAgent to
read IIS configuration with `appcmd.exe`; the HostAgent service identity only
needs file-system access to the artifact cache and runtime web folders.

The older app-pool control path is still available by setting
`HostAgent:UseAppOfflineForWebAppDeployment` to `false` and enabling
`StopIisAppPoolForWebAppDeployment` and/or
`StartIisAppPoolAfterWebAppDeployment`. That mode requires the HostAgent service
identity to have permission to read and control IIS configuration through
`appcmd.exe`.

Runtime-local files are preserved through
`HostAgent:WebAppDeploymentExcludedEntries`. The default exclusions are
`appsettings.json`, `appsettings.*.json`, `logs`, and `App_Data`, so deployment
can update application binaries without overwriting local configuration or
runtime data.

Applications can temporarily block HostAgent from replacing their files or
restarting their runtime by writing a deployment lock file at
`App_Data/omp-deployment.lock.json` below the deployed app root. The file uses
the `OpenModulePlatform.DeploymentLock.v1` JSON schema from
`OpenModulePlatform.Artifacts.DeploymentLockFile` and contains an expiry time.
HostAgent checks the file immediately before web-app and service-app deployment
steps that would change runtime files or restart the runtime. An active or
unreadable lock makes HostAgent skip that deployment for the current cycle and
record a warning in `omp.HostAppDeploymentStates`; an expired lock is ignored.

Portal uses this lock while importing universal module packages so a package
that also selects a newer Portal artifact cannot cause HostAgent to recycle or
replace Portal before the import has finished. The lock is renewed while the
import request is running and removed when the import completes. If Portal is
terminated unexpectedly, the lock expires automatically instead of blocking
deployment forever.

Legacy artifact zips should not contain runtime configuration files. The
exclusions above are a safety net for existing runtime folders and older
packages, while Portal artifact upload rejects known runtime config files such
as `appsettings*.json` and `odv.site.config.js`. Environment-owned config is
either written by the bootstrap/deployment layer or managed through
`omp.ArtifactConfigurationFiles`.

Enabled rows in `omp.ArtifactConfigurationFiles` are written after the artifact
has been mirrored. Each row belongs to one artifact and contains a path relative
to the deployed app root plus the exact text HostAgent should write. HostAgent
uses this for deployment-owned runtime files, for example a site-local
`odv.site.config.js`, without requiring a new artifact zip for every
configuration change.

HostAgent's built-in web-app `appsettings.json` template is intentionally
neutral. It provides only the shared Portal/WebApp, OMP database, OMP auth, and
logging baseline needed by normal OMP-hosted ASP.NET Core web apps. Module
sections such as Content Web App settings, OpenDocViewer settings, and
customer-specific values must be supplied by the artifact package's
configuration files or by matching config overlays. The public Content Web App
artifact package carries its `ContentWebAppModule` defaults as a package-owned
`appsettings.json` entry.

Host-specific config overlays are applied after artifact-owned configuration
files. Matching enabled rows in `omp.ConfigOverlayDocuments` and
`omp.ConfigOverlayConfigurationFiles` are selected for the current host and the
artifact being deployed. If an overlay file uses the same relative path as an
artifact-owned file, the overlay wins for that host. This is the preferred
model for customer, server, or environment values that should not be bundled
with a global artifact package.

New artifact packages can carry those files in the manifest envelope documented
in [`ARTIFACT_PACKAGES.md`](ARTIFACT_PACKAGES.md). HostAgent registers the
configuration rows during import and only extracts the declared payload to the
artifact store.

Configuration file content may contain HostAgent tokens. Tokens are expanded per
deployment, so one artifact-level row can still produce host- or
app-instance-specific runtime configuration:

- `{{Omp.AppInstanceId}}`
- `{{Omp.AppInstanceKey}}`
- `{{Omp.HostId}}`
- `{{Omp.HostKey}}`
- `{{Omp.ArtifactId}}`
- `{{Omp.ArtifactVersion}}`
- `{{Omp.TargetName}}`
- `{{Omp.ConnectionStrings.OmpDb}}`
- `{{Omp.ConnectionStrings.OmpDb.DatabaseName}}`

For values written inside JSON strings, use the `Omp.Json.` variants, for
example `{{Omp.Json.ConnectionStrings.OmpDb}}`. Those variants escape the value
for JSON string content but do not include the surrounding quotes.

## Import folder

HostAgent can poll a folder for portable deployment objects. The feature is off
unless `HostAgent:ArtifactZipImport:IsEnabled` is set to `true`. The setting
name still contains `ArtifactZipImport` for backward compatibility with older
runtime configuration files, but the accepted object format is now universal
module package zip only.

```json
{
  "HostAgent": {
    "ArtifactZipImport": {
      "IsEnabled": true,
      "ImportPath": "E:\\\\OMP\\\\ArtifactImports",
      "ProcessedPath": "",
      "FailedPath": "",
      "MaxFilesPerCycle": 10,
      "CopyConfigurationFilesFromPreviousVersion": true
    }
  }
}
```

The import folder recognizes only top-level `.zip` files that contain
`omp-universal-package.json`. Put module definitions, artifact packages, host
configuration objects, config overlays, widgets, and widget runtime data inside
that universal package instead of dropping individual object files into the
folder.

HostAgent performs only the unattended choices that are safe to automate. It
applies imported module definitions, runs embedded idempotent repair SQL for
non-platform modules, imports compatible artifact packages, registers packaged
configuration files or copies configuration file rows from the latest previous
matching artifact when enabled, and selects imported artifacts for matching
desired app rows.

For universal package zips, HostAgent treats each inner artifact package
independently. Identical already-registered artifacts are skipped, incompatible
historical artifact versions are skipped, and compatible missing artifacts are
imported. If the package carries an older module definition than the one already
applied, HostAgent stores it but keeps the newer applied definition. If several
compatible versions for the same app/package/target slot are present, only the
highest compatible version is selected as desired state so an exported package
with history cannot accidentally downgrade a running installation.

The folder import is intentionally strict. Duplicate module definitions with the
same version but different JSON, duplicate artifact versions with different
content, invalid package filenames, unknown module/app/package combinations,
unsafe repair SQL, and malformed JSON or zip files fail without prompting. An
incompatible inner artifact in a universal package is skipped as historical
package content. Successful files move to `processed`; failed files move to
`failed` with an adjacent `.error.txt`. Files that are not universal package
zips are treated as unsupported and moved to `failed` once HostAgent can open
them exclusively.

## Runtime file mirrors

HostAgent can mirror environment-owned files from a shared folder to local
runtime folders on every host. This is useful for multi-server web apps where
operators maintain content files centrally, but the application should read from
local `App_Data` folders during normal requests.

```json
{
  "HostAgent": {
    "FileMirrors": [
      {
        "SourcePath": "\\\\fileserver\\share\\OMP\\Data\\ContentReports",
        "TargetPath": "D:\\\\Netserv\\\\Web\\\\WebApps\\\\content\\\\App_Data\\\\ContentReports",
        "DeleteStaleTargetEntries": true
      },
      {
        "SourcePath": "\\\\fileserver\\share\\OMP\\Data\\ContentPages",
        "TargetPath": "D:\\\\Netserv\\\\Web\\\\WebApps\\\\content\\\\App_Data\\\\ContentPages",
        "DeleteStaleTargetEntries": true
      }
    ]
  }
}
```

Mirrors run after artifact provisioning and app deployment in each HostAgent
cycle. The target path must not be a drive or share root. `ExcludedEntries` uses
the same simple entry and filename-wildcard matching as app deployment
exclusions.

## Windows service app deployment

HostAgent can also deploy service-backed app instances after their artifacts
have been provisioned. This is enabled with:

```json
{
  "HostAgent": {
    "DeployServiceApps": true,
    "ServicesRoot": "D:\\OMP\\Services"
  }
}
```

The deployment handler consumes enabled `omp.AppInstances` whose artifact has
`PackageType = 'service-app'` and a successful `omp.HostArtifactStates` row. It
resolves the target folder from `AppInstances.InstallPath` when set. Relative
install paths are resolved under `HostAgent:ServicesRoot`; empty install paths
fall back to a folder derived from the resolved Windows service name.

The Windows service name is resolved from `AppInstances.InstallationName` when
that value is set to a specific service name. Generic values such as `default`
are ignored, and HostAgent falls back to the single executable name in the
artifact root. Service artifacts with multiple root executables must use a
specific `InstallationName`.

HostAgent can stop an existing service, mirror the provisioned artifact into
the runtime folder, create or update the Windows service with `sc.exe`, start
the service, and record the result in `omp.HostAppDeploymentStates`.

Service app deployment requires the HostAgent service identity to have Windows
service-control rights for the target service. The agent must be able to query,
stop, configure, and start the service before it can safely replace binaries in
the runtime folder. If `sc.exe query` fails with access denied, HostAgent treats
that as a deployment failure instead of assuming the service is missing; copying
over files while the service may still be running can leave assemblies locked.

Runtime-local files are preserved through
`HostAgent:ServiceAppDeploymentExcludedEntries`. The default exclusions are the
same as for web apps: `appsettings.json`, `appsettings.*.json`, `logs`, and
`App_Data`.

Service app deployment uses the same `omp.ArtifactConfigurationFiles` mechanism
as web apps. If a configured file is missing or differs from the database value,
HostAgent treats the app as needing deployment so it can stop the runtime,
mirror the artifact, rewrite the configuration files, and start the service
again.

Service app identity resolution is explicit and credential-safe. HostAgent
first checks `HostAgent:ServiceAppIdentityOverrides` using the app instance key,
installation name, target name, and resolved Windows service name. If no
override applies, it uses `HostAgent:ServiceAppUserName`,
`HostAgent:ServiceAppPassword`, and
`HostAgent:ServiceAppPasswordCredentialKey`. When those service-app defaults are
empty, HostAgent falls back to the self-upgrade service account settings so a
single bootstrap identity can be reused intentionally. Password values are never
logged.

HostAgent compares the desired and current Windows service accounts with
normalized account names. If the credential store automation mode is `Full`, it
can apply a mismatch automatically. If the mode is `PortalAdminApproved`, it
applies only after a Portal repair request. With automation disabled, HostAgent
records manual action required and leaves the service account unchanged. The
logs record the non-secret identity source, normalized desired and actual
account names, automation mode, repair request state, and whether HostAgent will
apply or defer the mismatch.

## HostAgent self-upgrade

HostAgent can prepare and hand over to a newer HostAgent artifact without using
the generic `service-app` deployment handler against the running service.
HostAgent artifacts use `PackageType = 'host-agent'` and are selected per host
through `omp.HostAgentDesiredStates`.

The active service publishes runtime information to
`omp.HostAgentRuntimeStates` and acquires a short lease in
`omp.HostAgentLeases` before each cycle. A normal HostAgent skips work when
another service currently owns that host lease. A HostAgent started in takeover
mode may take the lease so the new service can become the only active agent for
the host.

When the current HostAgent sees an enabled desired state with a different
artifact version, it provisions that artifact, copies it to a versioned folder
below `HostAgent:SelfUpgrade:InstallRoot`, writes a takeover appsettings file,
creates or updates a versioned Windows service, and starts it with:

```text
--service-name=<new service> --runtime-mode=Takeover --takeover-from=<old service>
```

The takeover service first validates that its own executable and required
credential-store entries are readable. Only after that readiness check succeeds
does it record the old service as quiescing, stop it, optionally delete it,
reconfigure its own Windows service command line back to normal mode, and
continue as the active HostAgent. If the readiness check fails, the new service
marks its runtime state as failed, releases the host lease, and stops its own
loop so the old service can resume the upgrade work on a later cycle.

Minimal configuration:

```json
{
  "HostAgent": {
    "ServiceName": "OMP.HostAgent",
    "Version": "0.3.10",
    "SelfUpgrade": {
      "IsEnabled": true,
      "InstallRoot": "D:\\OMP\\Services",
      "ServiceNamePrefix": "OMP.HostAgent",
      "ServiceAccountName": "",
      "ServiceAccountPasswordCredentialKey": "",
      "TakeoverStopTimeoutSeconds": 45,
      "DeletePreviousServiceAfterTakeover": true,
      "StartPreparedService": true,
      "PreparedServiceStartupVerificationDelaySeconds": 3
    },
    "CredentialStore": {
      "AutomationMode": "Full",
      "FilePath": "D:\\OMP\\Services\\HostAgent\\hostagent.credentials.json",
      "ProtectionScope": "LocalMachine",
      "EntropyPurpose": "OpenModulePlatform.HostAgent.CredentialStore.v1"
    }
  }
}
```

`ServiceAccountName` and `ServiceAccountPasswordCredentialKey` are optional.
Leave them empty for built-in service accounts. If HostAgent runs as a Windows
or AD account and needs that identity for IIS, SQL, or artifact-store access,
the bootstrapper writes the password to the local HostAgent credential store and
puts only the credential key in appsettings. The credential store uses Windows
DPAPI; with `ProtectionScope = "LocalMachine"` the encrypted password can only
be decrypted on the same Windows machine.

During self-upgrade the old HostAgent copies the credential store into the new
versioned install folder and rewrites the new appsettings to point at that local
copy. Cleanup of superseded HostAgent folders will not remove a folder that is
still referenced by the active credential-store path; this keeps interrupted or
legacy upgrades resumable until the next successful version has its own local
credential-store file.

`PreparedServiceStartupVerificationDelaySeconds` controls how long the current
HostAgent waits after starting the prepared takeover service before checking
that it is still running. The default is `3`, matching the original fixed
delay.

The desired version also performs cleanup during normal cycles. This makes the
upgrade idempotent if the process is interrupted after the new service starts:
the desired service can force the host lease, finish takeover bookkeeping, and
remove superseded HostAgent services and orphaned versioned folders later.
If Windows Service Control Manager refuses a stop command for a superseded
HostAgent with `1061` ("cannot accept control messages"), the desired HostAgent
waits briefly and then terminates the old HostAgent process before deleting the
old service. This recovery is intentionally limited to versioned HostAgent
services managed by the self-upgrade flow.

For a desired upgrade, insert or update one row in
`omp.HostAgentDesiredStates` for the concrete host and point it at the desired
`host-agent` artifact. The artifact must already be present in
`omp.Artifacts`, and its files must exist below `HostAgent:CentralArtifactRoot`.
When the import folder is enabled and a valid `host-agent` artifact zip is
imported by a HostAgent, the importer also points
`omp.HostAgentDesiredStates` at that artifact for the importing host. This makes
the file-drop import path self-contained: a new HostAgent zip can be dropped
into the import folder and the local HostAgent will prepare the versioned
replacement service on its next cycles.

The HostAgent-first package builder emits a normal bootstrap HostAgent zip for
first install and a standard `host-agent` artifact package for later
self-upgrades.

## Operator runbook

This section is for operators who need to recover from common HostAgent failure
modes without reading source code. All SQL examples assume the `OpenModulePlatform`
database and the `omp` schema. Take a database backup or run manual updates inside
a transaction whenever possible.

### Stuck `Pending` / `Running` HostDeployments after HostAgent crash

- **Symptom:** A host deployment request is not making progress. `omp.HostDeployments`
  shows a row in `Status = 0` (`Pending`) or `Status = 1` (`Running`) with an old
  `RequestedUtc`/`StartedUtc` and `CompletedUtc` is `NULL`. New deployment requests
  for the same host may not materialize.
- **Cause:** The HostAgent Windows service crashed or was killed while the row was
  in `Running`. The deployment row was never moved to a terminal state (`Succeeded`,
  `Failed`, or `Warning`) because there is no attempt counter or automatic reclaim
  for `HostDeployments`.
- **Recovery:**
  1. Identify the stuck row:
     ```sql
     SELECT HostDeploymentId, HostId, HostTemplateId, Status, RequestedUtc, StartedUtc, OutcomeMessage
     FROM omp.HostDeployments
     WHERE HostId = @HostId
       AND Status IN (0, 1)
       AND CompletedUtc IS NULL
     ORDER BY RequestedUtc;
     ```
  2. Wait one or two HostAgent refresh cycles (default 30 seconds). A `Pending`
     row should be claimed when the agent is healthy again, but a row that is
     already `Running` is not reclaimed by the current HostDeployment queue.
  3. If a `Running` row is still stuck, reset it to `Pending` so the next cycle can claim it:
     ```sql
     UPDATE omp.HostDeployments
     SET Status = 0,
         StartedUtc = NULL,
         OutcomeMessage = NULL,
         UpdatedUtc = SYSUTCDATETIME()
     WHERE HostDeploymentId = @HostDeploymentId
       AND Status = 1;
     ```
- **Note:** `omp.HostAgentJobs` has `MaxAttempts` (default 3) and `AttemptCount`,
  but `omp.HostDeployments` has **no attempt counter**. If a deployment repeatedly
  fails, investigate the underlying error in `OutcomeMessage` instead of retrying
  indefinitely.
- **Verification:** Within one refresh cycle the row should move to `Status = 1`
  (`Running`), then to `Status = 2`, `3`, or `4` with `CompletedUtc` set. Check
  `omp.HostAppDeploymentStates` and the HostAgent logs for the resulting app
  deployment state.

### HostAgent lease loss

- **Symptom:** A host stops provisioning artifacts and deploying apps even though
  the HostAgent Windows service appears to be running. No new work is processed.
- **Cause:** HostAgent uses a per-host database lease in `omp.HostAgentLeases`. The
  default lease duration is 90 seconds and it is renewed every 30 seconds. The lease
  can be lost if the service loses database connectivity, if the service process is
  hung, or if another HostAgent service took over the host. A dead process can also
  leave the lease row appearing "held" even though it is no longer updating it.
- **Recovery:**
  1. Inspect the current lease:
     ```sql
     SELECT HostId, ServiceName, LeaseToken, RuntimeMode, LeaseUntilUtc, UpdatedUtc
     FROM omp.HostAgentLeases
     WHERE HostId = @HostId;
     ```
  2. Compare `ServiceName` with the service that is actually running on the host.
     If `LeaseUntilUtc` is in the past and the listed service is dead, the lease is
     orphaned.
  3. Prefer waiting: if `LeaseUntilUtc` has expired, a healthy HostAgent on that
     host should reclaim the lease on its next cycle. If the host is still not
     processing work, release the stale lease manually:
     ```sql
     DELETE FROM omp.HostAgentLeases
     WHERE HostId = @HostId
       AND ServiceName = @DeadServiceName
       AND LeaseUntilUtc < SYSUTCDATETIME();
     ```
     Only delete the row when you are sure the listed service is no longer running.
     If `ServiceName` points to an unexpected live service, another host has taken
     over and you should investigate instead of deleting the lease.
- **Verification:** A new lease row appears with the current `ServiceName` and a
  future `LeaseUntilUtc`, and the host resumes processing deployments within one
  refresh cycle.

### Deployment lock file left behind

- **Symptom:** A single app target, such as Portal, refuses to redeploy even though
  HostAgent ran and other apps on the same host deployed successfully. The HostAgent
  log mentions the per-app deployment lock.
- **Cause:** The app wrote a deployment lock file at
  `App_Data/omp-deployment.lock.json` below its runtime root. The default TTL is
  5 minutes. If the app process crashed while the lock was held, the file can remain
  past its expiry and block HostAgent from replacing files or restarting the runtime.
- **Recovery:**
  1. Locate the lock file under the deployed app's root, for example:
     ```text
     D:\OMP\WebApps\portal\App_Data\omp-deployment.lock.json
     ```
  2. Open the file and check the expiry timestamp. The file uses the
     `OpenModulePlatform.DeploymentLock.v1` schema and contains an `ExpiryUtc` value.
  3. If `ExpiryUtc` is older than the current time, the lock is stale and safe to
     remove. Delete only that lock file; do not delete other `App_Data` contents.
- **Verification:** On the next HostAgent cycle the app deploys successfully. The
  lock file is gone and `omp.HostAppDeploymentStates` for that host/app instance is
  updated with a recent `LastAppliedUtc` and no lock-related warning.

### Retry exhaustion on HostAgent jobs

- **Symptom:** A HostAgent job, such as an artifact cleanup or health probe, is
  stuck and HostAgent is no longer retrying it.
- **Cause:** `omp.HostAgentJobs` has `MaxAttempts` (default 3). Each failed run
  increments `AttemptCount`. Once `AttemptCount >= MaxAttempts`, HostAgent stops
  retrying the job automatically.
- **Recovery:**
  1. Identify exhausted jobs:
     ```sql
     SELECT HostAgentJobId, HostId, JobType, Status, AttemptCount, MaxAttempts, LastError
     FROM omp.HostAgentJobs
     WHERE AttemptCount >= MaxAttempts
       AND Status NOT IN (2, 3)   -- not already terminal success/warning
     ORDER BY RequestedUtc;
     ```
  2. Review `LastError` and fix the underlying problem before retrying.
  3. Reset the attempt counter and release the lease so HostAgent can claim the job
     again:
     ```sql
     UPDATE omp.HostAgentJobs
     SET Status = 0,
         AttemptCount = 0,
         ClaimedByServiceName = NULL,
         ClaimedUtc = NULL,
         LeaseUntilUtc = NULL,
         LeaseToken = NULL,
         StartedUtc = NULL,
         CompletedUtc = NULL,
         LastError = NULL,
         UpdatedUtc = SYSUTCDATETIME()
     WHERE HostAgentJobId = @HostAgentJobId;
     ```
- **Verification:** The next HostAgent cycle claims the job (`Status` becomes 1,
  `StartedUtc` is set), runs it, and records a terminal result. `AttemptCount`
  increments and the job either succeeds or fails with a fresh `LastError`.

### Same-version / different-content import failure

- **Symptom:** A universal package zip dropped into the import folder is moved to
  `failed/` and an adjacent `.error.txt` reports a hash mismatch or "different
  content for an existing identity".
- **Cause:** HostAgent computes the SHA-256 of each imported artifact payload and
  stores it in `omp.Artifacts.Sha256`. For a given identity
  `(AppId, Version, PackageType, TargetName)`, HostAgent rejects a package whose
  hash differs from the already-registered artifact. This prevents silent
  replacement of a released version with different binaries.
- **Recovery:** Do **not** bypass this check by editing the database or deleting
  the existing artifact row. The correct fix is to bump the component version in
  `omp-components.json`, rebuild the universal package, and import the new package.
  1. Find the failed package and the error text:
     ```text
     E:\OMP\ArtifactImports\failed\<package-name>.zip
     E:\OMP\ArtifactImports\failed\<package-name>.zip.error.txt
     ```
  2. Read the error file to confirm the identity and hash mismatch.
  3. Update the relevant component version in `omp-components.json`, run the build
     scripts, and drop the new universal package into the import folder.
- **Verification:** The new package is moved to `processed/` and a row appears in
  `omp.Artifacts` with the bumped `Version`, the same `AppId`, `PackageType`, and
  `TargetName`, and the new `Sha256`. The desired app instances that reference that
  artifact are updated on the next HostAgent cycle.

### Load-balanced DataProtection key check

- **Symptom:** Authentication works on one IIS node but users see login loops or
  role switching fails on another node behind a load balancer.
- **Cause:** Every OMP web app that shares a DNS name must use the same
  `OmpAuth:DataProtectionKeyPath`. If one node points to a local-only key folder,
  cookies created on another node cannot be decrypted there. This is an
  operational configuration issue, not a HostAgent code bug.
- **Manual verification (no source-code or AI Orchestrator access needed):**
  1. On each IIS node, open the runtime `appsettings.json` for the affected app
     (for example `D:\OMP\WebApps\portal\appsettings.json`).
  2. Confirm that `OmpAuth:DataProtectionKeyPath` is set to the same shared path
     on every node.
  3. Confirm the shared folder exists and that each application pool identity has
     read/write access to it.
  4. Cross-reference the load-balancer scenario in
     [`HOSTING_WINDOWS_IIS.md`](HOSTING_WINDOWS_IIS.md) for additional checks such
     as forwarded headers, WebSockets, and sticky sessions.
- **Verification:** After aligning the path and permissions, recycle the app pools
  on all nodes and test authentication and role switching from each node. Auth
  cookies should now decrypt consistently across the farm.

## Artifact layout

Recommended central layout:

```text
\\server\omp-artifacts\worker\ibs-packager\1.0.0\...
\\server\omp-artifacts\channel-type\file-drop\0.1.0\...
\\server\omp-artifacts\channel-type\file-drop\0.2.0\...
```

Recommended local layout is managed by HostAgent:

```text
D:\OMP\Artifacts\worker\ibs-packager\1.0.0\...
D:\OMP\Artifacts\channel-type\file-drop\0.1.0\...
D:\OMP\Artifacts\channel-type\file-drop\0.2.0\...
```

## Stabilization note

The named-pipe RPC response writer uses an `async Task` method and awaits `StreamWriter.WriteLineAsync(...)` directly. This avoids SDK-specific return-type issues around `WriteLineAsync` and keeps the implementation compatible with the .NET 10 SDK used by the project.

## Not implemented yet

- package extraction from archive files
- HTTP/S3/Azure Blob download sources
- artifact retention/cleanup
- signing/certificate verification
- remote HostAgent management API
- service credential provisioning and rotation

## v2.2 stabilization note

HostAgent remains the owner of artifact provisioning. WorkerManager should not copy artifacts directly; it should consume provisioned local paths from HostAgent or the local immutable artifact cache.

The repository build helper now only passes `--no-restore` to `dotnet build` when the caller explicitly uses `-NoRestore`. This keeps normal local builds sensitive to dependency changes while still allowing CI-style no-restore builds when requested.

## v2.3 stabilization note

Path handling in `ArtifactProvisioner` now uses explicit root-bound resolution for relative artifact paths and staging paths. This avoids `Path.Combine` behavior where a rooted later argument can silently discard earlier root arguments.

The HostAgent services now use explicit expected exception types in recovery paths instead of broad catch-all handlers. RPC timeout handling remains non-fatal for the service.

The legacy dev install script now creates the `omp_portal` schema and registers the `omp_portal` module with `SchemaName = 'omp_portal'`, matching the newer modular initialization scripts.
