#Requires -Version 5.1
<#
.SYNOPSIS
Runs administrator-side diagnostics for an OMP HostAgent Windows service.

.DESCRIPTION
This script is intended to be run from an elevated PowerShell session on the
target host. It inspects the Windows service, HostAgent configuration, log
folder, IIS state, event logs, and OMP database metadata. It does not mutate
IIS, Windows services, or SQL data.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'OMP.HostAgent',
    [string]$AppPath = '',
    [string]$EnvironmentName = 'Production',
    [int]$RecentHours = 24,
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'

$startedAt = Get-Date
$checks = New-Object System.Collections.ArrayList

function Add-Check {
    param(
        [string]$Area,
        [string]$Name,
        [ValidateSet('OK', 'Warn', 'Fail', 'Info')]
        [string]$Status,
        [string]$Detail,
        $Data = $null
    )

    [void]$checks.Add([ordered]@{
        Area = $Area
        Name = $Name
        Status = $Status
        Detail = $Detail
        Data = $Data
    })
}

function ConvertTo-PlainObject {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $result[$key] = ConvertTo-PlainObject $Value[$key]
        }
        return $result
    }

    if ($Value -is [pscustomobject]) {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $result[$property.Name] = ConvertTo-PlainObject $property.Value
        }
        return $result
    }

    if (($Value -is [System.Collections.IEnumerable]) -and -not ($Value -is [string])) {
        $items = @()
        foreach ($item in $Value) {
            $items += ,(ConvertTo-PlainObject $item)
        }
        return $items
    }

    return $Value
}

function Merge-Config {
    param(
        [System.Collections.IDictionary]$Base,
        [System.Collections.IDictionary]$Overlay
    )

    $result = [ordered]@{}
    if ($null -ne $Base) {
        foreach ($key in $Base.Keys) {
            $result[$key] = $Base[$key]
        }
    }

    if ($null -eq $Overlay) {
        return $result
    }

    foreach ($key in $Overlay.Keys) {
        if ($result.Contains($key) -and
            $result[$key] -is [System.Collections.IDictionary] -and
            $Overlay[$key] -is [System.Collections.IDictionary]) {
            $result[$key] = Merge-Config -Base $result[$key] -Overlay $Overlay[$key]
        }
        else {
            $result[$key] = $Overlay[$key]
        }
    }

    return $result
}

function Read-JsonConfigFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $json = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($json)) {
        return [ordered]@{}
    }

    return ConvertTo-PlainObject ($json | ConvertFrom-Json)
}

function Read-HostAgentConfig {
    param(
        [string]$BasePath,
        [string]$Environment
    )

    $loadedFiles = @()
    $config = [ordered]@{}
    $baseFile = Join-Path $BasePath 'appsettings.json'
    $baseConfig = Read-JsonConfigFile -Path $baseFile
    if ($null -ne $baseConfig) {
        $config = Merge-Config -Base $config -Overlay $baseConfig
        $loadedFiles += $baseFile
    }

    $envName = if ([string]::IsNullOrWhiteSpace($Environment)) { 'Production' } else { $Environment.Trim() }
    $envFile = Join-Path $BasePath ("appsettings.$envName.json")
    $envConfig = Read-JsonConfigFile -Path $envFile
    if ($null -ne $envConfig) {
        $config = Merge-Config -Base $config -Overlay $envConfig
        $loadedFiles += $envFile
    }

    return [ordered]@{
        EnvironmentName = $envName
        Files = $loadedFiles
        Config = $config
    }
}

function Get-ConfigValue {
    param(
        [System.Collections.IDictionary]$Config,
        [string]$Path,
        $Default = $null
    )

    $current = $Config
    foreach ($segment in $Path.Split(':')) {
        if ($current -is [System.Collections.IDictionary] -and $current.Contains($segment)) {
            $current = $current[$segment]
        }
        else {
            return $Default
        }
    }

    if ($null -eq $current) {
        return $Default
    }

    return $current
}

function Get-ExecutablePathFromCommandLine {
    param([string]$CommandLine)

    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return ''
    }

    $text = $CommandLine.Trim()
    if ($text.StartsWith('"')) {
        $end = $text.IndexOf('"', 1)
        if ($end -gt 1) {
            return $text.Substring(1, $end - 1)
        }
    }

    $space = $text.IndexOf(' ')
    if ($space -gt 0) {
        return $text.Substring(0, $space)
    }

    return $text
}

