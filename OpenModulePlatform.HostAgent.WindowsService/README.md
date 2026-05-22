# OpenModulePlatform.HostAgent.WindowsService

HostAgent v1 is the host-local OMP service responsible for provisioning immutable artifact versions to a local cache.

Implemented in v1:

- runs as a Windows Service
- supports `--run-once` for installers that need a deterministic first
  provisioning/deployment cycle before starting the long-running service
- materializes enabled instance-template topology for the local host when
  `HostAgent:MaterializeTemplates` is enabled
- claims and completes pending host deployment requests when
  `HostAgent:ProcessHostDeployments` is enabled
- reads desired artifacts from OMP SQL
- provisions app-instance artifacts and explicit host artifact requirements
- verifies SHA-256 when `omp.Artifacts.Sha256` is populated
- writes provisioning state to `omp.HostArtifactStates`
- deploys provisioned IIS `web-app` artifacts when `HostAgent:DeployWebApps`
  is enabled
- deploys provisioned Windows `service-app` artifacts when
  `HostAgent:DeployServiceApps` is enabled
- writes app deployment state to `omp.HostAppDeploymentStates`
- can be configured with a host-local credential store model for service
  identity, app-pool identity, and HostAgent self-upgrade automation

Expected first use:

1. Publish an artifact to a central artifact root.
2. Register the artifact in `omp.Artifacts` with `RelativePath` pointing to the artifact directory or file.
3. Assign the artifact to an app instance, or create an explicit `omp.HostArtifactRequirements` row.
4. HostAgent copies the artifact to `HostAgent:LocalArtifactCacheRoot`.
5. If the artifact is a `web-app` package and `HostAgent:DeployWebApps` is
   enabled, HostAgent mirrors it to the IIS runtime folder while preserving
   local configuration and runtime data exclusions. By default this uses
   `app_offline.htm` instead of `appcmd.exe`, so normal web-app deployments do
   not need IIS administration permissions.
6. If the artifact is a `service-app` package and
   `HostAgent:DeployServiceApps` is enabled, HostAgent mirrors it to the
   service runtime folder and creates or updates the Windows service.
7. Worker Manager can be configured to resolve app-instance artifacts from `omp.HostArtifactStates`.

## Credential Store

HostAgent supports a credential-store configuration section for identities used
when automation creates IIS app pools, creates Windows services, repairs service
logon drift, or prepares a HostAgent self-upgrade.

Example configuration:

```json
"CredentialStore": {
  "AutomationMode": "Disabled",
  "FilePath": "",
  "ProtectionScope": "LocalMachine",
  "EntropyPurpose": "OpenModulePlatform.HostAgent.CredentialStore.v1"
}
```

`AutomationMode` has three supported values:

- `Disabled`: HostAgent does not read or write stored credentials. Operators
  remain responsible for service and app-pool identities.
- `PortalAdminApproved`: HostAgent reports service-app identity drift and waits
  for a PortalAdmin repair request before using stored credentials to change the
  Windows service logon account.
- `Full`: HostAgent can use stored credentials whenever the desired host state
  requires it, including automatic service-app identity repair.

Credential files store readable credential keys and usernames, but passwords are
encrypted with Windows DPAPI. `LocalMachine` protects the value for the current
machine/server, so copying the file to another host does not make it
decryptable. `CurrentUser` is stricter and also binds decryption to the Windows
identity that encrypted the value.

For service apps, `HostAgent:ServiceAppUserName` and
`HostAgent:ServiceAppPasswordCredentialKey` define the default Windows service
identity. When they are omitted, HostAgent falls back to
`HostAgent:SelfUpgrade:ServiceAccountName` and
`HostAgent:SelfUpgrade:ServiceAccountPasswordCredentialKey`. Per-service
exceptions can be configured in `HostAgent:ServiceAppIdentityOverrides`, keyed by
app instance key, installation name, target name, or resolved Windows service
name.

Not implemented in v1:

- remote HTTP download
- package extraction
- retention/cleanup policy

See `../docs/HOST_AGENT_TEMPLATE_AUTOMATION.md` for the roadmap from template
metadata to full HostAgent-driven deployment automation.
