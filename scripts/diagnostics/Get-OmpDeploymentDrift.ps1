# Reads the same deployment-drift picture as the Portal's Host deployments page
# (HostDrift summary/details) plus artifact provisioning and worker runtime
# state, directly from the database. Built for scripted deploy verification:
#
#   Get-OmpDeploymentDrift.ps1                  # human-readable status
#   Get-OmpDeploymentDrift.ps1 -Json            # machine-readable snapshot
#   Get-OmpDeploymentDrift.ps1 -Wait -TimeoutSeconds 300
#                                               # poll until converged (exit 0),
#                                               # timeout (exit 2) or error (1)
#
# Converged means: every enabled host has no pending/failed/warning apps, no
# HostAgent upgrade pending, no artifact provisioning pending/failed, and no
# worker that should run but is not running.
[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string]$HostKey,
    [switch]$Json,
    [switch]$Wait,
    [int]$TimeoutSeconds = 300,
    [int]$PollSeconds = 10
)

$ErrorActionPreference = 'Stop'

function Open-Connection {
    $conn = New-Object System.Data.SqlClient.SqlConnection
    $conn.ConnectionString = "Server=$Server;Database=$Database;Integrated Security=True;Encrypt=False"
    $conn.Open()
    return $conn
}

function Invoke-Query {
    param($Connection, [string]$Sql)
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql
    $cmd.CommandTimeout = 60
    $table = New-Object System.Data.DataTable
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    [void]$adapter.Fill($table)
    # -NoEnumerate: returning a DataTable through the pipeline would unroll it
    # into DataRows, which breaks .Rows-based checks at the call sites.
    Write-Output $table -NoEnumerate
}

# The identity-check columns are newer than some databases; mirror the Portal's
# dynamic column probe so the summary works on both schema generations.
$identityProbeSql = "SELECT CASE WHEN COL_LENGTH('omp.HostAppDeploymentStates','IdentityCheckStatus') IS NULL THEN 0 ELSE 1 END"