function Resolve-HostAgentPath {
    param(
        $Service,
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    if ($null -eq $Service) {
        return ''
    }

    $exe = Get-ExecutablePathFromCommandLine -CommandLine $Service.PathName
    if ([string]::IsNullOrWhiteSpace($exe)) {
        return ''
    }

    if ([IO.Path]::GetFileName($exe).Equals('dotnet.exe', [StringComparison]::OrdinalIgnoreCase)) {
        $parts = $Service.PathName -split '\s+'
        foreach ($part in $parts) {
            $candidate = $part.Trim('"')
            if ($candidate.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $candidate)) {
                return Split-Path -Parent $candidate
            }
        }
    }

    if (Test-Path -LiteralPath $exe) {
        return Split-Path -Parent $exe
    }

    return ''
}

function Resolve-ConfiguredPath {
    param(
        [string]$Path,
        [string]$BaseDirectory
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    $resolved = $Path.Replace('${basedir}', $BaseDirectory).Replace('/', [IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::IsPathRooted($resolved)) {
        return [IO.Path]::GetFullPath($resolved)
    }

    return [IO.Path]::GetFullPath((Join-Path $BaseDirectory $resolved))
}

function Protect-ConnectionString {
    param([string]$ConnectionString)

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return ''
    }

    try {
        $builder = New-Object System.Data.Common.DbConnectionStringBuilder
        $builder.ConnectionString = $ConnectionString
        foreach ($key in @('Password', 'Pwd', 'User ID', 'UID')) {
            if ($builder.ContainsKey($key)) {
                $builder[$key] = '***'
            }
        }
        return $builder.ConnectionString
    }
    catch {
        return ($ConnectionString -replace '(?i)(password|pwd|user id|uid)\s*=\s*[^;]*', '$1=***')
    }
}

function Convert-DataTableRows {
    param([System.Data.DataTable]$Table)

    $rows = @()
    foreach ($row in $Table.Rows) {
        $item = [ordered]@{}
        foreach ($column in $Table.Columns) {
            $value = $row[$column.ColumnName]
            if ($value -eq [DBNull]::Value) {
                $value = $null
            }
            $item[$column.ColumnName] = $value
        }
        $rows += $item
    }
    return $rows
}

function Invoke-SqlRows {
    param(
        [string]$ConnectionString,
        [string]$Query,
        [hashtable]$Parameters = @{}
    )

    Add-Type -AssemblyName System.Data
    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $Query
        $command.CommandTimeout = 30
        foreach ($key in $Parameters.Keys) {
            [void]$command.Parameters.AddWithValue($key, $Parameters[$key])
        }
        $reader = $command.ExecuteReader()
        try {
            $table = New-Object System.Data.DataTable
            $table.Load($reader)
            return Convert-DataTableRows -Table $table
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $connection.Dispose()
    }
}

function Get-DirectorySummary {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [ordered]@{ Path = ''; Exists = $false; FileCount = 0; DirectoryCount = 0; Error = 'Path is not configured.' }
    }

    $summary = [ordered]@{
        Path = $Path
        Exists = Test-Path -LiteralPath $Path
        FileCount = 0
        DirectoryCount = 0
        Error = ''
    }

    if (-not $summary.Exists) {
        return $summary
    }

    try {
        $items = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop)
        $summary.FileCount = @($items | Where-Object { -not $_.PSIsContainer }).Count
        $summary.DirectoryCount = @($items | Where-Object { $_.PSIsContainer }).Count
    }
    catch {
        $summary.Error = $_.Exception.Message
    }

    return $summary
}

function Get-RecentLogSummary {
    param(
        [string]$Path,
        [int]$Hours
    )

    $summary = Get-DirectorySummary -Path $Path
    $summary.RecentFiles = @()
    if (-not $summary.Exists) {
        return $summary
    }

    try {
        $threshold = (Get-Date).AddHours(-1 * [Math]::Max(1, $Hours))
        $summary.RecentFiles = @(Get-ChildItem -LiteralPath $Path -Filter '*.log' -File -ErrorAction Stop |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 10 Name, Length, LastWriteTimeUtc)
        $summary.RecentLogCount = @($summary.RecentFiles | Where-Object { $_.LastWriteTimeUtc -ge $threshold }).Count
    }
    catch {
        $summary.Error = $_.Exception.Message
    }

    return $summary
}

