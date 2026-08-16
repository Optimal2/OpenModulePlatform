# Lists EVERY desired app on every enabled host with its desired version, the
# version actually deployed, and where it stands -- web apps, service apps,
# workers and worker hosts.
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
# Classified CTE, deliberately, expression for expression. Neither script is
# authoritative over the other and neither is a safe default to believe: they are
# the same ladder applied to the same rows, so a disagreement means one of the two
# has been edited without the other and BOTH need re-reading before either result
# is used. (R12-F9: this comment used to say "if the two ever disagree, this
# script is the one that is wrong". A truth table run against the live database on
# 2026-08-16 showed the opposite in the only case where they could disagree --
# DeploymentState = 1 fell into no bucket in the gate and was counted healthy,
# while this script classified it Unknown and exited 2. The gate has since been
# given the same ELSE branch, but the instruction to distrust the correct signal
# was worse than the divergence it described.)
#
# Note what "waiting for the HostAgent" actually looks like here. An app is
# Pending when the agent has not yet materialised it (no AppInstance), has not
# reported a runtime artifact, or reports a different artifact than the desired
# one. The HostAgent's OWN version is not in this list -- that is the
# HostAgentUpgradePending column in Get-OmpDeploymentDrift, and it is worth
# checking first: an agent that is itself out of date explains a whole host's
# worth of pending apps at once.
#
# Workers and worker hosts are listed too (R12-F2/R12-F7), and their RuntimeVersion
# is honest about what can be known. Measured on LINUS-LAPTOP before this was
# added: 16 web-app + 5 service-app instances were visible here, and 2 worker + 1
# worker-host were not -- 3 of 24 desired app instances that no check could see.
#
# RuntimeVersion for a worker is now a real reading, not an inference: WorkerManager
# records the artifact it started each process from, and the worker host build it
# launched it with, in omp.WorkerInstanceRuntimeStates.RuntimeArtifactId /
# RuntimeArtifactVersion / RuntimeHostArtifactId / RuntimeHostArtifactVersion. Where
# that reading is missing -- an older WorkerManager, a database the migration has not
# reached, a process nobody is observing -- RuntimeVersion says 'unknown' and the row
# is ranked Unknown, never InSync. An unverifiable version is not an agreeing one.
#
# Channel-type packages are listed as well (R12-F8). They are not app instances and
# have no runtime state; what a host is asked to run is one enabled row per channel
# in omp.HostArtifactRequirements, and what it has is the HostAgent's provisioning
# report for that artifact. That pair is what the channel-type rows show. A channel
# the module never wrote a requirement row for is invisible to this script -- that
# fact lives in the module's own database, not in omp.

[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string]$HostKey,
    [switch]$PendingOnly,
    [switch]$Json,
    # Same meaning and same default as Get-OmpDeploymentDrift.ps1 -MaxStateAgeSeconds:
    # how old a reported worker state may be and still count as evidence (R12-F3).
    [int]$MaxStateAgeSeconds = 300
)

$ErrorActionPreference = 'Stop'

$conn = New-Object System.Data.SqlClient.SqlConnection
$conn.ConnectionString = "Server=$Server;Database=$Database;Integrated Security=True;Encrypt=False"
$conn.Open()

function Invoke-DetailQuery {
    param($Connection, [string]$Sql)
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql
    $cmd.CommandTimeout = 60
    $table = New-Object System.Data.DataTable
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    [void]$adapter.Fill($table)
    Write-Output $table -NoEnumerate
}

