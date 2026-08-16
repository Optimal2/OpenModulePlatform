# Lists EVERY desired app on every enabled host with its desired version, the
# version actually deployed, and where it stands.
#
#   Get-OmpAppDeploymentDetail.ps1                    # all apps, worst first
#   Get-OmpAppDeploymentDetail.ps1 -PendingOnly       # only what is not in sync
#   Get-OmpAppDeploymentDetail.ps1 -HostKey LINUS-LAPTOP
#   Get-OmpAppDeploymentDetail.ps1 -Json
#
# Why this exists next to Get-OmpDeploymentDrift.ps1 rather than inside it:
# that script is the deployment gate. It answers "is this host converged" and
# "how many apps are pending", and scripted deploys exit on its return code.
# It deliberately aggregates. This one answers the other question -- WHICH apps
# -- and is kept separate so a change here can never break the gate.
#
# The status rules below mirror the CASE expressions in Get-OmpDeploymentDrift's
# Aggregated CTE exactly. If the two ever disagree, this script is the one that
# is wrong: compare the per-status counts here against InSync/Pending/Failed/
# Warnings there. They matched when this was written (21/21 in sync on
# LINUS-LAPTOP).
#
# Note what "waiting for the HostAgent" actually looks like here. An app is
# Pending when the agent has not yet materialised it (no AppInstance), has not
# reported a runtime artifact, or reports a different artifact than the desired
# one. The HostAgent's OWN version is not in this list -- that is the
# HostAgentUpgradePending column in Get-OmpDeploymentDrift, and it is worth
# checking first: an agent that is itself out of date explains a whole host's
# worth of pending apps at once.

[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string]$HostKey,
    [switch]$PendingOnly,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

$conn = New-Object System.Data.SqlClient.SqlConnection
$conn.ConnectionString = "Server=$Server;Database=$Database;Integrated Security=True;Encrypt=False"
$conn.Open()