function Get-EventLogSignals {
    param(
        [string]$ServiceName,
        [int]$Hours
    )

    $startTime = (Get-Date).AddHours(-1 * [Math]::Max(1, $Hours))
    $signals = @()

    foreach ($logName in @('System', 'Application')) {
        try {
            $events = Get-WinEvent -FilterHashtable @{ LogName = $logName; StartTime = $startTime } -ErrorAction Stop |
                Where-Object {
                    $messageMatches = $_.Message -like "*$ServiceName*" -or
                        $_.Message -like '*OpenModulePlatform.HostAgent*' -or
                        $_.Message -like '*OMP.HostAgent*'

                    ($_.ProviderName -like '*Service Control Manager*' -and $messageMatches) -or
                    ($messageMatches -and (
                        $_.ProviderName -like '*.NET Runtime*' -or
                        $_.ProviderName -like '*Application Error*' -or
                        $_.ProviderName -like '*Windows Error Reporting*' -or
                        $_.ProviderName -like '*NLog*'))
                } |
                Select-Object -First 60 TimeCreated, LogName, ProviderName, Id, LevelDisplayName, Message
            $signals += $events
        }
        catch {
            $signals += [pscustomobject]@{
                TimeCreated = $null
                LogName = $logName
                ProviderName = 'Diagnostic'
                Id = 0
                LevelDisplayName = 'Error'
                Message = $_.Exception.Message
            }
        }
    }

    return $signals
}

$service = $null
try {
    $escapedServiceName = $ServiceName.Replace("'", "''")
    $service = Get-CimInstance Win32_Service -Filter "Name = '$escapedServiceName'" -ErrorAction Stop
    if ($null -eq $service) {
        $candidates = @(Get-CimInstance Win32_Service -ErrorAction Stop |
            Where-Object { $_.Name -like 'OMP.HostAgent*' -or $_.Name -like 'OpenModulePlatform.HostAgent*' } |
            Select-Object Name, DisplayName, State, StartMode, StartName, PathName)
        Add-Check 'Windows service' 'Configured service' 'Fail' "Service '$ServiceName' was not found." $candidates
    }
    else {
        Add-Check 'Windows service' 'Configured service' 'OK' "Service '$($service.Name)' was found." ([ordered]@{
            Name = $service.Name
            DisplayName = $service.DisplayName
            State = $service.State
            StartMode = $service.StartMode
            StartName = $service.StartName
            ProcessId = $service.ProcessId
            PathName = $service.PathName
        })
    }
}
catch {
    Add-Check 'Windows service' 'Configured service' 'Fail' $_.Exception.Message
}

$resolvedAppPath = ''
try {
    $resolvedAppPath = Resolve-HostAgentPath -Service $service -ExplicitPath $AppPath
    if ([string]::IsNullOrWhiteSpace($resolvedAppPath)) {
        Add-Check 'Application files' 'HostAgent app path' 'Fail' 'Could not resolve HostAgent application path from parameters or service command line.'
    }
    elseif (Test-Path -LiteralPath $resolvedAppPath) {
        Add-Check 'Application files' 'HostAgent app path' 'OK' $resolvedAppPath
    }
    else {
        Add-Check 'Application files' 'HostAgent app path' 'Fail' "Path does not exist: $resolvedAppPath"
    }
}
catch {
    Add-Check 'Application files' 'HostAgent app path' 'Fail' $_.Exception.Message
}

$configInfo = $null
$config = [ordered]@{}
if (-not [string]::IsNullOrWhiteSpace($resolvedAppPath) -and (Test-Path -LiteralPath $resolvedAppPath)) {
    try {
        $configInfo = Read-HostAgentConfig -BasePath $resolvedAppPath -Environment $EnvironmentName
        $config = $configInfo.Config
        Add-Check 'Runtime configuration' 'appsettings files' 'OK' "Loaded $(@($configInfo.Files).Count) appsettings file(s)." $configInfo.Files
    }
    catch {
        Add-Check 'Runtime configuration' 'appsettings files' 'Fail' $_.Exception.Message
    }
}

$connectionString = [string](Get-ConfigValue -Config $config -Path 'ConnectionStrings:OmpDb' -Default '')
$hostKey = [string](Get-ConfigValue -Config $config -Path 'HostAgent:HostKey' -Default '')
if ([string]::IsNullOrWhiteSpace($hostKey)) {
    $hostKey = [string](Get-ConfigValue -Config $config -Path 'HostAgent:HostName' -Default '')
}
if ([string]::IsNullOrWhiteSpace($hostKey)) {
    $hostKey = $env:COMPUTERNAME
}

