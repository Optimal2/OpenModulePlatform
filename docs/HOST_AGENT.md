# HostAgent

HostAgent is host-local OMP/ODV infrastructure. It is intentionally generic and is not tied to a specific module such as IbsPackager.

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
- Web app deployment state in `omp.HostAppDeploymentStates`
- Local named-pipe RPC for synchronous artifact provisioning:
  - operation: `ensureArtifact`
  - request fields: `artifactId`, optional `desiredLocalPath`
  - response fields: `success`, `state`, `localPath`, `contentSha256`, `errorMessage`

## SQL objects

- `omp.HostArtifactRequirements`
- `omp.HostArtifactStates`
- `omp.HostAppDeploymentStates`
- `omp.WorkerInstances`
- `omp.WorkerInstanceRuntimeStates`

`omp.WorkerInstances` introduces process-level desired state below an `omp.AppInstances` row. This supports modules where one app instance needs multiple isolated worker processes, for example one process per channel.

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
app instance key; `HostAgent:IisAppPoolUserName` and
`HostAgent:IisAppPoolPassword` can be set when app pools must run as a specific
Windows identity.

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

Artifact packages should not contain runtime configuration files in the first
place. The exclusions above are a safety net for existing runtime folders and
older packages, while Portal artifact upload rejects known runtime config files
such as `appsettings*.json` and `odv.site.config.js`. Environment-owned config
is either written by the bootstrap/deployment layer or managed through
`omp.ArtifactConfigurationFiles`.

Enabled rows in `omp.ArtifactConfigurationFiles` are written after the artifact
has been mirrored. Each row belongs to one artifact and contains a path relative
to the deployed app root plus the exact text HostAgent should write. HostAgent
uses this for deployment-owned runtime files, for example a site-local
`odv.site.config.js`, without requiring a new artifact zip for every
configuration change.

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

For values written inside JSON strings, use the `Omp.Json.` variants, for
example `{{Omp.Json.ConnectionStrings.OmpDb}}`. Those variants escape the value
for JSON string content but do not include the surrounding quotes.

## Artifact zip import

HostAgent can poll a folder for immutable artifact zip files. The feature is
off unless `HostAgent:ArtifactZipImport:IsEnabled` is set to `true`.

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

Each zip filename must use the same metadata format as Portal upload:
`moduleKey__appKey__packageType__targetName__version.zip`. HostAgent validates
and extracts the zip below `CentralArtifactRoot`, rejects duplicate extracted
content by SHA-256, registers the `omp.Artifacts` row, copies configuration file
rows from the latest previous matching artifact when enabled, and selects the
new artifact for matching desired app rows. Successful files move to
`processed`; failed files move to `failed` with an adjacent `.error.txt`.

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
        "SourcePath": "\\\\server\\share\\EMP\\Data\\ContentReports",
        "TargetPath": "D:\\\\Netserv\\\\Web\\\\WebApps\\\\content\\\\App_Data\\\\ContentReports",
        "DeleteStaleTargetEntries": true
      },
      {
        "SourcePath": "\\\\server\\share\\EMP\\Data\\ContentPages",
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

HostAgent does not currently rotate Windows service credentials. If a service
already exists, its configured account is preserved. If HostAgent creates a new
service, it uses the Windows default service account. Environment-specific
service accounts should therefore be bootstrapped before HostAgent owns regular
version updates, or added through a future local credential provider.

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
- HostAgent self-update orchestration

## v2.2 stabilization note

HostAgent remains the owner of artifact provisioning. WorkerManager should not copy artifacts directly; it should consume provisioned local paths from HostAgent or the local immutable artifact cache.

The repository build helper now only passes `--no-restore` to `dotnet build` when the caller explicitly uses `-NoRestore`. This keeps normal local builds sensitive to dependency changes while still allowing CI-style no-restore builds when requested.

## v2.3 stabilization note

Path handling in `ArtifactProvisioner` now uses explicit root-bound resolution for relative artifact paths and staging paths. This avoids `Path.Combine` behavior where a rooted later argument can silently discard earlier root arguments.

The HostAgent services now use explicit expected exception types in recovery paths instead of broad catch-all handlers. RPC timeout handling remains non-fatal for the service.

The legacy dev install script now creates the `omp_portal` schema and registers the `omp_portal` module with `SchemaName = 'omp_portal'`, matching the newer modular initialization scripts.