try {
    # The identity-check column only exists on newer schemas. Probing for it
    # keeps the script usable against a host that has not been migrated yet,
    # instead of failing with an unhelpful "invalid column name".
    $probe = $conn.CreateCommand()
    $probe.CommandText = "SELECT CASE WHEN COL_LENGTH('omp.HostAppDeploymentStates','IdentityCheckStatus') IS NULL THEN 0 ELSE 1 END"
    $hasIdentity = [int]$probe.ExecuteScalar() -eq 1
    $probe.CommandText = "SELECT CASE WHEN COL_LENGTH('omp.HostAppDeploymentStates','LastWarning') IS NULL THEN 0 ELSE 1 END"
    $hasLastWarning = [int]$probe.ExecuteScalar() -eq 1
    $probe.CommandText = "SELECT CASE WHEN OBJECT_ID('omp.WorkerInstanceRuntimeStates','U') IS NULL THEN 0 ELSE 1 END"
    $hasWorkerInstanceStates = [int]$probe.ExecuteScalar() -eq 1
    # R12-F2. Probed on the table the worker join will actually read.
    $probe.CommandText = if ($hasWorkerInstanceStates) {
        "SELECT CASE WHEN COL_LENGTH('omp.WorkerInstanceRuntimeStates','RuntimeArtifactId') IS NULL OR COL_LENGTH('omp.WorkerInstanceRuntimeStates','RuntimeHostArtifactId') IS NULL THEN 0 ELSE 1 END"
    } else {
        "SELECT CASE WHEN COL_LENGTH('omp.AppInstanceRuntimeStates','RuntimeArtifactId') IS NULL OR COL_LENGTH('omp.AppInstanceRuntimeStates','RuntimeHostArtifactId') IS NULL THEN 0 ELSE 1 END"
    }
    $hasWorkerVersionColumns = [int]$probe.ExecuteScalar() -eq 1

    $identityWarning = if ($hasIdentity) {
        "state.IdentityCheckStatus IN (N'ManualActionRequired', N'WaitingForPortalAdminApproval')"
    } else { '1 = 0' }
    # R12-F5. The HostAgent records non-blocking deployment warnings here (an OmpAuth
    # configuration set that disagrees across apps is the documented case). The Portal
    # showed them, no script did, so an inconsistent artifact set was invisible to a
    # scripted deploy. Same Warning rank the identity warning already had.
    $lastWarningColumn = if ($hasLastWarning) { 'state.LastWarning' } else { 'CAST(NULL AS nvarchar(4000))' }

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
           $lastWarningColumn AS LastWarning,
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
           CAST(NULL AS nvarchar(150)) AS WorkerInstanceKey,
           DesiredPackageType AS PackageType,
           DesiredVersion,
           ISNULL(RuntimeVersion, N'-') AS RuntimeVersion,
           COALESCE(LastError, LastWarning) AS LastError, LastCheckedUtc,
           CASE
               WHEN DeploymentState = 3 OR LastError IS NOT NULL OR DesiredProvisioningState = 3 THEN 0
               WHEN DeploymentState = 4 OR HasIdentityWarning = 1 OR DesiredProvisioningState = 4
                    OR LastWarning IS NOT NULL THEN 1
               WHEN AppInstanceId IS NULL
                    OR (DesiredPackageType IN (N'web-app', N'service-app') AND RuntimeArtifactId IS NULL)
                    OR DeploymentState = 0
                    OR ISNULL(RuntimeArtifactId, -1) <> ISNULL(DesiredArtifactId, -1) THEN 2
               WHEN DeploymentState = 2 AND LastError IS NULL THEN 3
               ELSE 4
           END AS Rank
    FROM ResolvedApps
)
SELECT HostKey, ModuleInstanceKey, AppKey, AppInstanceKey, WorkerInstanceKey, PackageType,
       DesiredVersion, RuntimeVersion,
       CASE Rank WHEN 0 THEN 'Failed' WHEN 1 THEN 'Warning' WHEN 2 THEN 'Pending'
                 WHEN 3 THEN 'InSync' ELSE 'Unknown' END AS Status,
       LastError, LastCheckedUtc