function Get-DriftSnapshot {
    $conn = Open-Connection
    try {
        $probe = $conn.CreateCommand()
        $probe.CommandText = $identityProbeSql
        $hasIdentity = [int]$probe.ExecuteScalar() -eq 1
        $identityWarning = if ($hasIdentity) { "state.IdentityCheckStatus IN (N'ManualActionRequired', N'WaitingForPortalAdminApproval')" } else { '1 = 0' }
        $hostFilter = if ($HostKey) { "AND h.HostKey = N'$($HostKey.Replace("'", "''"))'" } else { '' }

        $summarySql = @"
WITH DesiredTemplateApps AS
(
    SELECT h.HostId, h.HostKey, mi.ModuleInstanceId, tai.AppId, tai.AppInstanceKey,
           tai.DesiredArtifactId, desiredArtifact.PackageType AS DesiredPackageType,
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
    SELECT desired.HostId, desired.DesiredArtifactId, desired.DesiredPackageType,
           appInstance.AppInstanceId, appInstance.ArtifactId AS MaterializedArtifactId,
           state.ArtifactId AS RuntimeArtifactId, state.DeploymentState,
           state.LastCheckedUtc, state.LastAppliedUtc, state.LastError,
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
    LEFT JOIN omp.HostArtifactStates desiredArtifactState
        ON desiredArtifactState.HostId = desired.HostId AND desiredArtifactState.ArtifactId = desired.DesiredArtifactId
),
Aggregated AS
(
    SELECT HostId,
           COUNT(1) AS DesiredAppCount,
           SUM(CASE WHEN AppInstanceId IS NOT NULL AND DeploymentState = 2
                         AND ISNULL(RuntimeArtifactId, -1) = ISNULL(DesiredArtifactId, -1)
                         AND LastError IS NULL AND ISNULL(DesiredProvisioningState, 2) NOT IN (3, 4)
                         AND HasIdentityWarning = 0 THEN 1 ELSE 0 END) AS InSyncAppCount,
           SUM(CASE WHEN AppInstanceId IS NULL
                         OR (DesiredPackageType IN (N'web-app', N'service-app') AND RuntimeArtifactId IS NULL)
                         OR DeploymentState = 0
                         OR ISNULL(RuntimeArtifactId, -1) <> ISNULL(DesiredArtifactId, -1) THEN 1 ELSE 0 END) AS PendingAppCount,
           SUM(CASE WHEN DeploymentState = 3 OR LastError IS NOT NULL OR DesiredProvisioningState = 3 THEN 1 ELSE 0 END) AS FailedAppCount,
           SUM(CASE WHEN DeploymentState = 4 OR HasIdentityWarning = 1 OR DesiredProvisioningState = 4 THEN 1 ELSE 0 END) AS WarningAppCount,
           MAX(LastCheckedUtc) AS LastCheckedUtc,
           MAX(LastAppliedUtc) AS LastAppliedUtc
    FROM ResolvedApps
    GROUP BY HostId
)
SELECT h.HostKey,
       ISNULL(aggregated.DesiredAppCount, 0) AS DesiredApps,
       ISNULL(aggregated.InSyncAppCount, 0) AS InSync,
       ISNULL(aggregated.PendingAppCount, 0) AS Pending,
       ISNULL(aggregated.FailedAppCount, 0) AS Failed,
       ISNULL(aggregated.WarningAppCount, 0) AS Warnings,
       aggregated.LastCheckedUtc,
       desiredArtifact.Version AS HostAgentDesired,
       runtimeState.Version AS HostAgentCurrent,
       runtimeState.LastSeenUtc AS HostAgentLastSeenUtc,
       CAST(CASE WHEN desiredArtifact.Version IS NOT NULL
                      AND ISNULL(runtimeState.Version, N'') <> desiredArtifact.Version THEN 1 ELSE 0 END AS bit) AS HostAgentUpgradePending
FROM omp.Hosts h
LEFT JOIN Aggregated aggregated ON aggregated.HostId = h.HostId
LEFT JOIN omp.HostAgentDesiredStates desiredState ON desiredState.HostId = h.HostId
LEFT JOIN omp.Artifacts desiredArtifact ON desiredArtifact.ArtifactId = desiredState.ArtifactId
OUTER APPLY
(
    SELECT TOP (1) runtime.*
    FROM omp.HostAgentRuntimeStates runtime
    WHERE runtime.HostId = h.HostId
    ORDER BY runtime.IsActive DESC,
             COALESCE(runtime.LastSeenUtc, runtime.UpdatedUtc, runtime.CreatedUtc) DESC,
             runtime.ServiceName
) runtimeState
WHERE h.IsEnabled = 1 $hostFilter
ORDER BY h.HostKey;
"@

        # Artifact provisioning state for every desired artifact on each host —
        # this is what covers worker/channel-type packages that the app summary
        # (web-app/service-app only) does not see. Only artifacts with an
        # enabled requirement count: states left behind for retired versions
        # are noise, not drift.
        $artifactSql = @"
SELECT h.HostKey, a.PackageType, a.TargetName, a.Version,
       has.ProvisioningState, has.LastError, has.UpdatedUtc
FROM omp.HostArtifactStates has
INNER JOIN omp.Hosts h ON h.HostId = has.HostId
INNER JOIN omp.Artifacts a ON a.ArtifactId = has.ArtifactId
WHERE h.IsEnabled = 1 $hostFilter
  AND (has.ProvisioningState NOT IN (2) OR has.LastError IS NOT NULL)
  AND EXISTS
  (
      SELECT 1
      FROM omp.HostArtifactRequirements r
      WHERE r.HostId = has.HostId
        AND r.ArtifactId = has.ArtifactId
        AND r.IsEnabled = 1
  )
ORDER BY h.HostKey, a.PackageType, a.TargetName, a.Version;
"@

        $workerSql = @"
SELECT i.InstanceKey, mi.ModuleInstanceKey, a.AppKey, ai.AppInstanceKey,
       ISNULL(h.HostKey, ht.TemplateKey) AS Placement,
       ai.DesiredState, ISNULL(rs.ObservedState, 0) AS ObservedState,
       rs.LastSeenUtc, rs.LastExitCode, rs.StatusMessage
FROM omp.AppInstances ai
INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
INNER JOIN omp.AppWorkerDefinitions awd ON awd.AppId = ai.AppId
LEFT JOIN omp.Hosts h ON h.HostId = ai.HostId
LEFT JOIN omp.HostTemplates ht ON ht.HostTemplateId = ai.TargetHostTemplateId
LEFT JOIN omp.AppInstanceRuntimeStates rs ON rs.AppInstanceId = ai.AppInstanceId
WHERE ai.IsEnabled = 1 AND ai.IsAllowed = 1 AND ai.DesiredState = 1
  AND ISNULL(rs.ObservedState, 0) <> 2
ORDER BY i.InstanceKey, mi.ModuleInstanceKey, ai.AppInstanceKey;
"@

        $summary = Invoke-Query $conn $summarySql
        $artifacts = Invoke-Query $conn $artifactSql
        $workers = Invoke-Query $conn $workerSql

        $converged = $true
        foreach ($row in $summary.Rows) {
            if ([int]$row.Pending -gt 0 -or [int]$row.Failed -gt 0 -or [int]$row.Warnings -gt 0 -or [bool]$row.HostAgentUpgradePending) {
                $converged = $false
            }
        }
        if ($artifacts.Rows.Count -gt 0 -or $workers.Rows.Count -gt 0) {
            $converged = $false
        }

        return [pscustomobject]@{
            CheckedUtc = (Get-Date).ToUniversalTime().ToString('s') + 'Z'
            Converged  = $converged
            Hosts      = @($summary | Select-Object HostKey, DesiredApps, InSync, Pending, Failed, Warnings, HostAgentDesired, HostAgentCurrent, HostAgentUpgradePending, HostAgentLastSeenUtc)
            ArtifactIssues = @($artifacts | Select-Object HostKey, PackageType, TargetName, Version, ProvisioningState, LastError, UpdatedUtc)
            WorkerIssues   = @($workers | Select-Object InstanceKey, ModuleInstanceKey, AppKey, AppInstanceKey, Placement, ObservedState, LastSeenUtc, LastExitCode, StatusMessage)
        }
    }
    finally {
        $conn.Dispose()
    }
}

function Write-Snapshot {
    param($Snapshot)
    if ($Json) {
        $Snapshot | ConvertTo-Json -Depth 6
        return
    }

    Write-Host ("Converged: {0}   ({1})" -f $Snapshot.Converged, $Snapshot.CheckedUtc)
    Write-Host ''
    $Snapshot.Hosts | Format-Table HostKey, DesiredApps, InSync, Pending, Failed, Warnings, HostAgentDesired, HostAgentCurrent, HostAgentUpgradePending -AutoSize | Out-String | Write-Host
    if ($Snapshot.ArtifactIssues.Count -gt 0) {
        Write-Host 'Artifact provisioning issues (state<>2 or error):'
        $Snapshot.ArtifactIssues | Format-Table HostKey, PackageType, TargetName, Version, ProvisioningState, LastError -AutoSize | Out-String | Write-Host
    }
    if ($Snapshot.WorkerIssues.Count -gt 0) {
        Write-Host 'Workers not running (desired=run, observed<>Running):'
        $Snapshot.WorkerIssues | Format-Table ModuleInstanceKey, AppKey, AppInstanceKey, Placement, ObservedState, LastSeenUtc, StatusMessage -AutoSize | Out-String | Write-Host
    }
}

if (-not $Wait) {
    $snapshot = Get-DriftSnapshot
    Write-Snapshot $snapshot
    if ($snapshot.Converged) { exit 0 } else { exit 2 }
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ($true) {
    $snapshot = Get-DriftSnapshot
    if ($snapshot.Converged) {
        Write-Snapshot $snapshot
        exit 0
    }

    if ((Get-Date) -ge $deadline) {
        Write-Snapshot $snapshot
        Write-Warning "Deployment did not converge within $TimeoutSeconds seconds."
        exit 2
    }

    if (-not $Json) {
        $pending = ($snapshot.Hosts | Measure-Object -Property Pending -Sum).Sum
        $artifactCount = $snapshot.ArtifactIssues.Count
        $workerCount = $snapshot.WorkerIssues.Count
        Write-Host ("Waiting... pending apps: {0}, artifact issues: {1}, workers not running: {2}" -f $pending, $artifactCount, $workerCount)
    }
    Start-Sleep -Seconds $PollSeconds
}