$hostAgentSettings = [ordered]@{
    HostKey = $hostKey
    ServiceName = Get-ConfigValue -Config $config -Path 'HostAgent:ServiceName' -Default ''
    Version = Get-ConfigValue -Config $config -Path 'HostAgent:Version' -Default ''
    RefreshSeconds = Get-ConfigValue -Config $config -Path 'HostAgent:RefreshSeconds' -Default $null
    CentralArtifactRoot = Get-ConfigValue -Config $config -Path 'HostAgent:CentralArtifactRoot' -Default ''
    LocalArtifactCacheRoot = Get-ConfigValue -Config $config -Path 'HostAgent:LocalArtifactCacheRoot' -Default ''
    DeployWebApps = Get-ConfigValue -Config $config -Path 'HostAgent:DeployWebApps' -Default $null
    IisSiteName = Get-ConfigValue -Config $config -Path 'HostAgent:IisSiteName' -Default ''
    WebAppsRoot = Get-ConfigValue -Config $config -Path 'HostAgent:WebAppsRoot' -Default ''
    PortalPhysicalPath = Get-ConfigValue -Config $config -Path 'HostAgent:PortalPhysicalPath' -Default ''
    DeployServiceApps = Get-ConfigValue -Config $config -Path 'HostAgent:DeployServiceApps' -Default $null
    ServicesRoot = Get-ConfigValue -Config $config -Path 'HostAgent:ServicesRoot' -Default ''
    ArtifactZipImport = Get-ConfigValue -Config $config -Path 'HostAgent:ArtifactZipImport' -Default $null
}
Add-Check 'Runtime configuration' 'HostAgent settings' 'Info' "Effective host key: $hostKey" $hostAgentSettings
Add-Check 'Runtime configuration' 'OmpDb connection string' ($(if ([string]::IsNullOrWhiteSpace($connectionString)) { 'Fail' } else { 'OK' })) (Protect-ConnectionString $connectionString)

if (-not [string]::IsNullOrWhiteSpace($resolvedAppPath)) {
    $logDirectorySetting = [string](Get-ConfigValue -Config $config -Path 'NLog:variables:logDirectory' -Default '${basedir}/logs')
    $logDirectory = Resolve-ConfiguredPath -Path $logDirectorySetting -BaseDirectory $resolvedAppPath
    $logSummary = Get-RecentLogSummary -Path $logDirectory -Hours $RecentHours
    $logStatus = if (-not $logSummary.Exists) { 'Fail' } elseif ($logSummary.RecentLogCount -lt 1) { 'Warn' } else { 'OK' }
    Add-Check 'Logging' 'NLog log directory' $logStatus $logDirectory $logSummary
}

foreach ($pathItem in @(
    @{ Name = 'CentralArtifactRoot'; Path = [string]$hostAgentSettings.CentralArtifactRoot },
    @{ Name = 'LocalArtifactCacheRoot'; Path = [string]$hostAgentSettings.LocalArtifactCacheRoot },
    @{ Name = 'ArtifactZipImport.ImportPath'; Path = [string](Get-ConfigValue -Config $config -Path 'HostAgent:ArtifactZipImport:ImportPath' -Default '') },
    @{ Name = 'ArtifactZipImport.ProcessedPath'; Path = [string](Get-ConfigValue -Config $config -Path 'HostAgent:ArtifactZipImport:ProcessedPath' -Default '') },
    @{ Name = 'ArtifactZipImport.FailedPath'; Path = [string](Get-ConfigValue -Config $config -Path 'HostAgent:ArtifactZipImport:FailedPath' -Default '') },
    @{ Name = 'WebAppsRoot'; Path = [string]$hostAgentSettings.WebAppsRoot },
    @{ Name = 'PortalPhysicalPath'; Path = [string]$hostAgentSettings.PortalPhysicalPath },
    @{ Name = 'ServicesRoot'; Path = [string]$hostAgentSettings.ServicesRoot }
)) {
    if ([string]::IsNullOrWhiteSpace($pathItem.Path)) {
        Add-Check 'Filesystem' $pathItem.Name 'Info' 'Path is not configured.'
        continue
    }

    $summary = Get-DirectorySummary -Path $pathItem.Path
    $status = if ($summary.Exists) { 'OK' } else { 'Warn' }
    Add-Check 'Filesystem' $pathItem.Name $status $pathItem.Path $summary
}

