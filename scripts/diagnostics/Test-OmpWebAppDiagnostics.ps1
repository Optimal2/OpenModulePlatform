#Requires -Version 5.1
<#
.SYNOPSIS
Collects diagnostics for one OMP-hosted web application.

.DESCRIPTION
This script is intended to be run from an elevated PowerShell session on the
target OMP host. It inspects HostAgent configuration, OMP database metadata,
IIS application/app-pool state, deployed files, recent application logs, recent
Windows Event Log entries, and an optional HTTP request.

The script is read-only. It does not mutate IIS, Windows services, SQL data, or
application files.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'OMP.HostAgent',
    [string]$HostAgentPath = '',
    [string]$EnvironmentName = 'Production',
    [string]$HostKey = '',
    [string]$RoutePath = '',
    [string]$AppInstanceKey = '',
    [string]$Url = '',
    [string]$ZebraClient = '',
    [int]$RecentHours = 24,
    [int]$HttpTimeoutSeconds = 20,
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

function Normalize-RoutePath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    $value = $Path.Trim()
    while ($value.StartsWith('/')) {
        $value = $value.Substring(1)
    }
    while ($value.EndsWith('/')) {
        $value = $value.Substring(0, $value.Length - 1)
    }

    return $value
}

function Protect-ConnectionString {
    param([string]$ConnectionString)

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return ''
    }

    try {
        $builder = New-Object System.Data.Common.DbConnectionStringBuilder
        $builder.ConnectionString = $ConnectionString
        foreach ($key in @('Password', 'Pwd', 'User ID', 'UserID', 'UID')) {
            if ($builder.ContainsKey($key)) {
                $builder[$key] = '***'
            }
        }
        return $builder.ConnectionString
    }
    catch {
        return ($ConnectionString -replace '(?i)(password|pwd|user id|userid|uid)\s*=\s*[^;]*', '$1=***')
    }
}

function Redact-ConfigValue {
    param(
        [string]$Key,
        $Value
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Key -match '(?i)(password|secret|token|apiKey|apikey|clientSecret|connectionstring)') {
        if ($Value -is [string] -and $Key -match '(?i)connectionstring') {
            return Protect-ConnectionString $Value
        }
        return '***'
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($childKey in $Value.Keys) {
            $result[$childKey] = Redact-ConfigValue -Key ([string]$childKey) -Value $Value[$childKey]
        }
        return $result
    }

    if (($Value -is [System.Collections.IEnumerable]) -and -not ($Value -is [string])) {
        $items = @()
        foreach ($item in $Value) {
            $items += ,(Redact-ConfigValue -Key $Key -Value $item)
        }
        return $items
    }

    return $Value
}

function Normalize-SqlConnectionString {
    param([string]$ConnectionString)

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return ''
    }

    # The application can use Microsoft.Data.SqlClient-style keys, while these
    # diagnostics intentionally use in-box System.Data.SqlClient for portability.
    return ($ConnectionString -replace '(?i)(^|;)\s*Trust\s+Server\s+Certificate\s*=', '$1TrustServerCertificate=')
}

function Convert-DataTableRows {
    param([System.Data.DataTable]$Table)

    $rows = @()
    foreach ($row in $Table.Rows) {
        $item = [ordered]@{}
        foreach ($column in $Table.Columns) {
            $value = $row[$column.ColumnName]
            if ([object]::ReferenceEquals($value, [DBNull]::Value)) {
                $value = $null
            }
            elseif ($value -is [bool]) {
                $value = [int]$value
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
    $connection = New-Object System.Data.SqlClient.SqlConnection (Normalize-SqlConnectionString $ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $Query
        $command.CommandTimeout = 30
        foreach ($key in $Parameters.Keys) {
            $value = $Parameters[$key]
            if ($null -eq $value) {
                $value = [DBNull]::Value
            }
            [void]$command.Parameters.AddWithValue($key, $value)
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
    param(
        [string]$Path,
        [int]$RecentHours
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [ordered]@{ Path = ''; Exists = $false; Error = 'Path is not configured.' }
    }

    $summary = [ordered]@{
        Path = $Path
        Exists = $false
        FileCount = 0
        DirectoryCount = 0
        RecentFiles = @()
        Error = ''
    }

    try {
        $summary.Exists = Test-Path -LiteralPath $Path -ErrorAction Stop
        if (-not $summary.Exists) {
            return $summary
        }

        $items = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop)
        $summary.FileCount = @($items | Where-Object { -not $_.PSIsContainer }).Count
        $summary.DirectoryCount = @($items | Where-Object { $_.PSIsContainer }).Count
        $threshold = (Get-Date).AddHours(-1 * [Math]::Max(1, $RecentHours))
        $summary.RecentFiles = @($items |
            Where-Object { -not $_.PSIsContainer -and $_.LastWriteTimeUtc -ge $threshold } |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 30 Name, Length, LastWriteTimeUtc)
    }
    catch {
        $summary.Error = $_.Exception.Message
    }

    return $summary
}

function Get-FileSummary {
    param([string]$Path)

    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf -ErrorAction Stop)) {
            return [ordered]@{ Path = $Path; Exists = $false }
        }

        $item = Get-Item -LiteralPath $Path -ErrorAction Stop
        return [ordered]@{
            Path = $Path
            Exists = $true
            Length = $item.Length
            LastWriteTimeUtc = $item.LastWriteTimeUtc
        }
    }
    catch {
        return [ordered]@{ Path = $Path; Exists = $false; Error = $_.Exception.Message }
    }
}

function Get-ConfigFileSummaries {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -ErrorAction SilentlyContinue)) {
        return @()
    }

    $files = @(Get-ChildItem -LiteralPath $Path -Filter 'appsettings*.json' -File -ErrorAction SilentlyContinue |
        Sort-Object Name)
    $result = @()
    foreach ($file in $files) {
        $data = [ordered]@{
            Name = $file.Name
            FullName = $file.FullName
            Length = $file.Length
            LastWriteTimeUtc = $file.LastWriteTimeUtc
            ParseStatus = 'NotRead'
            RedactedConfig = $null
            Error = ''
        }

        try {
            $json = Read-JsonConfigFile -Path $file.FullName
            $data.ParseStatus = 'OK'
            $data.RedactedConfig = Redact-ConfigValue -Key $file.Name -Value $json
        }
        catch {
            $data.ParseStatus = 'Fail'
            $data.Error = $_.Exception.Message
        }

        $result += $data
    }

    return $result
}