FROM Classified
$(if ($PendingOnly) { 'WHERE Rank <> 3' })
ORDER BY HostKey, Rank, ModuleInstanceKey, AppKey, AppInstanceKey;
"@

    # Workers and worker hosts. Placement is resolved exactly the way
    # OmpWorkerRuntimeRepository resolves it -- direct HostId pin, active
    # HostDeploymentAssignment for the target host template, or no placement at all
    # (every enabled host) -- and the state comes from the PER-INSTANCE table, not
    # from the app-instance summary that collapses six siblings into one row (R12-D1).
    $workerRuntimeJoin = if ($hasWorkerInstanceStates) {
        'LEFT JOIN omp.WorkerInstanceRuntimeStates wrs ON wrs.WorkerInstanceId = wi.WorkerInstanceId'
    } else {
        'LEFT JOIN omp.AppInstanceRuntimeStates wrs ON wrs.AppInstanceId = wi.AppInstanceId'
    }

    # R12-F2. Literal NULLs on a database without the witness columns, so one query
    # text serves both schema generations and the "unknown" decision lives in one place.
    $workerRuntimeVersionColumns = if ($hasWorkerVersionColumns) {
        'wrs.RuntimeArtifactId, wrs.RuntimeArtifactVersion, wrs.RuntimeHostArtifactId, wrs.RuntimeHostArtifactVersion'
    } else {
        'CAST(NULL AS int) AS RuntimeArtifactId, CAST(NULL AS nvarchar(50)) AS RuntimeArtifactVersion, CAST(NULL AS int) AS RuntimeHostArtifactId, CAST(NULL AS nvarchar(50)) AS RuntimeHostArtifactVersion'
    }

    $workerSql = @"