try {
    $events = Get-EventLogSignals -ServiceName $ServiceName -Hours $RecentHours
    $status = if (@($events).Count -gt 0) { 'Warn' } else { 'OK' }
    Add-Check 'Event logs' 'Recent HostAgent failure signals' $status "Found $(@($events).Count) recent matching event(s)." $events
}
catch {
    Add-Check 'Event logs' 'Recent HostAgent failure signals' 'Warn' $_.Exception.Message
}

try {
    Import-Module WebAdministration -ErrorAction Stop
    $siteName = [string]$hostAgentSettings.IisSiteName
    if ([string]::IsNullOrWhiteSpace($siteName)) {
        Add-Check 'IIS' 'Configured IIS site' 'Info' 'HostAgent:IisSiteName is not configured.'
    }
    else {
        $site = Get-Website -Name $siteName -ErrorAction SilentlyContinue
        if ($null -eq $site) {
            Add-Check 'IIS' 'Configured IIS site' 'Warn' "IIS site '$siteName' was not found."
        }
        else {
            Add-Check 'IIS' 'Configured IIS site' 'OK' "IIS site '$siteName' was found." ($site | Select-Object Name, State, PhysicalPath, Bindings)
        }
    }

    $poolPrefix = [string](Get-ConfigValue -Config $config -Path 'HostAgent:IisAppPoolNamePrefix' -Default 'OMP_')
    $pools = @(Get-ChildItem IIS:\AppPools -ErrorAction Stop |
        Where-Object { $_.Name -like "$poolPrefix*" } |
        Select-Object Name, State, @{ Name = 'IdentityType'; Expression = { $_.processModel.identityType } }, @{ Name = 'UserName'; Expression = { $_.processModel.userName } })
    Add-Check 'IIS' 'Managed app pools' 'Info' "Found $($pools.Count) app pool(s) using prefix '$poolPrefix'." $pools
}
catch {
    Add-Check 'IIS' 'IIS inspection' 'Warn' $_.Exception.Message
}

