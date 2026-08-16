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
# Converged means, for every enabled host that matched:
#   * every desired app instance is classified InSync -- DesiredApps == InSync,
#     not "no app landed in a problem bucket" (R12-F6/INV3),
#   * the HostAgent runs its desired version, has a desired version at all, and
#     has reported within -MaxStateAgeSeconds (R12-F3),
#   * app deployment state was last checked within -MaxStateAgeSeconds (R12-F3),
#   * no artifact requirement is unprovisioned, failed or unreported, and no host
#     is required to run two different builds of the same package at once (R12-F8),
#   * every desired worker and worker-host package is provisioned on the host and
#     every worker instance REPORTS running the desired artifact, with a fresh
#     heartbeat (R12-F2, R12-F7, R12-D1).
#
# How the running worker version is established (R12-F2). WorkerManager records
# the artifact it actually started each worker process from, and the worker host
# build it launched it with, in omp.WorkerInstanceRuntimeStates.RuntimeArtifactId /
# RuntimeArtifactVersion / RuntimeHostArtifactId / RuntimeHostArtifactVersion. The
# columns are written only while a process is alive and are cleared when a state
# goes stale, so a version in them always belongs to a process someone is currently
# observing. This script compares them against the desired artifact.
#
# Where that evidence is missing -- a WorkerManager older than this witness, a
# database the migration has not reached, a manually created runtime app instance --
# the row is reported as UNVERIFIABLE and counts against convergence. It is not
# treated as healthy: a gate that cannot see a version must not certify one. The
# older ordering heuristic (a process that started before its desired artifact
# existed cannot be running it) is kept as the fallback for exactly those rows.
[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string]$HostKey,
    [switch]$Json,
    [switch]$Wait,
    [int]$TimeoutSeconds = 300,
    [int]$PollSeconds = 10,
    # R12-F3. How old a reported state may be and still count as evidence. The
    # HostAgent cycle is ~30 s and the WorkerManager refresh is 15 s deployed, so
    # 300 s is roughly ten missed cycles: long enough never to trip on a slow
    # cycle, short enough that a dead agent cannot pass a deploy gate. This is the
    # one knob a site may genuinely need to move (a host polling on a long
    # interval), and it lives here only -- both the HostAgent check and the worker
    # check read this same value.
    [int]$MaxStateAgeSeconds = 300
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

# A SQL NULL arrives from a DataTable as [DBNull]::Value, which PowerShell treats
# as truthy and [int] casts to 0. Both are wrong for an age: "never reported" must
# read as infinitely stale, not as zero seconds old.
function Get-NullableInt {
    param($Value)
    if ($null -eq $Value -or $Value -is [System.DBNull]) {
        return $null
    }
    return [int]$Value
}

function Test-StateAgeIsFresh {
    param($AgeSeconds, [int]$MaxAgeSeconds)
    $age = Get-NullableInt $AgeSeconds
    if ($null -eq $age) {
        return $false
    }
    return $age -le $MaxAgeSeconds
}

function Format-AgeSeconds {
    param($AgeSeconds)
    $age = Get-NullableInt $AgeSeconds
    if ($null -eq $age) {
        return 'never'
    }
    return "$age s ago"
}

# The identity-check columns are newer than some databases; mirror the Portal's
# dynamic column probe so the summary works on both schema generations.
$identityProbeSql = "SELECT CASE WHEN COL_LENGTH('omp.HostAppDeploymentStates','IdentityCheckStatus') IS NULL THEN 0 ELSE 1 END"
$lastWarningProbeSql = "SELECT CASE WHEN COL_LENGTH('omp.HostAppDeploymentStates','LastWarning') IS NULL THEN 0 ELSE 1 END"
$workerStateProbeSql = "SELECT CASE WHEN OBJECT_ID('omp.WorkerInstanceRuntimeStates','U') IS NULL THEN 0 ELSE 1 END"
# R12-F2. Probed on whichever table the worker join actually reads, so the answer
# describes the columns this run will select and not a table it will not touch.
$workerVersionProbeSqlPerInstance = "SELECT CASE WHEN COL_LENGTH('omp.WorkerInstanceRuntimeStates','RuntimeArtifactId') IS NULL OR COL_LENGTH('omp.WorkerInstanceRuntimeStates','RuntimeHostArtifactId') IS NULL THEN 0 ELSE 1 END"
$workerVersionProbeSqlSummary = "SELECT CASE WHEN COL_LENGTH('omp.AppInstanceRuntimeStates','RuntimeArtifactId') IS NULL OR COL_LENGTH('omp.AppInstanceRuntimeStates','RuntimeHostArtifactId') IS NULL THEN 0 ELSE 1 END"