WITH EnabledHosts AS
(
    SELECT h.HostId, h.HostKey
    FROM omp.Hosts h
    WHERE h.IsEnabled = 1 $hostFilter
),
HostRoles AS
(
    SELECT eh.HostId, eh.HostKey, hda.HostTemplateId
    FROM EnabledHosts eh
    INNER JOIN omp.HostDeploymentAssignments hda
        ON hda.HostId = eh.HostId AND hda.IsActive = 1
),
DesiredWorkerApps AS
(
    SELECT eh.HostId, eh.HostKey, mi.ModuleInstanceKey, app.AppKey,
           ai.AppInstanceId, ai.AppInstanceKey,
           art.ArtifactId AS DesiredArtifactId, art.PackageType, art.Version AS DesiredVersion,
           art.CreatedUtc AS DesiredArtifactCreatedUtc
    FROM omp.AppInstances ai
    INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
    INNER JOIN omp.Apps app ON app.AppId = ai.AppId AND app.IsEnabled = 1
    INNER JOIN omp.Artifacts art
        ON art.ArtifactId = ai.ArtifactId AND art.IsEnabled = 1
       AND art.PackageType IN (N'worker', N'worker-host')
    INNER JOIN EnabledHosts eh ON eh.HostId = ai.HostId
    WHERE ai.IsEnabled = 1 AND ai.IsAllowed = 1 AND ai.DesiredState = 1

    UNION

    SELECT hr.HostId, hr.HostKey, mi.ModuleInstanceKey, app.AppKey,
           ai.AppInstanceId, ai.AppInstanceKey,
           art.ArtifactId, art.PackageType, art.Version, art.CreatedUtc
    FROM omp.AppInstances ai
    INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
    INNER JOIN omp.Apps app ON app.AppId = ai.AppId AND app.IsEnabled = 1
    INNER JOIN omp.Artifacts art
        ON art.ArtifactId = ai.ArtifactId AND art.IsEnabled = 1
       AND art.PackageType IN (N'worker', N'worker-host')
    INNER JOIN HostRoles hr ON hr.HostTemplateId = ai.TargetHostTemplateId
    WHERE ai.HostId IS NULL AND ai.IsEnabled = 1 AND ai.IsAllowed = 1 AND ai.DesiredState = 1

    UNION

    SELECT eh.HostId, eh.HostKey, mi.ModuleInstanceKey, app.AppKey,
           ai.AppInstanceId, ai.AppInstanceKey,
           art.ArtifactId, art.PackageType, art.Version, art.CreatedUtc
    FROM omp.AppInstances ai
    INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
    INNER JOIN omp.Apps app ON app.AppId = ai.AppId AND app.IsEnabled = 1
    INNER JOIN omp.Artifacts art
        ON art.ArtifactId = ai.ArtifactId AND art.IsEnabled = 1
       AND art.PackageType IN (N'worker', N'worker-host')
    CROSS JOIN EnabledHosts eh
    WHERE ai.HostId IS NULL AND ai.TargetHostTemplateId IS NULL
      AND ai.IsEnabled = 1 AND ai.IsAllowed = 1 AND ai.DesiredState = 1
),
DesiredArtifactOnHost AS
(
    SELECT d.HostId, d.DesiredArtifactId,
           has.ProvisioningState, has.LastError AS ArtifactLastError
    FROM (SELECT DISTINCT HostId, DesiredArtifactId FROM DesiredWorkerApps) d
    LEFT JOIN omp.HostArtifactStates has
        ON has.HostId = d.HostId AND has.ArtifactId = d.DesiredArtifactId
),
WorkerInstanceRows AS
(
    SELECT d.HostId, d.HostKey, d.ModuleInstanceKey, d.PackageType, d.AppKey, d.AppInstanceKey,
           wi.WorkerInstanceKey, d.DesiredArtifactId, d.DesiredVersion, d.DesiredArtifactCreatedUtc,
           wrs.ObservedState, wrs.LastSeenUtc, wrs.StartedUtc, wrs.StatusMessage,
           $workerRuntimeVersionColumns
    FROM DesiredWorkerApps d
    INNER JOIN omp.WorkerInstances wi
        ON wi.AppInstanceId = d.AppInstanceId
       AND wi.IsEnabled = 1 AND wi.IsAllowed = 1 AND wi.DesiredState = 1
    $workerRuntimeJoin
    WHERE d.PackageType = N'worker'
),
-- A worker-host package is not a process of its own: it is the executable every
-- worker process on the host runs. Each live worker now reports the worker host
-- build it was launched with (R12-F2), so that is the evidence; the oldest worker
-- start is only the fallback for processes that report nothing.
HostWorkerStarts AS
(
    SELECT HostId,
           MIN(StartedUtc) AS OldestWorkerStartUtc,
           COUNT(1) AS RunningWorkerCount,
           COUNT(DISTINCT RuntimeHostArtifactId) AS DistinctHostArtifactCount,
           SUM(CASE WHEN RuntimeHostArtifactId IS NULL THEN 1 ELSE 0 END) AS UnreportedHostArtifactCount,
           -- Only read when DistinctHostArtifactCount = 1, where MIN is the single value.
           MIN(RuntimeHostArtifactId) AS ReportedHostArtifactId,
           MIN(RuntimeHostArtifactVersion) AS ReportedHostArtifactVersion
    FROM WorkerInstanceRows
    WHERE ObservedState = 2
    GROUP BY HostId
),
WorkerClassified AS
(
    SELECT w.HostKey, w.ModuleInstanceKey, w.AppKey, w.AppInstanceKey, w.WorkerInstanceKey,
           w.PackageType, w.DesiredVersion,
           -- R12-F2. Read from the runtime witness, not inferred from the desired
           -- version. 'unknown' is a real answer and ranks as Unknown below.
           CASE WHEN w.ObservedState = 2 AND w.RuntimeArtifactId IS NOT NULL
                THEN ISNULL(w.RuntimeArtifactVersion, N'?')
                ELSE N'unknown' END AS RuntimeVersion,
           w.LastSeenUtc AS LastCheckedUtc,
           CASE
               WHEN a.ProvisioningState IS NULL
                   THEN N'Desired artifact has no HostAgent provisioning report on this host.'
               WHEN a.ProvisioningState <> 2 OR a.ArtifactLastError IS NOT NULL
                   THEN N'Desired artifact is not provisioned on this host (state '
                        + CAST(CAST(a.ProvisioningState AS int) AS nvarchar(10)) + N').'
               WHEN w.ObservedState IS NULL
                   THEN N'No runtime state has ever been reported for this worker instance.'
               WHEN w.ObservedState <> 2
                   THEN N'Worker is not running (observed state ' + CAST(CAST(w.ObservedState AS int) AS nvarchar(10)) + N').'
               WHEN w.LastSeenUtc IS NULL
                    OR DATEDIFF(second, w.LastSeenUtc, SYSUTCDATETIME()) > $MaxStateAgeSeconds
                   THEN N'Worker state is stale; nothing has refreshed it within $MaxStateAgeSeconds s.'
               WHEN w.RuntimeArtifactId IS NOT NULL AND w.RuntimeArtifactId <> w.DesiredArtifactId
                   THEN N'Worker runs artifact version ' + ISNULL(w.RuntimeArtifactVersion, N'?')
                        + N' but ' + ISNULL(w.DesiredVersion, N'?') + N' is desired.'
               WHEN w.RuntimeArtifactId IS NULL AND w.StartedUtc IS NOT NULL
                    AND w.StartedUtc < w.DesiredArtifactCreatedUtc
                   THEN N'Worker started before its desired artifact existed, so it is running an older build.'
               WHEN w.RuntimeArtifactId IS NULL
                   THEN N'No running artifact version was reported for this worker, so the running build cannot be verified. '
                        + N'The WorkerManager on this host predates the runtime version witness, or the omp_core migration has not been applied.'
               ELSE NULL
           END AS LastError,
           CASE
               WHEN w.ObservedState = 5 THEN 0
               WHEN a.ProvisioningState IS NULL OR a.ProvisioningState <> 2 OR a.ArtifactLastError IS NOT NULL THEN 2
               WHEN w.ObservedState IS NULL OR w.ObservedState <> 2 THEN 2
               WHEN w.LastSeenUtc IS NULL
                    OR DATEDIFF(second, w.LastSeenUtc, SYSUTCDATETIME()) > $MaxStateAgeSeconds THEN 1
               -- A version that disagrees is Pending (the deploy has not landed here yet);
               -- a version that cannot be read at all is Unknown, which is a different
               -- statement and must not be dressed up as either agreement or a rollout.
               WHEN w.RuntimeArtifactId IS NOT NULL AND w.RuntimeArtifactId <> w.DesiredArtifactId THEN 2
               WHEN w.RuntimeArtifactId IS NULL AND w.StartedUtc IS NOT NULL
                    AND w.StartedUtc < w.DesiredArtifactCreatedUtc THEN 2
               WHEN w.RuntimeArtifactId IS NULL THEN 4
               ELSE 3
           END AS Rank
    FROM WorkerInstanceRows w
    INNER JOIN DesiredArtifactOnHost a
        ON a.HostId = w.HostId AND a.DesiredArtifactId = w.DesiredArtifactId

    UNION ALL

    SELECT d.HostKey, d.ModuleInstanceKey, d.AppKey, d.AppInstanceKey, NULL,
           d.PackageType, d.DesiredVersion,
           CASE WHEN d.PackageType = N'worker-host' AND ISNULL(s.RunningWorkerCount, 0) > 0
                     AND s.UnreportedHostArtifactCount = 0 AND s.DistinctHostArtifactCount = 1
                THEN ISNULL(s.ReportedHostArtifactVersion, N'?')
                WHEN a.ProvisioningState = 2 AND ISNULL(s.RunningWorkerCount, 0) = 0
                THEN N'provisioned, not loaded'
                ELSE N'unknown' END AS RuntimeVersion,
           s.OldestWorkerStartUtc AS LastCheckedUtc,
           CASE
               WHEN a.ProvisioningState IS NULL
                   THEN N'Desired artifact has no HostAgent provisioning report on this host.'
               WHEN a.ProvisioningState <> 2 OR a.ArtifactLastError IS NOT NULL
                   THEN N'Desired artifact is not provisioned on this host (state '
                        + CAST(CAST(a.ProvisioningState AS int) AS nvarchar(10)) + N').'
               WHEN ISNULL(s.RunningWorkerCount, 0) = 0
                   THEN NULL
               WHEN d.PackageType = N'worker-host' AND s.UnreportedHostArtifactCount = 0
                    AND s.DistinctHostArtifactCount > 1
                   THEN N'Worker processes on this host are running more than one worker host build.'
               WHEN d.PackageType = N'worker-host' AND s.UnreportedHostArtifactCount = 0
                    AND s.ReportedHostArtifactId <> d.DesiredArtifactId
                   THEN N'Worker processes run worker host build ' + ISNULL(s.ReportedHostArtifactVersion, N'?')
                        + N' but ' + ISNULL(d.DesiredVersion, N'?') + N' is desired.'
               WHEN d.PackageType = N'worker-host' AND s.UnreportedHostArtifactCount = 0
                   THEN NULL
               WHEN s.OldestWorkerStartUtc IS NULL
                   THEN N'Worker start times are unknown, so the running worker host build cannot be established.'
               WHEN s.OldestWorkerStartUtc < d.DesiredArtifactCreatedUtc
                   THEN N'A worker process started before this worker host build existed, so it is running an older one.'
               WHEN d.PackageType = N'worker-host'
                   THEN N'No running worker host build was reported by the worker processes on this host, so it cannot be verified.'
               ELSE NULL
           END AS LastError,
           CASE
               WHEN a.ProvisioningState IS NULL OR a.ProvisioningState <> 2 OR a.ArtifactLastError IS NOT NULL THEN 2
               WHEN ISNULL(s.RunningWorkerCount, 0) = 0 THEN 3
               WHEN d.PackageType = N'worker-host' AND s.UnreportedHostArtifactCount = 0
                    AND s.DistinctHostArtifactCount > 1 THEN 2
               WHEN d.PackageType = N'worker-host' AND s.UnreportedHostArtifactCount = 0
                    AND s.ReportedHostArtifactId <> d.DesiredArtifactId THEN 2
               WHEN d.PackageType = N'worker-host' AND s.UnreportedHostArtifactCount = 0 THEN 3
               WHEN s.OldestWorkerStartUtc IS NULL THEN 4
               WHEN s.OldestWorkerStartUtc < d.DesiredArtifactCreatedUtc THEN 2
               WHEN d.PackageType = N'worker-host' THEN 4
               ELSE 3
           END AS Rank
    FROM DesiredWorkerApps d
    INNER JOIN DesiredArtifactOnHost a
        ON a.HostId = d.HostId AND a.DesiredArtifactId = d.DesiredArtifactId
    LEFT JOIN HostWorkerStarts s ON s.HostId = d.HostId
    WHERE d.PackageType = N'worker-host'
       OR NOT EXISTS (SELECT 1 FROM omp.WorkerInstances wi
                      WHERE wi.AppInstanceId = d.AppInstanceId
                        AND wi.IsEnabled = 1 AND wi.IsAllowed = 1 AND wi.DesiredState = 1)
)
SELECT HostKey, ModuleInstanceKey, AppKey, AppInstanceKey, WorkerInstanceKey, PackageType,
       DesiredVersion, RuntimeVersion,
       CASE Rank WHEN 0 THEN 'Failed' WHEN 1 THEN 'Warning' WHEN 2 THEN 'Pending'
                 WHEN 3 THEN 'InSync' ELSE 'Unknown' END AS Status,
       LastError, LastCheckedUtc