if (-not [string]::IsNullOrWhiteSpace($connectionString)) {
    try {
        $identityRows = Invoke-SqlRows -ConnectionString $connectionString -Query @'
SELECT
    @@SERVERNAME AS ServerName,
    DB_NAME() AS DatabaseName,
    SUSER_SNAME() AS LoginName,
    ORIGINAL_LOGIN() AS OriginalLoginName,
    USER_NAME() AS DatabaseUserName,
    IS_ROLEMEMBER(N'db_datareader') AS IsDbDataReader,
    IS_ROLEMEMBER(N'db_datawriter') AS IsDbDataWriter,
    IS_ROLEMEMBER(N'db_owner') AS IsDbOwner;
'@
        Add-Check 'SQL' 'Connection identity' 'OK' 'Connected to OMP database.' $identityRows
    }
    catch {
        Add-Check 'SQL' 'Connection identity' 'Fail' $_.Exception.Message
    }

    try {
        $permissionRows = Invoke-SqlRows -ConnectionString $connectionString -Query @'
DECLARE @objects TABLE
(
    ObjectName nvarchar(256) NOT NULL,
    RequiredSelect bit NOT NULL,
    RequiredInsert bit NOT NULL,
    RequiredUpdate bit NOT NULL,
    RequiredDelete bit NOT NULL
);

INSERT INTO @objects(ObjectName, RequiredSelect, RequiredInsert, RequiredUpdate, RequiredDelete)
VALUES
(N'omp.Hosts', 1, 0, 1, 0),
(N'omp.HostAgentLeases', 1, 1, 1, 1),
(N'omp.HostAgentRuntimeStates', 1, 1, 1, 0),
(N'omp.HostAgentDesiredStates', 1, 1, 1, 0),
(N'omp.HostArtifactStates', 1, 1, 1, 0),
(N'omp.HostAppDeploymentStates', 1, 1, 1, 0),
(N'omp.HostDeployments', 1, 0, 1, 0),
(N'omp.HostArtifactRequirements', 1, 0, 0, 0),
(N'omp.Artifacts', 1, 1, 1, 0),
(N'omp.ArtifactConfigurationFiles', 1, 1, 0, 1),
(N'omp.ModuleDefinitionDocuments', 1, 1, 1, 0),
(N'omp.ModuleDefinitionArtifactCompatibility', 1, 1, 0, 1),
(N'omp.ModuleDefinitionSqlExecutions', 1, 1, 1, 0),
(N'omp.HostConfigurationDocuments', 1, 1, 1, 0),
(N'omp.ConfigOverlayDocuments', 1, 1, 1, 0),
(N'omp.ConfigOverlayConfigurationFiles', 1, 1, 0, 1),
(N'omp.Modules', 1, 1, 1, 0),
(N'omp.ModuleInstances', 1, 1, 1, 0),
(N'omp.Apps', 1, 1, 1, 0),
(N'omp.AppInstances', 1, 1, 1, 0),
(N'omp.WorkerInstances', 1, 1, 1, 0),
(N'omp.InstanceTemplateModuleInstances', 1, 1, 1, 0),
(N'omp.InstanceTemplateAppInstances', 1, 1, 1, 0);

SELECT
    ObjectName,
    CASE WHEN OBJECT_ID(ObjectName, N'U') IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS ObjectExists,
    RequiredSelect,
    CASE WHEN OBJECT_ID(ObjectName, N'U') IS NULL THEN NULL ELSE HAS_PERMS_BY_NAME(ObjectName, 'OBJECT', 'SELECT') END AS CanSelect,
    RequiredInsert,
    CASE WHEN OBJECT_ID(ObjectName, N'U') IS NULL THEN NULL ELSE HAS_PERMS_BY_NAME(ObjectName, 'OBJECT', 'INSERT') END AS CanInsert,
    RequiredUpdate,
    CASE WHEN OBJECT_ID(ObjectName, N'U') IS NULL THEN NULL ELSE HAS_PERMS_BY_NAME(ObjectName, 'OBJECT', 'UPDATE') END AS CanUpdate,
    RequiredDelete,
    CASE WHEN OBJECT_ID(ObjectName, N'U') IS NULL THEN NULL ELSE HAS_PERMS_BY_NAME(ObjectName, 'OBJECT', 'DELETE') END AS CanDelete
FROM @objects
ORDER BY ObjectName;
'@
        $issues = @($permissionRows | Where-Object {
            $_.ObjectExists -ne $true -or
            ($_.RequiredSelect -and $_.CanSelect -ne 1) -or
            ($_.RequiredInsert -and $_.CanInsert -ne 1) -or
            ($_.RequiredUpdate -and $_.CanUpdate -ne 1) -or
            ($_.RequiredDelete -and $_.CanDelete -ne 1)
        })
        Add-Check 'SQL' 'HostAgent object permissions' ($(if ($issues.Count -eq 0) { 'OK' } else { 'Warn' })) "$($issues.Count) permission/object issue(s)." $permissionRows
    }
    catch {
        Add-Check 'SQL' 'HostAgent object permissions' 'Fail' $_.Exception.Message
    }

    try {
        $procedureRows = Invoke-SqlRows -ConnectionString $connectionString -Query @'
SELECT
    N'omp.MaterializeInstanceTemplate' AS ObjectName,
    CASE WHEN OBJECT_ID(N'omp.MaterializeInstanceTemplate', N'P') IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS ObjectExists,
    CASE WHEN OBJECT_ID(N'omp.MaterializeInstanceTemplate', N'P') IS NULL THEN NULL ELSE HAS_PERMS_BY_NAME(N'omp.MaterializeInstanceTemplate', 'OBJECT', 'EXECUTE') END AS CanExecute;
'@
        Add-Check 'SQL' 'HostAgent procedure permissions' ($(if ($procedureRows[0].ObjectExists -and $procedureRows[0].CanExecute -eq 1) { 'OK' } else { 'Warn' })) 'Materialization procedure permission.' $procedureRows
    }
    catch {
        Add-Check 'SQL' 'HostAgent procedure permissions' 'Fail' $_.Exception.Message
    }

    try {
        $hostRows = Invoke-SqlRows -ConnectionString $connectionString -Query @'
SELECT TOP (5)
    HostId, HostKey, DisplayName, BaseUrl, Environment, IsEnabled, LastSeenUtc, UpdatedUtc
FROM omp.Hosts
WHERE HostKey = @hostKey
ORDER BY UpdatedUtc DESC;
'@ -Parameters @{ '@hostKey' = $hostKey }
        Add-Check 'SQL' 'Configured host row' ($(if (@($hostRows).Count -gt 0) { 'OK' } else { 'Fail' })) "HostKey=$hostKey" $hostRows
    }
    catch {
        Add-Check 'SQL' 'Configured host row' 'Fail' $_.Exception.Message
    }

    foreach ($querySpec in @(
        @{
            Name = 'HostAgent lease'
            Query = @'
SELECT TOP (10)
    l.ServiceName, l.RuntimeMode, l.LeaseUntilUtc, l.CreatedUtc, l.UpdatedUtc
FROM omp.HostAgentLeases l
INNER JOIN omp.Hosts h ON h.HostId = l.HostId
WHERE h.HostKey = @hostKey
ORDER BY l.UpdatedUtc DESC;
'@
        },
        @{
            Name = 'HostAgent runtime states'
            Query = @'
SELECT TOP (20)
    s.ServiceName, s.Version, s.InstallPath, s.ProcessId, s.RuntimeMode, s.IsActive,
    s.LastSeenUtc, s.QuiesceRequestedUtc, s.QuiescedUtc, s.StatusMessage, s.UpdatedUtc
FROM omp.HostAgentRuntimeStates s
INNER JOIN omp.Hosts h ON h.HostId = s.HostId
WHERE h.HostKey = @hostKey
ORDER BY s.UpdatedUtc DESC;
'@
        },
        @{
            Name = 'Pending host deployments'
            Query = @'
SELECT TOP (20)
    d.HostDeploymentId, d.Status, d.RequestedBy, d.RequestedUtc, d.StartedUtc, d.CompletedUtc, d.OutcomeMessage, d.UpdatedUtc
FROM omp.HostDeployments d
INNER JOIN omp.Hosts h ON h.HostId = d.HostId
WHERE h.HostKey = @hostKey
ORDER BY d.UpdatedUtc DESC;
'@
        },
        @{
            Name = 'Recent artifact states'
            Query = @'
SELECT TOP (25)
    ar.ArtifactId, a.AppKey, ar.Version, ar.PackageType, ar.TargetName,
    st.ProvisioningState, st.LocalPath, st.LastCheckedUtc, st.LastProvisionedUtc,
    LEFT(st.LastError, 1000) AS LastError, st.UpdatedUtc
FROM omp.HostArtifactStates st
INNER JOIN omp.Hosts h ON h.HostId = st.HostId
INNER JOIN omp.Artifacts ar ON ar.ArtifactId = st.ArtifactId
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
WHERE h.HostKey = @hostKey
ORDER BY st.UpdatedUtc DESC;
'@
        },
        @{
            Name = 'Recent app deployment states'
            Query = @'
SELECT TOP (25)
    ai.AppInstanceKey, ds.DeploymentState, ds.SourceLocalPath, ds.TargetPath,
    ds.RuntimeName, ds.LastCheckedUtc, ds.LastAppliedUtc, LEFT(ds.LastError, 1000) AS LastError, ds.UpdatedUtc
FROM omp.HostAppDeploymentStates ds
INNER JOIN omp.Hosts h ON h.HostId = ds.HostId
INNER JOIN omp.AppInstances ai ON ai.AppInstanceId = ds.AppInstanceId
WHERE h.HostKey = @hostKey
ORDER BY ds.UpdatedUtc DESC;
'@
        }
    )) {
        try {
            $rows = Invoke-SqlRows -ConnectionString $connectionString -Query $querySpec.Query -Parameters @{ '@hostKey' = $hostKey }
            Add-Check 'SQL' $querySpec.Name 'Info' "Returned $(@($rows).Count) row(s)." $rows
        }
        catch {
            Add-Check 'SQL' $querySpec.Name 'Warn' $_.Exception.Message
        }
    }
}

$finishedAt = Get-Date
$document = [ordered]@{
    Tool = 'Test-HostAgentAdminDiagnostics'
    MachineName = $env:COMPUTERNAME
    StartedAt = $startedAt.ToUniversalTime().ToString('o')
    FinishedAt = $finishedAt.ToUniversalTime().ToString('o')
    Parameters = [ordered]@{
        ServiceName = $ServiceName
        AppPath = $AppPath
        EnvironmentName = $EnvironmentName
        RecentHours = $RecentHours
    }
    ResolvedAppPath = $resolvedAppPath
    Checks = $checks
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    $OutputPath = Join-Path (Get-Location) "hostagent-admin-diagnostic-$($env:COMPUTERNAME)-$stamp.json"
}

$document | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Wrote HostAgent admin diagnostics to $OutputPath"