function Get-DriftSnapshot {
    $conn = Open-Connection
    try {
        $probe = $conn.CreateCommand()
        $probe.CommandText = $identityProbeSql
        $hasIdentity = [int]$probe.ExecuteScalar() -eq 1
        $probe.CommandText = $lastWarningProbeSql
        $hasLastWarning = [int]$probe.ExecuteScalar() -eq 1
        $probe.CommandText = $workerStateProbeSql
        $hasWorkerInstanceStates = [int]$probe.ExecuteScalar() -eq 1
        $probe.CommandText = if ($hasWorkerInstanceStates) { $workerVersionProbeSqlPerInstance } else { $workerVersionProbeSqlSummary }
        $hasWorkerVersionColumns = [int]$probe.ExecuteScalar() -eq 1

        $identityWarning = if ($hasIdentity) { "state.IdentityCheckStatus IN (N'ManualActionRequired', N'WaitingForPortalAdminApproval')" } else { '1 = 0' }
        # R12-F5. The HostAgent writes non-blocking deployment warnings (an OmpAuth
        # configuration set that disagrees across apps is the documented case) to
        # LastWarning. The Portal shows them; no script read them, so an inconsistent
        # artifact set was invisible to a scripted deploy and to the gate it exits on.
        # It joins the Warning bucket, which the identity warning already used -- a
        # warning that cannot fail the gate is a warning nobody acts on.
        $lastWarningColumn = if ($hasLastWarning) { 'state.LastWarning' } else { 'CAST(NULL AS nvarchar(4000))' }
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
           $lastWarningColumn AS LastWarning,
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
-- R12-F6/INV3. This used to be four independent SUM(CASE) buckets, which neither
-- partitioned nor covered the desired set: DeploymentState = 1 (Deploying) with a
-- matching artifact fell into no bucket at all and was counted as healthy, and
-- $converged was derived from the ABSENCE of problems instead of from the presence
-- of agreement. Verified against the live database by running the old CTE verbatim
-- (21/21) and again with DeploymentState injected as 1 for one app: DesiredApps 21,
-- InSync 20, Pending 0, Failed 0, Warnings 0 -- and "Converged: True".
-- One CASE now assigns exactly one rank to every row, the ELSE catches whatever the
-- named ranks do not, and convergence is DesiredApps == InSync (see below). The
-- ladder is identical to Get-OmpAppDeploymentDetail.ps1's Classified CTE by design:
-- the two scripts must classify the same row the same way.
Classified AS
(
    SELECT HostId, LastCheckedUtc, LastAppliedUtc,
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
),
Aggregated AS
(
    SELECT HostId,
           COUNT(1) AS DesiredAppCount,
           SUM(CASE WHEN Rank = 3 THEN 1 ELSE 0 END) AS InSyncAppCount,
           SUM(CASE WHEN Rank = 2 THEN 1 ELSE 0 END) AS PendingAppCount,
           SUM(CASE WHEN Rank = 0 THEN 1 ELSE 0 END) AS FailedAppCount,
           SUM(CASE WHEN Rank = 1 THEN 1 ELSE 0 END) AS WarningAppCount,
           SUM(CASE WHEN Rank = 4 THEN 1 ELSE 0 END) AS UnclassifiedAppCount,
           MAX(LastCheckedUtc) AS LastCheckedUtc,
           -- The OLDEST check is the one that decides freshness. MAX would let a
           -- single app the agent still visits vouch for every app it has stopped
           -- visiting -- the same masking that made a live channel hide dead ones.
           MIN(LastCheckedUtc) AS OldestLastCheckedUtc,
           MAX(LastAppliedUtc) AS LastAppliedUtc
    FROM Classified
    GROUP BY HostId
)
SELECT h.HostKey,
       ISNULL(aggregated.DesiredAppCount, 0) AS DesiredApps,
       ISNULL(aggregated.InSyncAppCount, 0) AS InSync,
       ISNULL(aggregated.PendingAppCount, 0) AS Pending,
       ISNULL(aggregated.FailedAppCount, 0) AS Failed,
       ISNULL(aggregated.WarningAppCount, 0) AS Warnings,
       ISNULL(aggregated.UnclassifiedAppCount, 0) AS Unclassified,
       aggregated.LastCheckedUtc,
       DATEDIFF(second, aggregated.OldestLastCheckedUtc, SYSUTCDATETIME()) AS OldestAppStateAgeSeconds,
       desiredArtifact.Version AS HostAgentDesired,
       runtimeState.Version AS HostAgentCurrent,
       runtimeState.LastSeenUtc AS HostAgentLastSeenUtc,
       DATEDIFF(second, runtimeState.LastSeenUtc, SYSUTCDATETIME()) AS HostAgentAgeSeconds,
       CAST(CASE WHEN desiredArtifact.Version IS NOT NULL
                      AND ISNULL(runtimeState.Version, N'') <> desiredArtifact.Version THEN 1 ELSE 0 END AS bit) AS HostAgentUpgradePending,
       -- R12-F3. HostAgentUpgradePending is 0 when there is no desired HostAgent
       -- artifact at all, so a host nobody had assigned an agent version to passed
       -- the gate as healthy. "No desired version" is not agreement, it is an
       -- unanswered question, and it gets its own column so the note can say so.
       CAST(CASE WHEN desiredArtifact.Version IS NULL THEN 1 ELSE 0 END AS bit) AS HostAgentDesiredMissing
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

        # Artifact provisioning state for every ENABLED REQUIREMENT ROW on each host.
        #
        # R12-F8: the comment here used to claim this covered worker and channel-type
        # packages. Measured, it does not: omp.HostArtifactRequirements on LINUS-LAPTOP
        # holds 7 channel-type rows and 1 service-app row and NO worker or worker-host
        # row at all, because a worker's artifact is provisioned on demand through the
        # HostAgent EnsureArtifact RPC rather than declared as a requirement. So this
        # query covers exactly what a module declared as a host requirement -- mostly
        # channel-type packages -- and nothing else. Worker and worker-host packages are
        # covered by the worker query below instead, which is where their truth lives.
        #
        # What "covered" means for a channel type, stated because the finding asked and
        # the answer is not obvious: which channel-type BUILD a host is supposed to run
        # is decided by the module's own channel configuration, and the module expresses
        # that decision by writing one enabled requirement row per channel (measured:
        # 6 rows keyed ibs_packager.channeltype:<ChannelId> plus one legacy
        # channel-type:file-drop:<version> row, all 7 pointing at 0.3.109, with 20
        # superseded rows disabled). This query therefore sees exactly what the module
        # declared. What it still cannot see is a channel the module never declared a
        # requirement for at all -- that lives in the module's own database, not in omp,
        # and nothing here can observe it.
        #
        # Driven FROM the requirements, not from the states. The query used to start at
        # omp.HostArtifactStates, which only has a row once the HostAgent has reported on
        # the artifact -- so a requirement the agent had not yet acknowledged produced no
        # row, no issue, and a clean "Converged" in exactly the window a deployment gate
        # exists to catch (R7-G12). A missing state is now the strongest signal there is,
        # not the absence of one.
        $artifactSql = @"
SELECT h.HostKey, a.PackageType, a.TargetName, a.Version,
       CAST(has.ProvisioningState AS int) AS ProvisioningState,
       CASE WHEN has.HostId IS NULL
            THEN N'No HostAgent report for this required artifact yet.'
            ELSE has.LastError END AS LastError,
       has.UpdatedUtc
FROM omp.HostArtifactRequirements r
INNER JOIN omp.Hosts h ON h.HostId = r.HostId
INNER JOIN omp.Artifacts a ON a.ArtifactId = r.ArtifactId
LEFT JOIN omp.HostArtifactStates has
    ON has.HostId = r.HostId
   AND has.ArtifactId = r.ArtifactId
WHERE h.IsEnabled = 1 $hostFilter
  AND r.IsEnabled = 1
  AND (has.HostId IS NULL OR has.ProvisioningState NOT IN (2) OR has.LastError IS NOT NULL)
ORDER BY h.HostKey, a.PackageType, a.TargetName, a.Version;
"@

        # R12-F8. Coverage the query above cannot express, because it only returns rows
        # that are already in trouble: a host required to run TWO different builds of the
        # same package target at the same time is silent there -- both rows can be
        # provisioned and error-free. That is exactly what a half-applied channel rollout
        # looks like (the module updated five channels' requirement rows and not the
        # sixth), and it is the one channel-type version question omp can answer on its
        # own. Every package type is included rather than only channel-type: the same
        # split is possible wherever requirement rows are written per instance.
        #
        # It also doubles as the coverage report. The counts are emitted whether or not
        # anything is wrong, so a reader can see WHICH package types this gate actually
        # checked instead of inferring coverage from silence.
        $requirementCoverageSql = @"
SELECT h.HostKey, a.PackageType, a.TargetName,
       COUNT(1) AS RequiredRows,
       COUNT(DISTINCT a.ArtifactId) AS DistinctArtifacts,
       MIN(a.Version) AS SampleVersion,
       SUM(CASE WHEN has.HostId IS NOT NULL AND has.ProvisioningState = 2 AND has.LastError IS NULL THEN 1 ELSE 0 END) AS ProvisionedRows
FROM omp.HostArtifactRequirements r
INNER JOIN omp.Hosts h ON h.HostId = r.HostId
INNER JOIN omp.Artifacts a ON a.ArtifactId = r.ArtifactId
LEFT JOIN omp.HostArtifactStates has
    ON has.HostId = r.HostId
   AND has.ArtifactId = r.ArtifactId
WHERE h.IsEnabled = 1 $hostFilter
  AND r.IsEnabled = 1
GROUP BY h.HostKey, a.PackageType, a.TargetName
ORDER BY h.HostKey, a.PackageType, a.TargetName;
"@

        # R12-D1. Per-instance state, not the app-instance summary. PublishObservationAsync
        # writes omp.AppInstanceRuntimeStates keyed on AppInstanceId alone while the
        # per-instance truth goes to omp.WorkerInstanceRuntimeStates -- measured on
        # LINUS-LAPTOP: 7 rows in the per-instance table against 2 in the summary one, for
        # 6 worker instances under ibs_packager_worker. Reading the summary meant one
        # worker stuck in Failed(5) was invisible for as long as any sibling reported
        # Running. The summary is now a real aggregation (worst state wins) as well, but
        # the gate reads the per-instance rows because that is where the siblings are.
        $workerRuntimeJoin = if ($hasWorkerInstanceStates) {
            'LEFT JOIN omp.WorkerInstanceRuntimeStates wrs ON wrs.WorkerInstanceId = wi.WorkerInstanceId'
        } else {
            'LEFT JOIN omp.AppInstanceRuntimeStates wrs ON wrs.AppInstanceId = wi.AppInstanceId'
        }

        # R12-F2. Selected as literal NULLs on a database that predates the witness, so the
        # rest of the query is written once and the "cannot be verified" branch below is the
        # single place that decides what a missing version means.
        $workerRuntimeVersionColumns = if ($hasWorkerVersionColumns) {
            'wrs.RuntimeArtifactId, wrs.RuntimeArtifactVersion, wrs.RuntimeHostArtifactId, wrs.RuntimeHostArtifactVersion'
        } else {
            'CAST(NULL AS int) AS RuntimeArtifactId, CAST(NULL AS nvarchar(50)) AS RuntimeArtifactVersion, CAST(NULL AS int) AS RuntimeHostArtifactId, CAST(NULL AS nvarchar(50)) AS RuntimeHostArtifactVersion'
        }

        # R12-F2 + R12-F7 + R12-F11/D10/D11. The old worker query INNER JOINed
        # omp.AppWorkerDefinitions, compared no artifact and no version, ignored -HostKey
        # entirely and never checked h.IsEnabled -- and omp_workerprocesshost (worker-host)
        # has no AppWorkerDefinitions row, so it was covered by nothing at all. Measured
        # before the change: 16 web-app + 5 service-app instances were visible to the
        # scripts, 2 worker + 1 worker-host were not -- 3 of 24 desired app instances that
        # no check could see, one of them the IbsPackager worker.
        #
        # Placement is resolved the same way OmpWorkerRuntimeRepository resolves it:
        # a direct HostId pin, an active HostDeploymentAssignment for the target host
        # template, or no placement at all (which means every enabled host).
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
    SELECT eh.HostId, eh.HostKey, app.AppKey, ai.AppInstanceId, ai.AppInstanceKey,
           art.ArtifactId AS DesiredArtifactId, art.PackageType, art.Version AS DesiredVersion,
           art.CreatedUtc AS DesiredArtifactCreatedUtc
    FROM omp.AppInstances ai
    INNER JOIN omp.Apps app ON app.AppId = ai.AppId AND app.IsEnabled = 1
    INNER JOIN omp.Artifacts art
        ON art.ArtifactId = ai.ArtifactId AND art.IsEnabled = 1
       AND art.PackageType IN (N'worker', N'worker-host')
    INNER JOIN EnabledHosts eh ON eh.HostId = ai.HostId
    WHERE ai.IsEnabled = 1 AND ai.IsAllowed = 1 AND ai.DesiredState = 1

    UNION

    SELECT hr.HostId, hr.HostKey, app.AppKey, ai.AppInstanceId, ai.AppInstanceKey,
           art.ArtifactId, art.PackageType, art.Version, art.CreatedUtc
    FROM omp.AppInstances ai
    INNER JOIN omp.Apps app ON app.AppId = ai.AppId AND app.IsEnabled = 1
    INNER JOIN omp.Artifacts art
        ON art.ArtifactId = ai.ArtifactId AND art.IsEnabled = 1
       AND art.PackageType IN (N'worker', N'worker-host')
    INNER JOIN HostRoles hr ON hr.HostTemplateId = ai.TargetHostTemplateId
    WHERE ai.HostId IS NULL AND ai.IsEnabled = 1 AND ai.IsAllowed = 1 AND ai.DesiredState = 1

    UNION

    SELECT eh.HostId, eh.HostKey, app.AppKey, ai.AppInstanceId, ai.AppInstanceKey,
           art.ArtifactId, art.PackageType, art.Version, art.CreatedUtc
    FROM omp.AppInstances ai
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
    SELECT d.HostId, d.HostKey, d.PackageType, d.AppKey, d.AppInstanceKey,
           wi.WorkerInstanceKey, d.DesiredArtifactId, d.DesiredVersion, d.DesiredArtifactCreatedUtc,
           wrs.ObservedState, wrs.LastSeenUtc, wrs.StartedUtc, wrs.LastExitCode, wrs.StatusMessage,
           $workerRuntimeVersionColumns
    FROM DesiredWorkerApps d
    INNER JOIN omp.WorkerInstances wi
        ON wi.AppInstanceId = d.AppInstanceId
       AND wi.IsEnabled = 1 AND wi.IsAllowed = 1 AND wi.DesiredState = 1
    $workerRuntimeJoin
    WHERE d.PackageType = N'worker'
),
-- A worker-host package is not a process of its own: it is the executable every
-- worker process on the host runs. R12-F2 gave it a real witness -- each running
-- worker reports the worker host build it was launched with -- so the evidence is
-- now what those processes report, and the oldest worker start is only the fallback
-- for processes that report nothing.
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
AllRows AS
(
    SELECT w.HostKey, w.PackageType, w.AppKey, w.AppInstanceKey, w.WorkerInstanceKey,
           w.DesiredVersion,
           -- R12-F2. What the process ITSELF is reported to be running, or the stated
           -- unknown. Never the desired version by default: that was the whole defect.
           CASE WHEN w.ObservedState = 2 AND w.RuntimeArtifactId IS NOT NULL
                THEN ISNULL(w.RuntimeArtifactVersion, N'?')
                ELSE N'unknown' END AS RuntimeVersion,
           CAST(a.ProvisioningState AS int) AS ProvisioningState,
           CAST(w.ObservedState AS int) AS ObservedState,
           DATEDIFF(second, w.LastSeenUtc, SYSUTCDATETIME()) AS StateAgeSeconds,
           w.StartedUtc, w.LastExitCode, w.StatusMessage,
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
               -- R12-F2, the answer this gate previously could not give at all.
               WHEN w.RuntimeArtifactId IS NOT NULL AND w.RuntimeArtifactId <> w.DesiredArtifactId
                   THEN N'Worker runs artifact version ' + ISNULL(w.RuntimeArtifactVersion, N'?')
                        + N' but ' + ISNULL(w.DesiredVersion, N'?') + N' is desired.'
               -- No witness: fall back to the ordering evidence, which can still convict
               -- but can never acquit, and say so when it does not convict.
               WHEN w.RuntimeArtifactId IS NULL AND w.StartedUtc IS NOT NULL
                    AND w.StartedUtc < w.DesiredArtifactCreatedUtc
                   THEN N'Worker started before its desired artifact existed, so it is running an older build.'
               WHEN w.RuntimeArtifactId IS NULL
                   THEN N'No running artifact version was reported for this worker, so the running build cannot be verified. '
                        + N'The WorkerManager on this host predates the runtime version witness, or the omp_core migration has not been applied.'
               ELSE NULL
           END AS Issue
    FROM WorkerInstanceRows w
    INNER JOIN DesiredArtifactOnHost a
        ON a.HostId = w.HostId AND a.DesiredArtifactId = w.DesiredArtifactId

    UNION ALL

    SELECT d.HostKey, d.PackageType, d.AppKey, d.AppInstanceKey, NULL,
           d.DesiredVersion,
           -- R12-F2. For a worker-host row the running build is what the live worker
           -- processes report they were launched with, and only when they all agree.
           CASE WHEN d.PackageType = N'worker-host' AND ISNULL(s.RunningWorkerCount, 0) > 0
                     AND s.UnreportedHostArtifactCount = 0 AND s.DistinctHostArtifactCount = 1
                THEN ISNULL(s.ReportedHostArtifactVersion, N'?')
                WHEN d.PackageType = N'worker-host' AND a.ProvisioningState = 2
                     AND ISNULL(s.RunningWorkerCount, 0) = 0
                THEN N'provisioned, not loaded'
                ELSE N'unknown' END AS RuntimeVersion,
           CAST(a.ProvisioningState AS int),
           NULL,
           NULL,
           s.OldestWorkerStartUtc,
           NULL,
           CASE WHEN ISNULL(s.RunningWorkerCount, 0) = 0
                THEN N'no worker process running on this host'
                ELSE CAST(s.RunningWorkerCount AS nvarchar(10)) + N' worker process(es) on this host' END,
           CASE
               WHEN a.ProvisioningState IS NULL
                   THEN N'Desired artifact has no HostAgent provisioning report on this host.'
               WHEN a.ProvisioningState <> 2 OR a.ArtifactLastError IS NOT NULL
                   THEN N'Desired artifact is not provisioned on this host (state '
                        + CAST(CAST(a.ProvisioningState AS int) AS nvarchar(10)) + N').'
               -- A worker host build nothing loads is provisioned and idle, not wrong.
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
               -- Fallback for processes that report no worker host build at all.
               WHEN s.OldestWorkerStartUtc IS NULL
                   THEN N'Worker start times are unknown, so the running worker host build cannot be established.'
               WHEN s.OldestWorkerStartUtc < d.DesiredArtifactCreatedUtc
                   THEN N'A worker process started before this worker host build existed, so it is running an older one.'
               WHEN d.PackageType = N'worker-host'
                   THEN N'No running worker host build was reported by the worker processes on this host, so it cannot be verified. '
                        + N'The WorkerManager on this host predates the runtime version witness, or the omp_core migration has not been applied.'
               ELSE NULL
           END AS Issue
    FROM DesiredWorkerApps d
    INNER JOIN DesiredArtifactOnHost a
        ON a.HostId = d.HostId AND a.DesiredArtifactId = d.DesiredArtifactId
    LEFT JOIN HostWorkerStarts s ON s.HostId = d.HostId
    WHERE d.PackageType = N'worker-host'
       OR NOT EXISTS (SELECT 1 FROM omp.WorkerInstances wi
                      WHERE wi.AppInstanceId = d.AppInstanceId
                        AND wi.IsEnabled = 1 AND wi.IsAllowed = 1 AND wi.DesiredState = 1)
)
SELECT HostKey, PackageType, AppKey, AppInstanceKey, WorkerInstanceKey, DesiredVersion,
       RuntimeVersion, ProvisioningState, ObservedState, StateAgeSeconds, StartedUtc,
       LastExitCode, StatusMessage, Issue
FROM AllRows
ORDER BY CASE WHEN Issue IS NULL THEN 1 ELSE 0 END, HostKey, PackageType, AppKey, WorkerInstanceKey;
"@

        $summary = Invoke-Query $conn $summarySql
        $artifacts = Invoke-Query $conn $artifactSql
        $workers = Invoke-Query $conn $workerSql
        $requirementCoverage = Invoke-Query $conn $requirementCoverageSql

        # Converged is a claim about hosts that were examined. Zero rows means no enabled
        # host matched -- a wrong -HostKey, a disabled host, an empty database -- and
        # answering "Converged" to that is answering a question nobody asked. The old code
        # started at $true and only ever cleared it, so checking nothing passed (R7-G11).
        $converged = $summary.Rows.Count -gt 0
        $notes = @()
        if ($summary.Rows.Count -eq 0) {
            $notes += 'No enabled host matched the query; nothing was checked, so convergence is unknown.'
        }
        if (-not $hasWorkerInstanceStates) {
            $notes += 'This database predates omp.WorkerInstanceRuntimeStates; worker siblings share one state row, so a single failed worker can be masked by a healthy one.'
        }
        if (-not $hasWorkerVersionColumns) {
            # Said once here as the cause, and again per worker row as the consequence.
            # The per-row issues are what fail the gate; this note is what explains them.
            $notes += 'This database has no RuntimeArtifactId/RuntimeArtifactVersion columns on the worker runtime state, so which build each worker actually runs cannot be read at all. Apply the omp_core schema migration (R12-F2).'
        }

        foreach ($row in $summary.Rows) {
            $rowHostKey = [string]$row.HostKey
            $desiredApps = [int]$row.DesiredApps
            $inSyncApps = [int]$row.InSync

            # R12-F6/INV3. The whole gate hangs on this one comparison. Every desired app
            # must have reached InSync; anything else -- a named problem bucket or a state
            # no bucket names -- is a difference, and a difference is not convergence.
            if ($desiredApps -ne $inSyncApps) {
                $converged = $false
                $notes += ("{0}: {1} of {2} desired apps are in sync (pending {3}, failed {4}, warnings {5}, unclassified {6})." -f `
                    $rowHostKey, $inSyncApps, $desiredApps, [int]$row.Pending, [int]$row.Failed, [int]$row.Warnings, [int]$row.Unclassified)
            }

            if ([bool]$row.HostAgentUpgradePending) {
                $converged = $false
                $notes += ("{0}: HostAgent runs {1} but {2} is desired." -f $rowHostKey, $row.HostAgentCurrent, $row.HostAgentDesired)
            }

            if ([bool]$row.HostAgentDesiredMissing) {
                $converged = $false
                $notes += ("{0}: no desired HostAgent artifact is assigned, so the agent version cannot be verified." -f $rowHostKey)
            }

            # R12-F3. Without this the gate answered "converged" the instant a dead
            # HostAgent stopped importing: nothing changed, every app still matched the
            # OLD desired artifact, and -Wait returned 0 immediately while the package sat
            # untouched in ArtifactImports.
            if (-not (Test-StateAgeIsFresh $row.HostAgentAgeSeconds $MaxStateAgeSeconds)) {
                $converged = $false
                $notes += ("{0}: HostAgent last reported {1}, older than the {2} s freshness limit -- the deployment state below may describe a host that stopped working." -f `
                    $rowHostKey, (Format-AgeSeconds $row.HostAgentAgeSeconds), $MaxStateAgeSeconds)
            }

            if ($desiredApps -gt 0 -and -not (Test-StateAgeIsFresh $row.OldestAppStateAgeSeconds $MaxStateAgeSeconds)) {
                $converged = $false
                $notes += ("{0}: the oldest app deployment state was checked {1}, older than the {2} s freshness limit." -f `
                    $rowHostKey, (Format-AgeSeconds $row.OldestAppStateAgeSeconds), $MaxStateAgeSeconds)
            }
        }

        $workerIssueRows = @($workers | Where-Object { $_.Issue -isnot [System.DBNull] -and $_.Issue })

        # R12-F8. A target required at two different builds on the same host is a
        # half-applied rollout, and both rows can be perfectly provisioned -- so this is
        # the only place it can fail the gate.
        $requirementSplitRows = @($requirementCoverage | Where-Object { [int]$_.DistinctArtifacts -gt 1 })
        foreach ($split in $requirementSplitRows) {
            $notes += ("{0}: {1}/{2} is required at {3} different versions by {4} enabled requirement rows; a rollout updated some rows and not others." -f `
                $split.HostKey, $split.PackageType, $split.TargetName, [int]$split.DistinctArtifacts, [int]$split.RequiredRows)
        }

        if ($artifacts.Rows.Count -gt 0 -or $workerIssueRows.Count -gt 0 -or $requirementSplitRows.Count -gt 0) {
            $converged = $false
        }
        foreach ($workerIssue in $workerIssueRows) {
            $notes += ("{0}: {1} {2}/{3} -- {4}" -f `
                $workerIssue.HostKey, $workerIssue.PackageType, $workerIssue.AppKey,
                $(if ($workerIssue.WorkerInstanceKey -is [System.DBNull]) { '(app instance)' } else { $workerIssue.WorkerInstanceKey }),
                $workerIssue.Issue)
        }

        return [pscustomobject]@{
            CheckedUtc = (Get-Date).ToUniversalTime().ToString('s') + 'Z'
            Converged  = $converged
            MaxStateAgeSeconds = $MaxStateAgeSeconds
            Notes      = $notes
            Hosts      = @($summary | Select-Object HostKey, DesiredApps, InSync, Pending, Failed, Warnings, Unclassified, HostAgentDesired, HostAgentCurrent, HostAgentUpgradePending, HostAgentDesiredMissing, HostAgentLastSeenUtc, HostAgentAgeSeconds, OldestAppStateAgeSeconds)
            ArtifactIssues = @($artifacts | Select-Object HostKey, PackageType, TargetName, Version, ProvisioningState, LastError, UpdatedUtc)
            # R12-F8. Emitted whether or not anything is wrong, so the reader can see
            # which package types were actually checked instead of inferring it from an
            # empty issue list. This is where channel-type coverage becomes visible.
            RequiredArtifacts = @($requirementCoverage | Select-Object HostKey, PackageType, TargetName, RequiredRows, DistinctArtifacts, SampleVersion, ProvisionedRows)
            # Every desired worker and worker-host row, healthy ones included: a
            # component nobody can see must not be able to pass for healthy by being
            # absent from the output (R12-F2).
            Workers    = @($workers | Select-Object HostKey, PackageType, AppKey, AppInstanceKey, WorkerInstanceKey, DesiredVersion, RuntimeVersion, ProvisioningState, ObservedState, StateAgeSeconds, StartedUtc, LastExitCode, StatusMessage, Issue)
            WorkerIssues = @($workerIssueRows | Select-Object HostKey, PackageType, AppKey, AppInstanceKey, WorkerInstanceKey, DesiredVersion, RuntimeVersion, ProvisioningState, ObservedState, StateAgeSeconds, StatusMessage, Issue)
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

    Write-Host ("Converged: {0}   ({1}, state freshness limit {2} s)" -f $Snapshot.Converged, $Snapshot.CheckedUtc, $Snapshot.MaxStateAgeSeconds)
    foreach ($note in $Snapshot.Notes) {
        Write-Warning $note
    }
    Write-Host ''
    $Snapshot.Hosts | Format-Table HostKey, DesiredApps, InSync, Pending, Failed, Warnings, Unclassified, HostAgentDesired, HostAgentCurrent, HostAgentUpgradePending, HostAgentAgeSeconds, OldestAppStateAgeSeconds -AutoSize | Out-String -Width 240 | Write-Host
    if ($Snapshot.ArtifactIssues.Count -gt 0) {
        Write-Host 'Artifact provisioning issues (state<>2 or error):'
        $Snapshot.ArtifactIssues | Format-Table HostKey, PackageType, TargetName, Version, ProvisioningState, LastError -AutoSize | Out-String -Width 240 | Write-Host
    }
    if ($Snapshot.RequiredArtifacts.Count -gt 0) {
        Write-Host 'Host artifact requirements (what this gate checks per package type):'
        $Snapshot.RequiredArtifacts | Format-Table HostKey, PackageType, TargetName, RequiredRows, DistinctArtifacts, SampleVersion, ProvisionedRows -AutoSize | Out-String -Width 240 | Write-Host
    }
    if ($Snapshot.Workers.Count -gt 0) {
        Write-Host 'Workers and worker hosts (desired version vs reported running build):'
        $Snapshot.Workers | Format-Table HostKey, PackageType, AppKey, WorkerInstanceKey, DesiredVersion, RuntimeVersion, ObservedState, StateAgeSeconds, StartedUtc, Issue -AutoSize | Out-String -Width 240 | Write-Host
        if (@($Snapshot.Workers | Where-Object { $_.RuntimeVersion -eq 'unknown' }).Count -gt 0) {
            Write-Host "  RuntimeVersion 'unknown' means the running build could not be read -- see the Issue column. It is never counted as agreement." -ForegroundColor Yellow
        }
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
        $outstanding = 0
        foreach ($hostRow in $snapshot.Hosts) {
            $outstanding += ([int]$hostRow.DesiredApps - [int]$hostRow.InSync)
        }
        $artifactCount = $snapshot.ArtifactIssues.Count
        $workerCount = $snapshot.WorkerIssues.Count
        Write-Host ("Waiting... apps not in sync: {0}, artifact issues: {1}, worker issues: {2}" -f $outstanding, $artifactCount, $workerCount)
    }
    Start-Sleep -Seconds $PollSeconds
}