function Read-AppConfig {
    param(
        [string]$Path,
        [string]$Environment
    )

    $loadedFiles = @()
    $config = [ordered]@{}
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -ErrorAction SilentlyContinue)) {
        return [ordered]@{ Files = $loadedFiles; Config = $config }
    }

    $baseFile = Join-Path $Path 'appsettings.json'
    $baseConfig = Read-JsonConfigFile -Path $baseFile
    if ($null -ne $baseConfig) {
        $config = Merge-Config -Base $config -Overlay $baseConfig
        $loadedFiles += $baseFile
    }

    $envName = if ([string]::IsNullOrWhiteSpace($Environment)) { 'Production' } else { $Environment.Trim() }
    $envFile = Join-Path $Path ("appsettings.$envName.json")
    $envConfig = Read-JsonConfigFile -Path $envFile
    if ($null -ne $envConfig) {
        $config = Merge-Config -Base $config -Overlay $envConfig
        $loadedFiles += $envFile
    }

    return [ordered]@{ Files = $loadedFiles; Config = $config }
}

function Resolve-AppPath {
    param(
        [string]$BasePath,
        [string]$ConfiguredPath
    )

    if ([string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        return ''
    }

    if ([System.IO.Path]::IsPathRooted($ConfiguredPath)) {
        return [System.IO.Path]::GetFullPath($ConfiguredPath)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $ConfiguredPath))
}

function Add-SharedWebConfigChecks {
    param(
        [string]$Path,
        [System.Collections.IDictionary]$Config
    )

    $ompDb = [string](Get-ConfigValue -Config $Config -Path 'ConnectionStrings:OmpDb' -Default '')
    $cookieName = [string](Get-ConfigValue -Config $Config -Path 'OmpAuth:CookieName' -Default '')
    $applicationName = [string](Get-ConfigValue -Config $Config -Path 'OmpAuth:ApplicationName' -Default '')
    $dataProtectionPath = [string](Get-ConfigValue -Config $Config -Path 'OmpAuth:DataProtectionKeyPath' -Default '')

    if ([string]::IsNullOrWhiteSpace($dataProtectionPath) -and -not [string]::IsNullOrWhiteSpace($Path)) {
        $webAppsRoot = Split-Path -Parent $Path
        $runtimeRoot = if ([string]::IsNullOrWhiteSpace($webAppsRoot)) { '' } else { Split-Path -Parent $webAppsRoot }
        if (-not [string]::IsNullOrWhiteSpace($runtimeRoot) -and
            [string]::Equals((Split-Path -Leaf $webAppsRoot), 'WebApps', [StringComparison]::OrdinalIgnoreCase)) {
            $dataProtectionPath = Join-Path $runtimeRoot 'DataProtectionKeys'
        }
    }

    $dataProtectionSummary = if ([string]::IsNullOrWhiteSpace($dataProtectionPath)) {
        [ordered]@{ Path = ''; Exists = $false; Error = 'No configured or inferred DataProtection key path.' }
    }
    else {
        Get-DirectorySummary -Path $dataProtectionPath -RecentHours 24
    }

    $data = [ordered]@{
        HasOmpDbConnectionString = -not [string]::IsNullOrWhiteSpace($ompDb)
        OmpDbConnectionString = Protect-ConnectionString $ompDb
        OmpAuthCookieName = $cookieName
        OmpAuthApplicationName = $applicationName
        DataProtectionKeyPath = $dataProtectionPath
        DataProtectionKeyPathExists = $dataProtectionSummary.Exists
        DataProtectionKeyPathError = $dataProtectionSummary.Error
    }

    $status = if ([string]::IsNullOrWhiteSpace($ompDb)) {
        'Fail'
    }
    elseif (-not $dataProtectionSummary.Exists) {
        'Warn'
    }
    else {
        'OK'
    }

    Add-Check 'Runtime configuration' 'OMP web prerequisites' $status 'Checks OmpDb and shared auth/DataProtection settings used by the shared topbar.' $data
}

