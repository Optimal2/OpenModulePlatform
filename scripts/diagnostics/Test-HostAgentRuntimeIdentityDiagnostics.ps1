#Requires -Version 5.1
<#
.SYNOPSIS
Runs HostAgent diagnostics as the same identity that should run the service.

.DESCRIPTION
Run this script in a PowerShell session started as the HostAgent service account.
It reads the HostAgent appsettings files, checks filesystem read/write access,
connects to the OMP database with the effective Windows identity, and inspects
HostAgent SQL permissions and host state. By default it does not run a HostAgent
cycle. Use -RunOnce only when you intentionally want to execute one cycle.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'OMP.HostAgent',
    [string]$AppPath = '',
    [string]$EnvironmentName = 'Production',
    [switch]$RunOnce,
    [int]$RunOnceTimeoutSeconds = 120,
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
        return (Get-Location).Path
    }

    $exe = Get-ExecutablePathFromCommandLine -CommandLine $Service.PathName
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

    return (Get-Location).Path
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

function Normalize-SqlConnectionString {
    param([string]$ConnectionString)

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return ''
    }

    try {
        $builder = New-Object System.Data.Common.DbConnectionStringBuilder
        $builder.ConnectionString = $ConnectionString
        if ($builder.ContainsKey('Trust Server Certificate')) {
            $value = $builder['Trust Server Certificate']
            [void]$builder.Remove('Trust Server Certificate')
            if (-not $builder.ContainsKey('TrustServerCertificate')) {
                $builder['TrustServerCertificate'] = $value
            }
        }
        return $builder.ConnectionString
    }
    catch {
        return ($ConnectionString -replace '(?i)Trust\s+Server\s+Certificate\s*=', 'TrustServerCertificate=')
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
    $effectiveConnectionString = Normalize-SqlConnectionString $ConnectionString
    $connection = New-Object System.Data.SqlClient.SqlConnection $effectiveConnectionString
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
        return [ordered]@{ Path = ''; Exists = $false; CanList = $false; Error = 'Path is not configured.' }
    }

    $exists = $false
    $errorMessage = ''
    try {
        $exists = Test-Path -LiteralPath $Path -ErrorAction Stop
    }
    catch {
        $errorMessage = $_.Exception.Message
    }

    $summary = [ordered]@{
        Path = $Path
        Exists = $exists
        CanList = $false
        FileCount = 0
        DirectoryCount = 0
        Error = $errorMessage
    }

    if (-not $summary.Exists) {
        return $summary
    }

    try {
        $items = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop)
        $summary.CanList = $true
        $summary.FileCount = @($items | Where-Object { -not $_.PSIsContainer }).Count
        $summary.DirectoryCount = @($items | Where-Object { $_.PSIsContainer }).Count
    }
    catch {
        $summary.Error = $_.Exception.Message
    }

    return $summary
}

function Test-DirectoryReadWrite {
    param([string]$Path)

    $summary = Get-DirectorySummary -Path $Path
    $summary.CanWrite = $false
    $summary.WriteError = ''
    if (-not $summary.Exists) {
        return $summary
    }

    $testFile = Join-Path $Path ('.omp-hostagent-diagnostic-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Set-Content -LiteralPath $testFile -Value 'test' -Encoding ASCII -ErrorAction Stop
        Remove-Item -LiteralPath $testFile -Force -ErrorAction Stop
        $summary.CanWrite = $true
    }
    catch {
        $summary.WriteError = $_.Exception.Message
        if (Test-Path -LiteralPath $testFile -ErrorAction SilentlyContinue) {
            Remove-Item -LiteralPath $testFile -Force -ErrorAction SilentlyContinue
        }
    }

    return $summary
}

function Quote-Argument {
    param([string]$Argument)

    if ($Argument -match '[\s"]') {
        return '"' + ($Argument.Replace('"', '\"')) + '"'
    }

    return $Argument
}

