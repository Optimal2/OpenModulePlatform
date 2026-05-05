# File: scripts/deployment/uninstall-omp-suite.ps1
[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'omp-suite.local.psd1'),
    [switch]$Yes,
    [switch]$KeepFiles,
    [switch]$KeepDatabaseObjects,
    [switch]$KeepIis,
    [switch]$KeepServices
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:appcmdPath = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'

function Write-Step {
    param([string]$Message)
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Confirm-DeploymentAction {
    param([string]$Message)
    if ($Yes) { return $true }
    $answer = Read-Host "$Message [y/j/N]"
    return $answer -imatch '^(y|yes|j|ja)$'
}

function Import-RequiredDeploymentConfig {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $samplePath = Join-Path $PSScriptRoot 'omp-suite.config.sample.psd1'
        throw "Deployment config not found: $Path. Copy $samplePath to omp-suite.local.psd1 and adjust it for this machine."
    }

    $config = Import-PowerShellDataFile -LiteralPath $Path
    if ($null -eq $config) {
        throw "Deployment config could not be read: $Path"
    }

    return $config
}

function Get-ConfigValue {
    param(
        [hashtable]$Config,
        [string]$Name,
        $DefaultValue = $null
    )

    if ($Config.ContainsKey($Name) -and $null -ne $Config[$Name]) {
        return $Config[$Name]
    }

    return $DefaultValue
}

function Get-NestedConfigValue {
    param(
        [hashtable]$Config,
        [string]$Section,
        [string]$Name,
        $DefaultValue = $null
    )

    if ($Config.ContainsKey($Section) -and $Config[$Section] -is [hashtable]) {
        $sectionTable = [hashtable]$Config[$Section]
        if ($sectionTable.ContainsKey($Name) -and $null -ne $sectionTable[$Name]) {
            return $sectionTable[$Name]
        }
    }

    return $DefaultValue
}

function Require-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found in PATH."
    }
}

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($Arguments -join ' ')"
    }
}

function Get-SqlcmdBaseArguments {
    param([string]$TargetDatabase)

    $args = @('-S', $script:SqlServer, '-d', $TargetDatabase, '-b')
    if ([string]::Equals($script:SqlAuthentication, 'SqlLogin', [StringComparison]::OrdinalIgnoreCase)) {
        $args += @('-U', $script:SqlUser, '-P', $script:SqlPassword)
    }
    else {
        $args += '-E'
    }

    return $args
}

function Invoke-SqlText {
    param(
        [Parameter(Mandatory = $true)][string]$Query,
        [string]$TargetDatabase = $script:Database
    )

    Require-Command sqlcmd
    $tempPath = [System.IO.Path]::GetTempFileName()
    Set-Content -LiteralPath $tempPath -Value $Query -Encoding UTF8
    try {
        $args = Get-SqlcmdBaseArguments -TargetDatabase $TargetDatabase
        $args += @('-i', $tempPath)
        Invoke-NativeChecked sqlcmd @args
    }
    finally {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
}

function ConvertTo-SqlUnicodeLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "N'$($Value.Replace("'", "''"))'"
}

function Remove-WindowsServiceIfExists {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return
    }

    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "Stopping service: $Name"
        Stop-Service -Name $Name -Force -ErrorAction Continue
        try { (Get-Service -Name $Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { }
    }

    Invoke-NativeChecked (Join-Path $env:windir 'System32\sc.exe') delete $Name
}

function Remove-Services {
    if ($KeepServices -or -not $script:RemoveServices) {
        return
    }

    Write-Step 'Removing Windows services'
    foreach ($serviceName in @($script:Services.ExampleService, $script:Services.WorkerManager, $script:Services.HostAgent)) {
        if (-not [string]::IsNullOrWhiteSpace($serviceName)) {
            Remove-WindowsServiceIfExists -Name $serviceName
        }
    }
}