function Add-DocumentLibraryConfigChecks {
    param(
        [System.Collections.IDictionary]$Config
    )

    $hasDocumentLibrarySection = $Config.Contains('DokumentBibliotek')
    if (-not $hasDocumentLibrarySection) {
        return
    }

    $useLegacy = [bool](Get-ConfigValue -Config $Config -Path 'DokumentBibliotek:UseLegacyDataStore' -Default $false)
    $dataSchema = [string](Get-ConfigValue -Config $Config -Path 'DokumentBibliotek:DataSchema' -Default '')
    if ([string]::IsNullOrWhiteSpace($dataSchema)) {
        $dataSchema = if ($useLegacy) { 'dbo' } else { 'omp_earkiv_dokumentbibliotek' }
    }

    $ompDb = [string](Get-ConfigValue -Config $Config -Path 'ConnectionStrings:OmpDb' -Default '')
    $legacyDb = [string](Get-ConfigValue -Config $Config -Path 'ConnectionStrings:DokumentBibliotekDb' -Default '')

    Add-Check 'Dokumentbibliotek' 'Data-store configuration' ($(if ($useLegacy -and [string]::IsNullOrWhiteSpace($legacyDb)) { 'Fail' } else { 'OK' })) "UseLegacyDataStore=$useLegacy; DataSchema=$dataSchema" ([ordered]@{
        UseLegacyDataStore = $useLegacy
        DataSchema = $dataSchema
        HasOmpDbConnectionString = -not [string]::IsNullOrWhiteSpace($ompDb)
        HasLegacyConnectionString = -not [string]::IsNullOrWhiteSpace($legacyDb)
        OmpDbConnectionString = Protect-ConnectionString $ompDb
        LegacyConnectionString = Protect-ConnectionString $legacyDb
    })

    $connectionToCheck = if ($useLegacy) { $legacyDb } else { $ompDb }
    if ([string]::IsNullOrWhiteSpace($connectionToCheck)) {
        return
    }

    try {
        $rows = Invoke-SqlRows -ConnectionString $connectionToCheck -Query @'
DECLARE @schema sysname = @schemaName;

WITH RequiredObjects AS
(
    SELECT N'Settings' AS TableName UNION ALL
    SELECT N'Dokument' UNION ALL
    SELECT N'Bilder' UNION ALL
    SELECT N'Blankett' UNION ALL
    SELECT N'Forvaltning' UNION ALL
    SELECT N'DokumentForvaltning' UNION ALL
    SELECT N'Arkiv' UNION ALL
    SELECT N'DokumentArkiv' UNION ALL
    SELECT N'DokumentScope' UNION ALL
    SELECT N'UserSettings'
)
SELECT
    RequiredObjects.TableName,
    CASE WHEN OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(RequiredObjects.TableName), N'U') IS NULL THEN 0 ELSE 1 END AS ExistsInDatabase
FROM RequiredObjects
ORDER BY RequiredObjects.TableName;
'@ -Parameters @{ '@schemaName' = $dataSchema }
        $missing = @($rows | Where-Object { $_.ExistsInDatabase -ne 1 })
        Add-Check 'Dokumentbibliotek' 'Required data tables' ($(if ($missing.Count -gt 0) { 'Warn' } else { 'OK' })) "Checked schema $dataSchema; missing $($missing.Count)." $rows
    }
    catch {
        Add-Check 'Dokumentbibliotek' 'Required data tables' 'Fail' $_.Exception.Message
    }
}

function Add-VajSkrivareConfigChecks {
    param(
        [string]$Path,
        [System.Collections.IDictionary]$Config,
        [string]$Client
    )

    $hasPrinterConfig = $Config.Contains('PrinterDatabases') -or $Config.Contains('ZebraConfig')
    if (-not $hasPrinterConfig) {
        return
    }

    $zebraEnabled = [bool](Get-ConfigValue -Config $Config -Path 'ZebraConfig:Enabled' -Default $false)
    $zebraPath = [string](Get-ConfigValue -Config $Config -Path 'ZebraConfig:FilePath' -Default '')
    $resolvedZebraPath = Resolve-AppPath -BasePath $Path -ConfiguredPath $zebraPath
    $printerItems = @(Get-ConfigValue -Config $Config -Path 'PrinterDatabases:Items' -Default @())

    Add-Check 'VajSkrivare' 'Printer/Zebra configuration' ($(if ($zebraEnabled -and [string]::IsNullOrWhiteSpace($resolvedZebraPath)) { 'Fail' } else { 'OK' })) "Printer database count=$(@($printerItems).Count); ZebraEnabled=$zebraEnabled" ([ordered]@{
        PrinterDatabaseCount = @($printerItems).Count
        ZebraEnabled = $zebraEnabled
        ZebraFilePath = $zebraPath
        ResolvedZebraFilePath = $resolvedZebraPath
    })

    if ([string]::IsNullOrWhiteSpace($resolvedZebraPath)) {
        return
    }

    $fileSummary = Get-FileSummary -Path $resolvedZebraPath
    if (-not $fileSummary.Exists) {
        Add-Check 'VajSkrivare' 'Zebra JSON file' ($(if ($zebraEnabled) { 'Fail' } else { 'Warn' })) $resolvedZebraPath $fileSummary
        return
    }

    try {
        $document = Get-Content -LiteralPath $resolvedZebraPath -Raw | ConvertFrom-Json
        $connections = @($document.Connections)
        $configs = @()
        if ($null -ne $document.Configs) {
            $configs = @($document.Configs.PSObject.Properties)
        }

        $clientMatches = @()
        if (-not [string]::IsNullOrWhiteSpace($Client)) {
            $clientMatches = @($connections | Where-Object {
                [string]::Equals([string]$_.Client, $Client.Trim(), [StringComparison]::OrdinalIgnoreCase)
            })
        }

        Add-Check 'VajSkrivare' 'Zebra JSON content' ($(if (-not [string]::IsNullOrWhiteSpace($Client) -and $clientMatches.Count -eq 0) { 'Warn' } else { 'OK' })) "Connections=$($connections.Count); Configs=$($configs.Count); ClientMatches=$($clientMatches.Count)" ([ordered]@{
            Path = $resolvedZebraPath
            Length = $fileSummary.Length
            LastWriteTimeUtc = $fileSummary.LastWriteTimeUtc
            ConnectionCount = $connections.Count
            ConfigCount = $configs.Count
            CheckedClient = $Client
            ClientMatches = @($clientMatches | Select-Object -First 5 Client, Printer, Config)
        })
    }
    catch {
        Add-Check 'VajSkrivare' 'Zebra JSON content' 'Fail' $_.Exception.Message ([ordered]@{ Path = $resolvedZebraPath })
    }
}