try {
    # The identity-check column only exists on newer schemas. Probing for it
    # keeps the script usable against a host that has not been migrated yet,
    # instead of failing with an unhelpful "invalid column name".
    $probe = $conn.CreateCommand()
    $probe.CommandText = "SELECT CASE WHEN COL_LENGTH('omp.HostAppDeploymentStates','IdentityCheckStatus') IS NULL THEN 0 ELSE 1 END"
    $hasIdentity = [int]$probe.ExecuteScalar() -eq 1
    $identityWarning = if ($hasIdentity) {
        "state.IdentityCheckStatus IN (N'ManualActionRequired', N'WaitingForPortalAdminApproval')"
    } else { '1 = 0' }

    $hostFilter = if ($HostKey) { "AND h.HostKey = N'$($HostKey.Replace("'", "''"))'" } else { '' }

    $sql = @"
WITH DesiredTemplateApps AS
(
    SELECT h.HostId, h.HostKey, mi.ModuleInstanceId, mi.ModuleInstanceKey,
           tai.AppId, tai.AppInstanceKey, app.AppKey,
           tai.DesiredArtifactId, desiredArtifact.PackageType AS DesiredPackageType,
           desiredArtifact.Version AS DesiredVersion,
           tai.InstanceTemplateHostId, tai.TargetHostTemplateId
    FROM omp.Hosts h
    INNER JOIN omp.Instances i ON i.InstanceId = h.InstanceId
    INNER JOIN omp.InstanceTemplates it ON it.InstanceTemplateId = i.InstanceTemplateId
    INNER JOIN omp.InstanceTemplateModuleInstances tmi
        ON tmi.InstanceTemplateId = it.InstanceTemplateId AND tmi.IsEnabled = 1
    INNER JOIN omp.ModuleInstances mi
        ON mi.InstanceId = i.InstanceId AND mi.ModuleInstanceKey = tmi.ModuleInstanceKey AND mi.IsEnabled = 1
    INNER JOIN omp.InstanceTemplateAppInstances tai
        ON tai.InstanceTemplateModuleInstanceId = tmi.InstanceTemplateModuleInstanceId
       AND tai.IsEnabled = 1 AND tai.IsAllowed = 1 AND tai.DesiredState = 1
    INNER JOIN omp.Apps app ON app.AppId = tai.AppId AND app.IsEnabled = 1
    INNER JOIN omp.Artifacts desiredArtifact
        ON desiredArtifact.ArtifactId = tai.DesiredArtifactId
       AND desiredArtifact.IsEnabled = 1
       AND desiredArtifact.PackageType IN (N'web-app', N'service-app')
    LEFT JOIN omp.InstanceTemplateHosts pinnedHost
        ON pinnedHost.InstanceTemplateHostId = tai.InstanceTemplateHostId AND pinnedHost.IsEnabled = 1
    WHERE h.IsEnabled = 1 AND i.IsEnabled = 1 AND it.IsEnabled = 1
      $hostFilter
      AND
      (
          (tai.InstanceTemplateHostId IS NOT NULL AND pinnedHost.HostKey = h.HostKey
           AND EXISTS (SELECT 1 FROM omp.HostDeploymentAssignments assignment
                       WHERE assignment.HostId = h.HostId AND assignment.HostTemplateId = pinnedHost.HostTemplateId AND assignment.IsActive = 1))
          OR (tai.InstanceTemplateHostId IS NULL AND tai.TargetHostTemplateId IS NULL AND desiredArtifact.PackageType = N'web-app')
          OR (tai.InstanceTemplateHostId IS NULL AND tai.TargetHostTemplateId IS NOT NULL
              AND EXISTS (SELECT 1 FROM omp.HostDeploymentAssignments assignment
                          WHERE assignment.HostId = h.HostId AND assignment.HostTemplateId = tai.TargetHostTemplateId AND assignment.IsActive = 1))
      )
),
ResolvedApps AS
(
    SELECT desired.HostKey, desired.ModuleInstanceKey, desired.AppKey, desired.AppInstanceKey,
           desired.DesiredPackageType, desired.DesiredVersion, desired.DesiredArtifactId,
           appInstance.AppInstanceId,
           state.ArtifactId AS RuntimeArtifactId, state.DeploymentState, state.LastError,
           state.LastCheckedUtc,
           runtimeArtifact.Version AS RuntimeVersion,
           desiredArtifactState.ProvisioningState AS DesiredProvisioningState,
           CAST(CASE WHEN $identityWarning THEN 1 ELSE 0 END AS bit) AS HasIdentityWarning
    FROM DesiredTemplateApps desired
    LEFT JOIN omp.AppInstances appInstance
        ON appInstance.ModuleInstanceId = desired.ModuleInstanceId
       AND appInstance.AppId = desired.AppId AND appInstance.AppInstanceKey = desired.AppInstanceKey
       AND appInstance.IsEnabled = 1 AND appInstance.IsAllowed = 1 AND appInstance.DesiredState = 1
       AND ((desired.InstanceTemplateHostId IS NOT NULL AND appInstance.HostId = desired.HostId)
            OR (desired.InstanceTemplateHostId IS NULL AND appInstance.HostId IS NULL
                AND ISNULL(appInstance.TargetHostTemplateId, -1) = ISNULL(desired.TargetHostTemplateId, -1)))
    LEFT JOIN omp.HostAppDeploymentStates state
        ON state.HostId = desired.HostId AND state.AppInstanceId = appInstance.AppInstanceId
    LEFT JOIN omp.Artifacts runtimeArtifact
        ON runtimeArtifact.ArtifactId = state.ArtifactId
    LEFT JOIN omp.HostArtifactStates desiredArtifactState
        ON desiredArtifactState.HostId = desired.HostId AND desiredArtifactState.ArtifactId = desired.DesiredArtifactId
),
Classified AS
(
    SELECT HostKey, ModuleInstanceKey, AppKey, AppInstanceKey,
           DesiredPackageType AS PackageType,
           DesiredVersion,
           ISNULL(RuntimeVersion, N'-') AS RuntimeVersion,
           LastError, LastCheckedUtc,
           CASE
               WHEN DeploymentState = 3 OR LastError IS NOT NULL OR DesiredProvisioningState = 3 THEN 0
               WHEN DeploymentState = 4 OR HasIdentityWarning = 1 OR DesiredProvisioningState = 4 THEN 1
               WHEN AppInstanceId IS NULL
                    OR (DesiredPackageType IN (N'web-app', N'service-app') AND RuntimeArtifactId IS NULL)
                    OR DeploymentState = 0
                    OR ISNULL(RuntimeArtifactId, -1) <> ISNULL(DesiredArtifactId, -1) THEN 2
               WHEN DeploymentState = 2 AND LastError IS NULL THEN 3
               ELSE 4
           END AS Rank
    FROM ResolvedApps
)
SELECT HostKey, ModuleInstanceKey, AppKey, AppInstanceKey, PackageType,
       DesiredVersion, RuntimeVersion,
       CASE Rank WHEN 0 THEN 'Failed' WHEN 1 THEN 'Warning' WHEN 2 THEN 'Pending'
                 WHEN 3 THEN 'InSync' ELSE 'Unknown' END AS Status,
       LastError, LastCheckedUtc
FROM Classified
$(if ($PendingOnly) { 'WHERE Rank <> 3' })
ORDER BY HostKey, Rank, ModuleInstanceKey, AppKey, AppInstanceKey;
"@

    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $cmd.CommandTimeout = 60
    $table = New-Object System.Data.DataTable
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    [void]$adapter.Fill($table)

    $rows = @($table | Select-Object HostKey, ModuleInstanceKey, AppKey, AppInstanceKey,
                                     PackageType, DesiredVersion, RuntimeVersion, Status,
                                     LastError, LastCheckedUtc)

    if ($Json) {
        [pscustomobject]@{
            CheckedUtc = (Get-Date).ToUniversalTime().ToString('s') + 'Z'
            Apps       = $rows
        } | ConvertTo-Json -Depth 5
    }
    else {
        if ($rows.Count -eq 0) {
            # An empty table means two very different things, and saying which
            # matters: with -PendingOnly it is good news, without it the host
            # filter matched nothing.
            if ($PendingOnly) { Write-Host 'Inga appar väntar, misslyckades eller varnar. Allt är i synk.' }
            else { Write-Warning "Inga appar hittades$(if ($HostKey) { " för HostKey '$HostKey'" }). Stämmer värdnamnet?" }
        }
        else {
            $rows | Format-Table HostKey, ModuleInstanceKey, AppKey, PackageType,
                                 DesiredVersion, RuntimeVersion, Status -AutoSize
            Write-Host ''
            $rows | Group-Object Status | Sort-Object Name | ForEach-Object {
                Write-Host ("  {0,-8} {1}" -f $_.Name, $_.Count)
            }
            foreach ($fel in $rows | Where-Object { $_.LastError }) {
                Write-Host ''
                Write-Host ("  {0} / {1}: {2}" -f $fel.AppKey, $fel.AppInstanceKey, $fel.LastError) -ForegroundColor Yellow
            }
        }
    }

    # Exit code mirrors Get-OmpDeploymentDrift: 0 = nothing outstanding,
    # 2 = something is, 1 = the question could not be answered.
    #
    # That last one matters. Without it an unknown -HostKey returns zero rows,
    # zero rows means zero outstanding apps, and the script reports success for
    # a host it never found -- a gate that passes because it looked in the wrong
    # place. Zero rows is only good news when -PendingOnly asked for exactly
    # that.
    if ($rows.Count -eq 0 -and -not $PendingOnly) {
        exit 1
    }

    $utestaende = @($rows | Where-Object { $_.Status -ne 'InSync' }).Count
    exit $(if ($utestaende -eq 0) { 0 } else { 2 })
}
finally {
    $conn.Dispose()
}