function Remove-Iis {
    if ($KeepIis -or -not $script:RemoveIis) {
        return
    }
    if (-not (Test-Path -LiteralPath $script:appcmdPath -PathType Leaf)) {
        Write-Warning "IIS appcmd.exe was not found. Skipping IIS cleanup: $script:appcmdPath"
        return
    }

    Write-Step 'Removing IIS applications, site, and app pools'
    $apps = @(
        "$script:IisSiteName/$script:OpenDocViewerAppPath",
        "$script:IisSiteName/iFrameWebAppModule",
        "$script:IisSiteName/ExampleWorkerAppModule",
        "$script:IisSiteName/ExampleServiceAppModule",
        "$script:IisSiteName/ExampleWebAppBlazorModule",
        "$script:IisSiteName/ExampleWebAppModule",
        "$script:IisSiteName/auth"
    )

    foreach ($app in $apps) {
        Remove-IisApplicationIfExists -Name $app
    }

    Remove-IisSiteIfExists -Name $script:IisSiteName

    foreach ($poolName in @($script:AppPools.Portal, $script:AppPools.Auth, $script:AppPools.OpenDocViewer, $script:AppPools.ExampleWebApp, $script:AppPools.ExampleWebAppBlazor, $script:AppPools.ExampleServiceWebApp, $script:AppPools.ExampleWorkerWebApp, $script:AppPools.IFrameWebApp)) {
        if ([string]::IsNullOrWhiteSpace($poolName)) {
            continue
        }

        Remove-IisAppPoolIfExists -Name $poolName
    }
}