function Get-RecentLogFiles {
    param(
        [string]$Path,
        [int]$RecentHours
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -ErrorAction SilentlyContinue)) {
        return @()
    }

    $threshold = (Get-Date).AddHours(-1 * [Math]::Max(1, $RecentHours))
    $patterns = @('*.log', '*.txt')
    $files = @()
    foreach ($pattern in $patterns) {
        $files += @(Get-ChildItem -LiteralPath $Path -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -ge $threshold })
    }

    return @($files |
        Sort-Object LastWriteTimeUtc -Descending -Unique |
        Select-Object -First 40 FullName, Length, LastWriteTimeUtc)
}

function Add-DeploymentFileChecks {
    param(
        [string]$Path,
        [string]$SourceName,
        [int]$RecentHours
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        Add-Check 'Filesystem' "Deployment target path ($SourceName)" 'Warn' 'Target path was not supplied.'
        return
    }

    $targetSummary = Get-DirectorySummary -Path $Path -RecentHours $RecentHours
    Add-Check 'Filesystem' "Deployment target path ($SourceName)" ($(if ($targetSummary.Exists) { 'OK' } else { 'Fail' })) $Path $targetSummary

    $webConfigPath = Join-Path $Path 'web.config'
    $webConfigSummary = Get-FileSummary -Path $webConfigPath
    Add-Check 'Filesystem' "web.config ($SourceName)" ($(if ($webConfigSummary.Exists) { 'OK' } else { 'Warn' })) $webConfigPath $webConfigSummary
    Add-Check 'Filesystem' "appsettings files ($SourceName)" 'Info' $Path (Get-ConfigFileSummaries -Path $Path)

    foreach ($logPath in @(
        (Join-Path $Path 'logs'),
        (Join-Path $Path 'Logs'),
        (Join-Path $Path 'App_Data\logs')
    )) {
        $logSummary = Get-DirectorySummary -Path $logPath -RecentHours $RecentHours
        if ($logSummary.Exists -or -not [string]::IsNullOrWhiteSpace($logSummary.Error)) {
            Add-Check 'Logging' "Log folder ($SourceName): $logPath" ($(if ($logSummary.Exists) { 'Info' } else { 'Warn' })) $logPath $logSummary
        }
    }

    $recentLogs = Get-RecentLogFiles -Path $Path -RecentHours $RecentHours
    Add-Check 'Logging' "Recent deployed-app log files ($SourceName)" ($(if (@($recentLogs).Count -gt 0) { 'Info' } else { 'Warn' })) "Found $(@($recentLogs).Count) recent log/text file(s)." $recentLogs
}

function Get-EventLogSignals {
    param(
        [string[]]$Needles,
        [int]$Hours
    )

    $startTime = (Get-Date).AddHours(-1 * [Math]::Max(1, $Hours))
    $signals = @()
    foreach ($logName in @('Application', 'System')) {
        try {
            $events = Get-WinEvent -FilterHashtable @{ LogName = $logName; StartTime = $startTime } -ErrorAction Stop |
                Where-Object {
                    $message = [string]$_.Message
                    $provider = [string]$_.ProviderName
                    $matchesNeedle = @($Needles | Where-Object {
                        -not [string]::IsNullOrWhiteSpace($_) -and
                        ($message -like "*$_*" -or $provider -like "*$_*")
                    }).Count -gt 0
                    $isErrorSignal = $_.LevelDisplayName -in @('Error', 'Critical') -and (
                        $provider -like '*ASP.NET Core*' -or
                        $provider -like '*.NET Runtime*' -or
                        $provider -like '*Application Error*' -or
                        $provider -like '*Windows Error Reporting*')
                    $matchesNeedle -or $isErrorSignal
                } |
                Select-Object -First 80 TimeCreated, LogName, ProviderName, Id, LevelDisplayName, @{ Name = 'Message'; Expression = {
                    $message = [string]$_.Message
                    if ($message.Length -gt 4000) {
                        $message.Substring(0, 4000) + '...'
                    }
                    else {
                        $message
                    }
                } }
            $signals += $events
        }
        catch {
            $signals += [ordered]@{ LogName = $logName; Error = $_.Exception.Message }
        }
    }

    return $signals
}

function Invoke-HttpProbe {
    param(
        [string]$ProbeUrl,
        [int]$TimeoutSeconds
    )

    if ([string]::IsNullOrWhiteSpace($ProbeUrl)) {
        return [ordered]@{ Skipped = $true; Reason = 'Url was not supplied.' }
    }

    $result = [ordered]@{
        Url = $ProbeUrl
        StatusCode = $null
        StatusDescription = ''
        FinalUrl = ''
        ContentType = ''
        ContentLength = $null
        BodySnippet = ''
        Error = ''
    }

    try {
        $response = Invoke-WebRequest -Uri $ProbeUrl -UseBasicParsing -TimeoutSec ([Math]::Max(1, $TimeoutSeconds)) -MaximumRedirection 5 -ErrorAction Stop
        $result.StatusCode = [int]$response.StatusCode
        $result.StatusDescription = [string]$response.StatusDescription
        $result.FinalUrl = [string]$response.BaseResponse.ResponseUri
        $result.ContentType = [string]$response.Headers['Content-Type']
        $result.ContentLength = if ($null -ne $response.RawContentLength) { [int64]$response.RawContentLength } else { $null }
        $content = [string]$response.Content
        $result.BodySnippet = if ($content.Length -gt 2000) { $content.Substring(0, 2000) } else { $content }
    }
    catch {
        $result.Error = $_.Exception.Message
        $webResponse = $_.Exception.Response
        if ($null -ne $webResponse) {
            try {
                $result.StatusCode = [int]$webResponse.StatusCode
                $result.StatusDescription = [string]$webResponse.StatusDescription
                $result.FinalUrl = [string]$webResponse.ResponseUri
                $result.ContentType = [string]$webResponse.ContentType
                if ($null -ne $webResponse.ContentLength -and $webResponse.ContentLength -ge 0) {
                    $result.ContentLength = [int64]$webResponse.ContentLength
                }
                $stream = $webResponse.GetResponseStream()
                if ($null -ne $stream) {
                    $reader = New-Object IO.StreamReader($stream)
                    $content = $reader.ReadToEnd()
                    $reader.Dispose()
                    $result.BodySnippet = if ($content.Length -gt 2000) { $content.Substring(0, 2000) } else { $content }
                }
            }
            catch {
                $result.Error = $result.Error + ' | Response read failed: ' + $_.Exception.Message
            }
        }
    }

    return $result
}