FROM WorkerClassified
$(if ($PendingOnly) { 'WHERE Rank <> 3' })
ORDER BY HostKey, Rank, ModuleInstanceKey, AppKey, WorkerInstanceKey;
"@

    # R12-F8. Host artifact requirements that are not app instances -- in practice the
    # channel-type packages, one enabled row per configured channel. They have no runtime
    # state to read, so RuntimeVersion here means "this exact artifact's content is
    # verified present on the host", which is a weaker claim than a running process and is
    # labelled as such in the output. web-app and service-app requirement rows are
    # excluded because those same deployments are already listed above as app instances,
    # and one deployment listed twice under two different truths is worse than not listing
    # it at all.
    $requirementSql = @"
SELECT h.HostKey,
       CAST(NULL AS nvarchar(100)) AS ModuleInstanceKey,
       a.TargetName AS AppKey,
       CAST(NULL AS nvarchar(100)) AS AppInstanceKey,
       -- The requirement key goes in the instance column because that is the column the
       -- table view prints: seven identical-looking file-drop rows with no discriminator
       -- is a table that hides which channel is which.
       CAST(r.RequirementKey AS nvarchar(150)) AS WorkerInstanceKey,
       a.PackageType,
       a.Version AS DesiredVersion,
       CASE WHEN has.HostId IS NOT NULL AND has.ProvisioningState = 2 AND has.LastError IS NULL
            THEN a.Version ELSE N'unknown' END AS RuntimeVersion,
       CASE
           WHEN has.HostId IS NULL THEN 'Pending'
           WHEN has.LastError IS NOT NULL OR has.ProvisioningState = 3 THEN 'Failed'
           WHEN has.ProvisioningState = 4 THEN 'Warning'
           WHEN has.ProvisioningState = 2 THEN 'InSync'
           ELSE 'Pending'
       END AS Status,
       CASE
           WHEN has.HostId IS NULL THEN N'No HostAgent provisioning report for this required artifact yet.'
           WHEN has.LastError IS NOT NULL THEN has.LastError
           WHEN has.ProvisioningState <> 2 THEN N'Required artifact is not provisioned (state '
                + CAST(CAST(has.ProvisioningState AS int) AS nvarchar(10)) + N').'
           ELSE NULL
       END AS LastError,
       has.LastCheckedUtc