function Test-IisApplicationExact {
    param([string]$Name)

    foreach ($line in @(& $script:appcmdPath list app 2>$null)) {
        $text = $line.ToString()
        if ($text -match '^APP "([^"]+)"' -and [string]::Equals($Matches[1], $Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Test-IisSiteExact {
    param([string]$Name)

    foreach ($line in @(& $script:appcmdPath list site 2>$null)) {
        $text = $line.ToString()
        if ($text -match '^SITE "([^"]+)"' -and [string]::Equals($Matches[1], $Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Test-IisAppPoolExact {
    param([string]$Name)

    foreach ($line in @(& $script:appcmdPath list apppool 2>$null)) {
        $text = $line.ToString()
        if ($text -match '^APPPOOL "([^"]+)"' -and [string]::Equals($Matches[1], $Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Remove-IisApplicationIfExists {
    param([string]$Name)

    if (-not (Test-IisApplicationExact -Name $Name)) {
        return
    }

    Write-Host "> $script:appcmdPath delete app $Name"
    & $script:appcmdPath delete app $Name
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and (Test-IisApplicationExact -Name $Name)) {
        throw "Command failed with exit code ${exitCode}: $script:appcmdPath delete app $Name"
    }
}

function Remove-IisSiteIfExists {
    param([string]$Name)

    if (-not (Test-IisSiteExact -Name $Name)) {
        return
    }

    Write-Host "> $script:appcmdPath delete site $Name"
    & $script:appcmdPath delete site $Name
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and (Test-IisSiteExact -Name $Name)) {
        throw "Command failed with exit code ${exitCode}: $script:appcmdPath delete site $Name"
    }
}

function Remove-IisAppPoolIfExists {
    param([string]$Name)

    if (-not (Test-IisAppPoolExact -Name $Name)) {
        return
    }

    Write-Host "> $script:appcmdPath delete apppool $Name"
    & $script:appcmdPath delete apppool $Name
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and (Test-IisAppPoolExact -Name $Name)) {
        throw "Command failed with exit code ${exitCode}: $script:appcmdPath delete apppool $Name"
    }
}

function Assert-RemovablePath {
    param([string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $allowedRoots = @($script:RuntimeRoot, $script:WebRoot, $script:WebAppsRoot, $script:ServicesRoot, $script:ArtifactStoreRoot, $script:ArtifactCacheRoot, $script:DataProtectionKeyPath)
    foreach ($root in $allowedRoots) {
        $rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\')
        if ($fullPath.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or $fullPath.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    throw "Refusing to remove path outside configured deployment roots: $Path"
}

function Resolve-RemovePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $script:RuntimeRoot $Path))
}

function Remove-Files {
    if ($KeepFiles -or -not $script:RemoveFiles) {
        return
    }

    Write-Step 'Removing deployed files'
    foreach ($path in $script:RemovePaths) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $fullPath = Resolve-RemovePath -Path $path
        Assert-RemovablePath -Path $fullPath
        if (Test-Path -LiteralPath $fullPath) {
            Remove-Item -LiteralPath $fullPath -Recurse -Force
            Write-Host "Removed: $fullPath"
        }
    }
}

function Remove-DatabaseObjects {
    if ($KeepDatabaseObjects -or -not $script:RemoveDatabaseObjects) {
        return
    }

    Write-Step 'Removing database objects'
    $schemaValues = @()
    foreach ($schema in $script:DatabaseSchemas) {
        if (-not [string]::IsNullOrWhiteSpace($schema)) {
            $schemaValues += '(' + (ConvertTo-SqlUnicodeLiteral -Value $schema) + ')'
        }
    }

    if ($schemaValues.Count -eq 0) {
        return
    }

    $dropSchemasBit = if ($script:DropSchemas) { 1 } else { 0 }
    Invoke-SqlText -Query @"
SET NOCOUNT ON;

DECLARE @DropSchemas bit = $dropSchemasBit;
DECLARE @Schemas table(Name sysname NOT NULL PRIMARY KEY);
INSERT INTO @Schemas(Name)
VALUES
$(($schemaValues -join ",`r`n"));

DECLARE @sql nvarchar(max) = N'';

SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(parent.schema_id)) + N'.' + QUOTENAME(parent.name) + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';' + CHAR(13) + CHAR(10)
FROM sys.foreign_keys fk
JOIN sys.tables parent ON parent.object_id = fk.parent_object_id
JOIN sys.tables referenced ON referenced.object_id = fk.referenced_object_id
WHERE SCHEMA_NAME(parent.schema_id) IN (SELECT Name FROM @Schemas)
   OR SCHEMA_NAME(referenced.schema_id) IN (SELECT Name FROM @Schemas);

IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;

SET @sql = N'';
SELECT @sql = @sql + N'DROP VIEW ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' + CHAR(13) + CHAR(10)
FROM sys.views
WHERE SCHEMA_NAME(schema_id) IN (SELECT Name FROM @Schemas);
IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;

SET @sql = N'';
SELECT @sql = @sql + N'DROP PROCEDURE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' + CHAR(13) + CHAR(10)
FROM sys.procedures
WHERE SCHEMA_NAME(schema_id) IN (SELECT Name FROM @Schemas);
IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;

SET @sql = N'';
SELECT @sql = @sql + N'DROP FUNCTION ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' + CHAR(13) + CHAR(10)
FROM sys.objects
WHERE type IN (N'FN', N'IF', N'TF')
  AND SCHEMA_NAME(schema_id) IN (SELECT Name FROM @Schemas);
IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;

SET @sql = N'';
SELECT @sql = @sql + N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' + CHAR(13) + CHAR(10)
FROM sys.tables
WHERE SCHEMA_NAME(schema_id) IN (SELECT Name FROM @Schemas);
IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;

IF @DropSchemas = 1
BEGIN
    SET @sql = N'';
    SELECT @sql = @sql + N'DROP SCHEMA ' + QUOTENAME(Name) + N';' + CHAR(13) + CHAR(10)
    FROM @Schemas
    WHERE SCHEMA_ID(Name) IS NOT NULL;
    IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;
END
"@
}

$config = Import-RequiredDeploymentConfig -Path $ConfigPath

$script:RuntimeRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'RuntimeRoot' -DefaultValue 'C:\OMP'))
$script:WebRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'WebRoot' -DefaultValue (Join-Path $script:RuntimeRoot 'Sites')))
$script:WebAppsRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'WebAppsRoot' -DefaultValue (Join-Path $script:RuntimeRoot 'WebApps')))
$script:ServicesRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'ServicesRoot' -DefaultValue (Join-Path $script:RuntimeRoot 'Services')))
$script:ArtifactStoreRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'ArtifactStoreRoot' -DefaultValue (Join-Path $script:RuntimeRoot 'ArtifactStore')))
$script:ArtifactCacheRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'ArtifactCacheRoot' -DefaultValue (Join-Path $script:RuntimeRoot 'ArtifactCache')))
$script:DataProtectionKeyPath = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'DataProtectionKeyPath' -DefaultValue (Join-Path $script:RuntimeRoot 'DataProtectionKeys')))