$normalizedRoute = Normalize-RoutePath -Path $RoutePath
$applicationPath = if ([string]::IsNullOrWhiteSpace($normalizedRoute)) { '' } else { '/' + $normalizedRoute }

$service = $null
try {
    $escapedServiceName = $ServiceName.Replace("'", "''")
    $service = Get-CimInstance Win32_Service -Filter "Name = '$escapedServiceName'" -ErrorAction Stop
    if ($null -ne $service) {
        Add-Check 'Windows service' 'HostAgent service' 'OK' "Service '$ServiceName' was found." ([ordered]@{
            Name = $service.Name
            State = $service.State
            StartMode = $service.StartMode
            StartName = $service.StartName
            PathName = $service.PathName
        })
    }
    else {
        Add-Check 'Windows service' 'HostAgent service' 'Warn' "Service '$ServiceName' was not found."
    }
}
catch {
    Add-Check 'Windows service' 'HostAgent service' 'Warn' $_.Exception.Message
}

$resolvedHostAgentPath = ''
try {
    $resolvedHostAgentPath = Resolve-HostAgentPath -Service $service -ExplicitPath $HostAgentPath
    if ([string]::IsNullOrWhiteSpace($resolvedHostAgentPath)) {
        Add-Check 'HostAgent' 'Application path' 'Warn' 'HostAgent path could not be resolved.'
    }
    elseif (Test-Path -LiteralPath $resolvedHostAgentPath) {
        Add-Check 'HostAgent' 'Application path' 'OK' $resolvedHostAgentPath
    }
    else {
        Add-Check 'HostAgent' 'Application path' 'Fail' "Path does not exist: $resolvedHostAgentPath"
    }
}
catch {
    Add-Check 'HostAgent' 'Application path' 'Fail' $_.Exception.Message
}

$config = [ordered]@{}
$connectionString = ''
$hostAgentSettings = [ordered]@{}
if (-not [string]::IsNullOrWhiteSpace($resolvedHostAgentPath) -and (Test-Path -LiteralPath $resolvedHostAgentPath -ErrorAction SilentlyContinue)) {
    try {
        $configInfo = Read-HostAgentConfig -BasePath $resolvedHostAgentPath -Environment $EnvironmentName
        $config = $configInfo.Config
        $connectionString = [string](Get-ConfigValue -Config $config -Path 'ConnectionStrings:OmpDb' -Default '')
        $hostAgentSettings = [ordered]@{
            HostKey = Get-ConfigValue -Config $config -Path 'HostAgent:HostKey' -Default $env:COMPUTERNAME
            IisSiteName = Get-ConfigValue -Config $config -Path 'HostAgent:IisSiteName' -Default ''
            WebAppsRoot = Get-ConfigValue -Config $config -Path 'HostAgent:WebAppsRoot' -Default ''
            PortalPhysicalPath = Get-ConfigValue -Config $config -Path 'HostAgent:PortalPhysicalPath' -Default ''
        }
        if ([string]::IsNullOrWhiteSpace($HostKey)) {
            $HostKey = [string]$hostAgentSettings.HostKey
        }
        Add-Check 'Runtime configuration' 'HostAgent appsettings files' 'OK' "Loaded $(@($configInfo.Files).Count) appsettings file(s)." $configInfo.Files
        Add-Check 'Runtime configuration' 'HostAgent settings' 'Info' "Effective host key: $HostKey" $hostAgentSettings
        Add-Check 'Runtime configuration' 'OmpDb connection string' ($(if ([string]::IsNullOrWhiteSpace($connectionString)) { 'Fail' } else { 'OK' })) (Protect-ConnectionString $connectionString)
    }
    catch {
        Add-Check 'Runtime configuration' 'HostAgent configuration' 'Fail' $_.Exception.Message
    }
}

$appRows = @()
$selectedApp = $null
$targetPath = ''
$sourcePath = ''
$runtimeName = ''
$moduleKey = ''
$schemaName = ''
$artifactId = $null

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
        $appRows = Invoke-SqlRows -ConnectionString $connectionString -Query @'
DECLARE @route nvarchar(256) = NULLIF(LTRIM(RTRIM(@routePath)), N'');
DECLARE @appKey nvarchar(100) = NULLIF(LTRIM(RTRIM(@appInstanceKey)), N'');
DECLARE @effectiveHostKey nvarchar(128) = NULLIF(LTRIM(RTRIM(@hostKey)), N'');

IF @route IS NOT NULL
BEGIN
    WHILE LEFT(@route, 1) = N'/' SET @route = SUBSTRING(@route, 2, LEN(@route));
    WHILE RIGHT(@route, 1) = N'/' SET @route = LEFT(@route, LEN(@route) - 1);
END;