function Invoke-HostAgentRunOnce {
    param(
        [string]$BasePath,
        [string]$ServiceName,
        [int]$TimeoutSeconds
    )

    $exePath = Join-Path $BasePath 'OpenModulePlatform.HostAgent.WindowsService.exe'
    $dllPath = Join-Path $BasePath 'OpenModulePlatform.HostAgent.WindowsService.dll'
    $fileName = ''
    $arguments = @('--run-once', "--service-name=$ServiceName")

    if (Test-Path -LiteralPath $exePath) {
        $fileName = $exePath
    }
    elseif (Test-Path -LiteralPath $dllPath) {
        $fileName = 'dotnet'
        $arguments = @($dllPath) + $arguments
    }
    else {
        throw "Cannot find HostAgent executable or dll under '$BasePath'."
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $fileName
    $psi.Arguments = ($arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
    $psi.WorkingDirectory = $BasePath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    [void]$process.Start()
    $completed = $process.WaitForExit([Math]::Max(1, $TimeoutSeconds) * 1000)
    if (-not $completed) {
        try {
            $process.Kill()
        }
        catch {
            # Best-effort cleanup only.
        }

        return [ordered]@{
            TimedOut = $true
            ExitCode = $null
            StandardOutput = $process.StandardOutput.ReadToEnd()
            StandardError = $process.StandardError.ReadToEnd()
        }
    }

    return [ordered]@{
        TimedOut = $false
        ExitCode = $process.ExitCode
        StandardOutput = $process.StandardOutput.ReadToEnd()
        StandardError = $process.StandardError.ReadToEnd()
    }
}

$currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
Add-Check 'Runtime identity' 'Current Windows identity' 'OK' $currentIdentity

$service = $null
try {
    $escapedServiceName = $ServiceName.Replace("'", "''")
    $service = Get-CimInstance Win32_Service -Filter "Name = '$escapedServiceName'" -ErrorAction Stop
    if ($null -ne $service) {
        $sameIdentity = $false
        if (-not [string]::IsNullOrWhiteSpace($service.StartName)) {
            $sameIdentity = $currentIdentity.Equals($service.StartName, [StringComparison]::OrdinalIgnoreCase)
        }
        Add-Check 'Windows service' 'Configured service identity' ($(if ($sameIdentity) { 'OK' } else { 'Warn' })) "Service runs as '$($service.StartName)'." ([ordered]@{
            Name = $service.Name
            State = $service.State
            StartName = $service.StartName
            CurrentIdentity = $currentIdentity
            PathName = $service.PathName
        })
    }
    else {
        Add-Check 'Windows service' 'Configured service identity' 'Warn' "Service '$ServiceName' was not found. AppPath must be supplied or current directory must be the HostAgent folder."
    }
}
catch {
    Add-Check 'Windows service' 'Configured service identity' 'Warn' $_.Exception.Message
}

$resolvedAppPath = ''
try {
    $resolvedAppPath = Resolve-HostAgentPath -Service $service -ExplicitPath $AppPath
    if (Test-Path -LiteralPath $resolvedAppPath) {
        Add-Check 'Application files' 'HostAgent app path' 'OK' $resolvedAppPath
    }
    else {
        Add-Check 'Application files' 'HostAgent app path' 'Fail' "Path does not exist: $resolvedAppPath"
    }
}
catch {
    Add-Check 'Application files' 'HostAgent app path' 'Fail' $_.Exception.Message
}

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
    ArtifactZipImport = Get-ConfigValue -Config $config -Path 'HostAgent:ArtifactZipImport' -Default $null
    DeployWebApps = Get-ConfigValue -Config $config -Path 'HostAgent:DeployWebApps' -Default $null
    WebAppsRoot = Get-ConfigValue -Config $config -Path 'HostAgent:WebAppsRoot' -Default ''
    PortalPhysicalPath = Get-ConfigValue -Config $config -Path 'HostAgent:PortalPhysicalPath' -Default ''
    DeployServiceApps = Get-ConfigValue -Config $config -Path 'HostAgent:DeployServiceApps' -Default $null
    ServicesRoot = Get-ConfigValue -Config $config -Path 'HostAgent:ServicesRoot' -Default ''
}
Add-Check 'Runtime configuration' 'HostAgent settings' 'Info' "Effective host key: $hostKey" $hostAgentSettings
Add-Check 'Runtime configuration' 'OmpDb connection string' ($(if ([string]::IsNullOrWhiteSpace($connectionString)) { 'Fail' } else { 'OK' })) (Protect-ConnectionString $connectionString)

if (-not [string]::IsNullOrWhiteSpace($resolvedAppPath)) {
    $logDirectorySetting = [string](Get-ConfigValue -Config $config -Path 'NLog:variables:logDirectory' -Default '${basedir}/logs')
    $logDirectory = Resolve-ConfiguredPath -Path $logDirectorySetting -BaseDirectory $resolvedAppPath
    $logAccess = Test-DirectoryReadWrite -Path $logDirectory
    $logStatus = if ($logAccess.Exists -and $logAccess.CanWrite) { 'OK' } elseif ($logAccess.Exists) { 'Fail' } else { 'Fail' }
    Add-Check 'Filesystem' 'Log directory read/write' $logStatus $logDirectory $logAccess
}

$artifactZipImport = Get-ConfigValue -Config $config -Path 'HostAgent:ArtifactZipImport' -Default $null
$importEnabled = [bool](Get-ConfigValue -Config $config -Path 'HostAgent:ArtifactZipImport:IsEnabled' -Default $false)
$importPath = [string](Get-ConfigValue -Config $config -Path 'HostAgent:ArtifactZipImport:ImportPath' -Default '')
$processedPath = [string](Get-ConfigValue -Config $config -Path 'HostAgent:ArtifactZipImport:ProcessedPath' -Default '')
$failedPath = [string](Get-ConfigValue -Config $config -Path 'HostAgent:ArtifactZipImport:FailedPath' -Default '')
if (-not [string]::IsNullOrWhiteSpace($importPath)) {
    if ([string]::IsNullOrWhiteSpace($processedPath)) {
        $processedPath = Join-Path $importPath 'processed'
    }
    if ([string]::IsNullOrWhiteSpace($failedPath)) {
        $failedPath = Join-Path $importPath 'failed'
    }
}
Add-Check 'Import folder' 'ArtifactZipImport enabled' ($(if ($importEnabled) { 'OK' } else { 'Warn' })) "IsEnabled=$importEnabled" $artifactZipImport

foreach ($pathItem in @(
    @{ Name = 'CentralArtifactRoot'; Path = [string]$hostAgentSettings.CentralArtifactRoot; Required = $true },
    @{ Name = 'LocalArtifactCacheRoot'; Path = [string]$hostAgentSettings.LocalArtifactCacheRoot; Required = $true },
    @{ Name = 'ArtifactZipImport.ImportPath'; Path = $importPath; Required = $importEnabled },
    @{ Name = 'ArtifactZipImport.ProcessedPath'; Path = $processedPath; Required = $importEnabled },
    @{ Name = 'ArtifactZipImport.FailedPath'; Path = $failedPath; Required = $importEnabled },
    @{ Name = 'WebAppsRoot'; Path = [string]$hostAgentSettings.WebAppsRoot; Required = [bool]$hostAgentSettings.DeployWebApps },
    @{ Name = 'PortalPhysicalPath'; Path = [string]$hostAgentSettings.PortalPhysicalPath; Required = [bool]$hostAgentSettings.DeployWebApps },
    @{ Name = 'ServicesRoot'; Path = [string]$hostAgentSettings.ServicesRoot; Required = [bool]$hostAgentSettings.DeployServiceApps }
)) {
    if ([string]::IsNullOrWhiteSpace($pathItem.Path)) {
        Add-Check 'Filesystem' $pathItem.Name ($(if ($pathItem.Required) { 'Fail' } else { 'Info' })) 'Path is not configured.'
        continue
    }

    $access = Test-DirectoryReadWrite -Path $pathItem.Path
    $status = if ($access.Exists -and $access.CanWrite) { 'OK' } elseif ($pathItem.Required) { 'Fail' } else { 'Warn' }
    Add-Check 'Filesystem' $pathItem.Name $status $pathItem.Path $access
}

if ($importEnabled -and -not [string]::IsNullOrWhiteSpace($importPath)) {
    $importDirectory = Get-DirectorySummary -Path $importPath
    if ($importDirectory.Exists) {
        try {
            $pending = @(Get-ChildItem -LiteralPath $importPath -File -ErrorAction Stop |
                Sort-Object Name |
                Select-Object Name, Length, LastWriteTimeUtc)
            $status = if ($pending.Count -gt 0) { 'Warn' } else { 'OK' }
            Add-Check 'Import folder' 'Pending import files' $status "Found $($pending.Count) top-level file(s)." $pending
        }
        catch {
            Add-Check 'Import folder' 'Pending import files' 'Fail' $_.Exception.Message
        }
    }
    else {
        $detail = if ([string]::IsNullOrWhiteSpace($importDirectory.Error)) { 'Import path is not accessible or does not exist.' } else { $importDirectory.Error }
        Add-Check 'Import folder' 'Pending import files' 'Fail' $detail $importDirectory
    }
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
        Add-Check 'SQL' 'HostAgent object permissions' ($(if ($issues.Count -eq 0) { 'OK' } else { 'Fail' })) "$($issues.Count) permission/object issue(s)." $permissionRows
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
        Add-Check 'SQL' 'HostAgent procedure permissions' ($(if ($procedureRows[0].ObjectExists -and $procedureRows[0].CanExecute -eq 1) { 'OK' } else { 'Fail' })) 'Materialization procedure permission.' $procedureRows
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

if ($RunOnce) {
    try {
        $runOnceResult = Invoke-HostAgentRunOnce -BasePath $resolvedAppPath -ServiceName $ServiceName -TimeoutSeconds $RunOnceTimeoutSeconds
        $status = if ($runOnceResult.TimedOut) { 'Fail' } elseif ($runOnceResult.ExitCode -eq 0) { 'OK' } else { 'Fail' }
        Add-Check 'HostAgent run-once' 'Run one HostAgent cycle' $status "ExitCode=$($runOnceResult.ExitCode); TimedOut=$($runOnceResult.TimedOut)" $runOnceResult
    }
    catch {
        Add-Check 'HostAgent run-once' 'Run one HostAgent cycle' 'Fail' $_.Exception.Message
    }
}
else {
    Add-Check 'HostAgent run-once' 'Run one HostAgent cycle' 'Info' 'Skipped. Re-run with -RunOnce to intentionally execute one HostAgent cycle.'
}

$finishedAt = Get-Date
$document = [ordered]@{
    Tool = 'Test-HostAgentRuntimeIdentityDiagnostics'
    MachineName = $env:COMPUTERNAME
    StartedAt = $startedAt.ToUniversalTime().ToString('o')
    FinishedAt = $finishedAt.ToUniversalTime().ToString('o')
    Parameters = [ordered]@{
        ServiceName = $ServiceName
        AppPath = $AppPath
        EnvironmentName = $EnvironmentName
        RunOnce = [bool]$RunOnce
        RunOnceTimeoutSeconds = $RunOnceTimeoutSeconds
    }
    CurrentIdentity = $currentIdentity
    ResolvedAppPath = $resolvedAppPath
    Checks = $checks
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    $OutputPath = Join-Path (Get-Location) "hostagent-runtime-diagnostic-$($env:COMPUTERNAME)-$stamp.json"
}

$document | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Wrote HostAgent runtime identity diagnostics to $OutputPath"