$script:SqlServer = [string](Get-ConfigValue -Config $config -Name 'SqlServer' -DefaultValue 'localhost')
$script:Database = [string](Get-ConfigValue -Config $config -Name 'Database' -DefaultValue 'OpenModulePlatform')
$script:SqlAuthentication = [string](Get-ConfigValue -Config $config -Name 'SqlAuthentication' -DefaultValue 'Integrated')
$script:SqlUser = [string](Get-ConfigValue -Config $config -Name 'SqlUser' -DefaultValue '')
$script:SqlPassword = [string](Get-ConfigValue -Config $config -Name 'SqlPassword' -DefaultValue '')

$script:IisSiteName = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'SiteName' -DefaultValue 'OpenModulePlatform')
$script:OpenDocViewerAppPath = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'OpenDocViewerAppPath' -DefaultValue 'opendocviewer')

$defaultAppPools = @{
    Portal = 'OMP_Portal'
    Auth = 'OMP_Auth'
    OpenDocViewer = 'OMP_OpenDocViewer'
    ExampleWebApp = 'OMP_ExampleWebAppModule'
    ExampleWebAppBlazor = 'OMP_ExampleWebAppBlazorModule'
    ExampleServiceWebApp = 'OMP_ExampleServiceAppModule'
    ExampleWorkerWebApp = 'OMP_ExampleWorkerAppModule'
    IFrameWebApp = 'OMP_iFrameWebAppModule'
}
$configuredPools = Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'AppPools' -DefaultValue @{}
foreach ($key in @($configuredPools.Keys)) {
    $defaultAppPools[$key] = [string]$configuredPools[$key]
}
$script:AppPools = [pscustomobject]$defaultAppPools

$defaultServices = @{
    HostAgent = 'OpenModulePlatform.HostAgent'
    WorkerManager = 'OpenModulePlatform.WorkerManager'
    ExampleService = 'OpenModulePlatform.Service.ExampleServiceAppModule'
}
$configuredServices = Get-ConfigValue -Config $config -Name 'Services' -DefaultValue @{}
foreach ($key in @($configuredServices.Keys)) {
    $defaultServices[$key] = [string]$configuredServices[$key]
}
$script:Services = [pscustomobject]$defaultServices

$script:RemoveIis = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'RemoveIis' -DefaultValue $true)
$script:RemoveServices = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'RemoveServices' -DefaultValue $true)
$script:RemoveFiles = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'RemoveFiles' -DefaultValue $true)
$script:RemoveDatabaseObjects = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'RemoveDatabaseObjects' -DefaultValue $true)
$script:DropSchemas = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'DropSchemas' -DefaultValue $true)
$script:DatabaseSchemas = @((Get-ConfigValue -Config $config -Name 'DatabaseSchemas' -DefaultValue @('omp_iframe', 'omp_example_workerapp', 'omp_example_serviceapp', 'omp_example_webapp_blazor', 'omp_example_webapp', 'omp_portal', 'omp')))
$script:RemovePaths = @((Get-ConfigValue -Config $config -Name 'RemovePaths' -DefaultValue @()))

Write-Host ''
Write-Host "This will uninstall OpenModulePlatform/ODV/example components for '$([string](Get-ConfigValue -Config $config -Name 'EnvironmentName' -DefaultValue 'environment'))'." -ForegroundColor Yellow
Write-Host "Database '$script:Database' on '$script:SqlServer' will be kept, but configured schemas/tables can be removed."
if (-not (Confirm-DeploymentAction 'Continue with uninstall')) {
    Write-Host 'Uninstall cancelled.'
    return
}

Remove-Services
Remove-Iis
Remove-Files
Remove-DatabaseObjects

Write-Host ''
Write-Host 'OpenModulePlatform suite uninstall completed.' -ForegroundColor Green