DECLARE @hostId uniqueidentifier =
(
    SELECT TOP (1) HostId
    FROM omp.Hosts
    WHERE @effectiveHostKey IS NOT NULL AND HostKey = @effectiveHostKey
    ORDER BY UpdatedUtc DESC
);

SELECT TOP (20)
    h.HostKey,
    h.Environment,
    m.ModuleKey,
    m.SchemaName,
    mi.ModuleInstanceKey,
    a.AppKey,
    ai.AppInstanceKey,
    ai.DisplayName,
    ai.RoutePath,
    ai.PublicUrl,
    ai.InstallPath AS AppInstallPath,
    ai.InstallationName,
    ai.IsEnabled AS AppInstanceIsEnabled,
    ai.IsAllowed AS AppInstanceIsAllowed,
    ai.DesiredState,
    art.ArtifactId,
    art.Version AS ArtifactVersion,
    art.PackageType,
    art.TargetName,
    art.RelativePath AS ArtifactRelativePath,
    art.IsEnabled AS ArtifactIsEnabled,
    ds.DeploymentState,
    ds.SourceLocalPath,
    ds.TargetPath,
    ds.RuntimeName,
    ds.DesiredRuntimeIdentity,
    ds.ActualRuntimeIdentity,
    ds.IdentityCheckStatus,
    ds.LastCheckedUtc,
    ds.LastAppliedUtc,
    ds.LastError,
    ds.UpdatedUtc AS DeploymentUpdatedUtc
FROM omp.AppInstances ai
JOIN omp.Apps a ON a.AppId = ai.AppId
JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
JOIN omp.Modules m ON m.ModuleId = mi.ModuleId
LEFT JOIN omp.Hosts h ON h.HostId = COALESCE(ai.HostId, @hostId)
LEFT JOIN omp.Artifacts art ON art.ArtifactId = ai.ArtifactId
LEFT JOIN omp.HostAppDeploymentStates ds
    ON ds.AppInstanceId = ai.AppInstanceId
   AND ds.HostId = COALESCE(ai.HostId, @hostId)