FROM omp.HostArtifactRequirements r
INNER JOIN omp.Hosts h ON h.HostId = r.HostId
INNER JOIN omp.Artifacts a ON a.ArtifactId = r.ArtifactId
LEFT JOIN omp.HostArtifactStates has
    ON has.HostId = r.HostId AND has.ArtifactId = r.ArtifactId
WHERE h.IsEnabled = 1 $hostFilter
  AND r.IsEnabled = 1
  AND a.PackageType NOT IN (N'web-app', N'service-app')
ORDER BY h.HostKey, a.PackageType, a.TargetName, r.RequirementKey;
"@

    $appTable = Invoke-DetailQuery $conn $sql
    $workerTable = Invoke-DetailQuery $conn $workerSql
    $requirementTable = Invoke-DetailQuery $conn $requirementSql

    $selectColumns = 'HostKey', 'ModuleInstanceKey', 'AppKey', 'AppInstanceKey', 'WorkerInstanceKey',
                     'PackageType', 'DesiredVersion', 'RuntimeVersion', 'Status', 'LastError', 'LastCheckedUtc'
    $requirementRows = @($requirementTable | Select-Object $selectColumns)
    if ($PendingOnly) {
        # The requirement query classifies in SQL rather than by rank, so -PendingOnly is
        # applied here instead of in its WHERE clause.
        $requirementRows = @($requirementRows | Where-Object { $_.Status -ne 'InSync' })
    }
    $rows = @($appTable | Select-Object $selectColumns) + @($workerTable | Select-Object $selectColumns) + $requirementRows

    if ($Json) {
        [pscustomobject]@{
            CheckedUtc = (Get-Date).ToUniversalTime().ToString('s') + 'Z'
            MaxStateAgeSeconds = $MaxStateAgeSeconds
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
            # -Width 240: piped into a file or another command the console defaults to
            # 80 columns and folds the version columns into unreadable vertical strips.
            $rows | Format-Table HostKey, ModuleInstanceKey, AppKey, WorkerInstanceKey, PackageType,
                                 DesiredVersion, RuntimeVersion, Status -AutoSize | Out-String -Width 240 | Write-Host
            Write-Host ''
            $rows | Group-Object Status | Sort-Object Name | ForEach-Object {
                Write-Host ("  {0,-8} {1}" -f $_.Name, $_.Count)
            }
            if (@($rows | Where-Object { $_.RuntimeVersion -eq 'unknown' }).Count -gt 0) {
                Write-Host ''
                Write-Host "  RuntimeVersion 'unknown' betyder att den körande versionen inte gick att läsa -- se raden i LastError nedan. Den räknas aldrig som att versionen stämmer." -ForegroundColor Yellow
            }
            if (@($rows | Where-Object { $_.PackageType -eq 'channel-type' }).Count -gt 0) {
                Write-Host ''
                Write-Host "  channel-type-raderna visar vad HostAgenten har provisionerat på värden, inte vad en kanalprocess har laddat. Det är den starkaste utsagan omp kan göra om en kanaltyp." -ForegroundColor Yellow
            }
            # LastError comes from a DataTable, so a SQL NULL arrives as
            # [DBNull]::Value -- which PowerShell treats as TRUE. Testing the
            # value alone printed one blank error line per app on a fully
            # healthy host. Exclude DBNull explicitly.
            foreach ($fel in $rows | Where-Object { $_.LastError -isnot [System.DBNull] -and $_.LastError }) {
                Write-Host ''
                $felInstans = if ($fel.WorkerInstanceKey -is [System.DBNull] -or -not $fel.WorkerInstanceKey) { $fel.AppInstanceKey } else { $fel.WorkerInstanceKey }
                Write-Host ("  {0} / {1}: {2}" -f $fel.AppKey, $felInstans, $fel.LastError) -ForegroundColor Yellow
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