WHERE
    (@appKey IS NOT NULL AND ai.AppInstanceKey = @appKey)
    OR
    (@route IS NOT NULL AND REPLACE(REPLACE(ai.RoutePath, N'/', N''), N'\', N'') = REPLACE(REPLACE(@route, N'/', N''), N'\', N''))
ORDER BY
    CASE WHEN ai.HostId = @hostId THEN 0 ELSE 1 END,
    ai.UpdatedUtc DESC;
'@ -Parameters @{
            '@routePath' = $normalizedRoute
            '@appInstanceKey' = $AppInstanceKey
            '@hostKey' = $HostKey
        }

        $status = if (@($appRows).Count -gt 0) { 'OK' } else { 'Fail' }
        Add-Check 'SQL' 'Target app instance' $status "Matched $(@($appRows).Count) app instance row(s)." $appRows
        if (@($appRows).Count -gt 0) {
            $selectedApp = $appRows[0]
            $targetPath = [string]$selectedApp.TargetPath
            $sourcePath = [string]$selectedApp.SourceLocalPath
            $runtimeName = [string]$selectedApp.RuntimeName
            $moduleKey = [string]$selectedApp.ModuleKey
            $schemaName = [string]$selectedApp.SchemaName
            $artifactId = $selectedApp.ArtifactId
        }
    }
    catch {
        Add-Check 'SQL' 'Target app instance' 'Fail' $_.Exception.Message
    }

    if ($null -ne $selectedApp) {
        try {
            $configRows = Invoke-SqlRows -ConnectionString $connectionString -Query @'
SELECT TOP (50)
    N'artifact' AS SourceKind,
    af.RelativePath,
    LEN(af.FileContent) AS FileContentLength,
    af.IsEnabled,
    art.ArtifactId,
    art.Version AS ArtifactVersion,
    art.PackageType,
    art.TargetName,
    CAST(NULL AS nvarchar(200)) AS OverlayKey,
    CAST(NULL AS nvarchar(50)) AS OverlayVersion,
    af.UpdatedUtc
FROM omp.ArtifactConfigurationFiles af
JOIN omp.Artifacts art ON art.ArtifactId = af.ArtifactId
WHERE af.ArtifactId = @artifactId
UNION ALL
SELECT TOP (50)
    N'overlay' AS SourceKind,
    cf.RelativePath,
    LEN(cf.FileContent) AS FileContentLength,
    cf.IsEnabled,
    CAST(NULL AS int) AS ArtifactId,
    od.ArtifactVersion,
    od.PackageType,
    od.TargetName,
    od.OverlayKey,
    od.OverlayVersion,
    cf.UpdatedUtc
FROM omp.ConfigOverlayDocuments od
JOIN omp.ConfigOverlayConfigurationFiles cf ON cf.ConfigOverlayDocumentId = od.ConfigOverlayDocumentId
WHERE od.IsEnabled = 1
  AND cf.IsEnabled = 1
  AND (od.HostKey = @hostKey OR od.HostKey = N'*')
  AND (od.ModuleKey IS NULL OR od.ModuleKey = @moduleKey)
  AND (od.AppKey IS NULL OR od.AppKey = @appKey)
  AND (od.PackageType IS NULL OR od.PackageType = @packageType)
  AND (od.TargetName IS NULL OR od.TargetName = @targetName)
ORDER BY UpdatedUtc DESC;
'@ -Parameters @{
                '@artifactId' = $artifactId
                '@hostKey' = $HostKey
                '@moduleKey' = $selectedApp.ModuleKey
                '@appKey' = $selectedApp.AppKey
                '@packageType' = $selectedApp.PackageType
                '@targetName' = $selectedApp.TargetName
            }
            Add-Check 'SQL' 'Configuration files and overlays' 'Info' "Returned $(@($configRows).Count) row(s)." $configRows
        }
        catch {
            Add-Check 'SQL' 'Configuration files and overlays' 'Warn' $_.Exception.Message
        }

        try {
            $runtimeRows = Invoke-SqlRows -ConnectionString $connectionString -Query @'
SELECT TOP (20)
    rs.RuntimeKind,
    rs.WorkerTypeKey,
    rs.ObservedState,
    rs.ProcessId,
    rs.StartedUtc,
    rs.LastSeenUtc,
    rs.LastExitUtc,
    rs.LastExitCode,
    rs.StatusMessage,
    rs.UpdatedUtc
FROM omp.AppInstanceRuntimeStates rs
JOIN omp.AppInstances ai ON ai.AppInstanceId = rs.AppInstanceId
WHERE ai.AppInstanceKey = @appInstanceKey
ORDER BY rs.UpdatedUtc DESC;
'@ -Parameters @{ '@appInstanceKey' = $selectedApp.AppInstanceKey }
            Add-Check 'SQL' 'App runtime states' 'Info' "Returned $(@($runtimeRows).Count) row(s)." $runtimeRows
        }
        catch {
            Add-Check 'SQL' 'App runtime states' 'Warn' $_.Exception.Message
        }

        try {
            $sqlExecutionRows = Invoke-SqlRows -ConnectionString $connectionString -Query @'
SELECT TOP (30)
    d.ModuleKey,
    d.DefinitionVersion,
    d.IsApplied,
    d.AppliedUtc,
    e.ScriptKey,
    e.ScriptPhase,
    e.ScriptOrder,
    e.ExecutionStatus,
    e.StartedUtc,
    e.CompletedUtc,
    e.ErrorMessage
FROM omp.ModuleDefinitionDocuments d
LEFT JOIN omp.ModuleDefinitionSqlExecutions e ON e.ModuleDefinitionDocumentId = d.ModuleDefinitionDocumentId
WHERE d.ModuleKey = @moduleKey
ORDER BY d.CreatedUtc DESC, e.StartedUtc DESC;
'@ -Parameters @{ '@moduleKey' = $moduleKey }
            $failedSql = @($sqlExecutionRows | Where-Object { $_.ExecutionStatus -eq 'Failed' })
            Add-Check 'SQL' 'Module SQL executions' ($(if ($failedSql.Count -gt 0) { 'Warn' } else { 'OK' })) "Returned $(@($sqlExecutionRows).Count) row(s); failed $($failedSql.Count)." $sqlExecutionRows
        }
        catch {
            Add-Check 'SQL' 'Module SQL executions' 'Warn' $_.Exception.Message
        }

        if (-not [string]::IsNullOrWhiteSpace($schemaName)) {
            try {
                $schemaRows = Invoke-SqlRows -ConnectionString $connectionString -Query @'
SELECT
    SCHEMA_ID(@schemaName) AS SchemaId,
    (
        SELECT COUNT(*)
        FROM sys.tables t
        WHERE t.schema_id = SCHEMA_ID(@schemaName)
    ) AS TableCount;
'@ -Parameters @{ '@schemaName' = $schemaName }
                $schemaStatus = if ($schemaRows[0].SchemaId -ne $null) { 'OK' } else { 'Fail' }
                Add-Check 'SQL' 'Module schema' $schemaStatus "Schema=$schemaName" $schemaRows
            }
            catch {
                Add-Check 'SQL' 'Module schema' 'Warn' $_.Exception.Message
            }
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($targetPath)) {
    Add-DeploymentFileChecks -Path $targetPath -SourceName 'OMP deployment state' -RecentHours $RecentHours
    try {
        $appConfigInfo = Read-AppConfig -Path $targetPath -Environment $EnvironmentName
        Add-Check 'Runtime configuration' 'Effective deployed appsettings' ($(if (@($appConfigInfo.Files).Count -gt 0) { 'OK' } else { 'Warn' })) "Loaded $(@($appConfigInfo.Files).Count) appsettings file(s)." ([ordered]@{
            Files = $appConfigInfo.Files
            RedactedConfig = Redact-ConfigValue -Key 'appsettings' -Value $appConfigInfo.Config
        })
        Add-SharedWebConfigChecks -Path $targetPath -Config $appConfigInfo.Config
        Add-DocumentLibraryConfigChecks -Config $appConfigInfo.Config
        Add-VajSkrivareConfigChecks -Path $targetPath -Config $appConfigInfo.Config -Client $ZebraClient
    }
    catch {
        Add-Check 'Runtime configuration' 'Effective deployed appsettings' 'Fail' $_.Exception.Message
    }
}
else {
    Add-Check 'Filesystem' 'Deployment target path' 'Warn' 'TargetPath was not found in OMP deployment state.'
}

if (-not [string]::IsNullOrWhiteSpace($sourcePath)) {
    $sourceSummary = Get-DirectorySummary -Path $sourcePath -RecentHours $RecentHours
    Add-Check 'Filesystem' 'Artifact source cache path' ($(if ($sourceSummary.Exists) { 'OK' } else { 'Warn' })) $sourcePath $sourceSummary
}

$iisApplication = $null
$iisAppPoolName = ''
try {
    Import-Module WebAdministration -ErrorAction Stop
    $siteName = [string]$hostAgentSettings.IisSiteName
    if ([string]::IsNullOrWhiteSpace($siteName)) {
        Add-Check 'IIS' 'Configured site' 'Warn' 'HostAgent:IisSiteName is not configured.'
    }
    else {
        $site = Get-Item "IIS:\Sites\$siteName" -ErrorAction SilentlyContinue
        if ($null -eq $site) {
            Add-Check 'IIS' 'Configured site' 'Fail' "IIS site '$siteName' was not found."
        }
        else {
            Add-Check 'IIS' 'Configured site' 'OK' "IIS site '$siteName' was found." ([ordered]@{
                Name = [string]$site.Name
                State = [string]$site.State
                PhysicalPath = [string]$site.PhysicalPath
            })
        }

        if (-not [string]::IsNullOrWhiteSpace($applicationPath)) {
            $apps = @(Get-WebApplication -Site $siteName -ErrorAction Stop |
                Where-Object { $_.Path.Equals($applicationPath, [StringComparison]::OrdinalIgnoreCase) })
            if ($apps.Count -eq 0) {
                Add-Check 'IIS' 'Web application' 'Fail' "No IIS application with path '$applicationPath' was found under site '$siteName'."
            }
            else {
                $iisApplication = $apps[0]
                $iisAppPoolName = [string]$iisApplication.ApplicationPool
                Add-Check 'IIS' 'Web application' 'OK' "IIS application '$applicationPath' was found." ([ordered]@{
                    Path = [string]$iisApplication.Path
                    PhysicalPath = [string]$iisApplication.PhysicalPath
                    ApplicationPool = [string]$iisApplication.ApplicationPool
                })
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($iisAppPoolName)) {
        $pool = Get-Item "IIS:\AppPools\$iisAppPoolName" -ErrorAction SilentlyContinue
        if ($null -eq $pool) {
            Add-Check 'IIS' 'Application pool' 'Fail' "App pool '$iisAppPoolName' was not found."
        }
        else {
            Add-Check 'IIS' 'Application pool' 'OK' "App pool '$iisAppPoolName' was found." ([ordered]@{
                Name = [string]$pool.Name
                State = [string]$pool.State
                ManagedRuntimeVersion = [string]$pool.ManagedRuntimeVersion
                StartMode = [string]$pool.startMode
                IdentityType = [string]$pool.processModel.identityType
                UserName = [string]$pool.processModel.userName
                Enable32BitAppOnWin64 = [string]$pool.enable32BitAppOnWin64
            })
        }

        $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'w3wp.exe'" -ErrorAction SilentlyContinue |
            Where-Object { [string]$_.CommandLine -like "*$iisAppPoolName*" } |
            Select-Object ProcessId, CreationDate, CommandLine)
        Add-Check 'IIS' 'Application pool worker process' ($(if ($processes.Count -gt 0) { 'OK' } else { 'Warn' })) "Found $($processes.Count) worker process(es)." $processes
    }
}
catch {
    Add-Check 'IIS' 'IIS inspection' 'Warn' $_.Exception.Message
}

if ([string]::IsNullOrWhiteSpace($targetPath) -and $null -ne $iisApplication -and -not [string]::IsNullOrWhiteSpace([string]$iisApplication.PhysicalPath)) {
    Add-DeploymentFileChecks -Path ([string]$iisApplication.PhysicalPath) -SourceName 'IIS application physical path' -RecentHours $RecentHours
}

if (-not [string]::IsNullOrWhiteSpace($Url)) {
    $probe = Invoke-HttpProbe -ProbeUrl $Url -TimeoutSeconds $HttpTimeoutSeconds
    $probeStatus = if ([string]$probe.FinalUrl -like '*/auth/login*') { 'Warn' } elseif ($probe.StatusCode -ge 200 -and $probe.StatusCode -lt 400) { 'OK' } elseif ($probe.StatusCode -ge 500) { 'Fail' } else { 'Warn' }
    Add-Check 'HTTP' 'Endpoint probe' $probeStatus $Url $probe
}
else {
    Add-Check 'HTTP' 'Endpoint probe' 'Info' 'Skipped because -Url was not supplied.'
}

$needles = @(
    $normalizedRoute,
    $applicationPath,
    $runtimeName,
    $iisAppPoolName,
    $targetPath,
    $moduleKey,
    $AppInstanceKey
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

try {
    $events = Get-EventLogSignals -Needles $needles -Hours $RecentHours
    $status = if (@($events).Count -gt 0) { 'Warn' } else { 'OK' }
    Add-Check 'Event logs' 'Recent web-app failure signals' $status "Found $(@($events).Count) matching event(s)." $events
}
catch {
    Add-Check 'Event logs' 'Recent web-app failure signals' 'Warn' $_.Exception.Message
}

$finishedAt = Get-Date
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $safeName = if (-not [string]::IsNullOrWhiteSpace($normalizedRoute)) { $normalizedRoute } elseif (-not [string]::IsNullOrWhiteSpace($AppInstanceKey)) { $AppInstanceKey } else { 'webapp' }
    $safeName = $safeName -replace '[^A-Za-z0-9_.-]+', '-'
    $OutputPath = Join-Path (Get-Location) ("omp-webapp-diagnostic-$safeName-$($env:COMPUTERNAME)-$(Get-Date -Format 'yyyyMMddHHmmss').json")
}

$report = [ordered]@{
    Tool = 'Test-OmpWebAppDiagnostics'
    MachineName = $env:COMPUTERNAME
    StartedAt = $startedAt
    FinishedAt = $finishedAt
    ServiceName = $ServiceName
    HostAgentPath = $resolvedHostAgentPath
    HostKey = $HostKey
    RoutePath = $normalizedRoute
    AppInstanceKey = $AppInstanceKey
    Url = $Url
    Checks = $checks
}

$json = $report | ConvertTo-Json -Depth 24
$parent = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
Set-Content -LiteralPath $OutputPath -Value $json -Encoding UTF8
Write-Host "Wrote OMP web app diagnostics to $OutputPath"
