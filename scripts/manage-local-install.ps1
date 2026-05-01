# File: scripts/manage-local-install.ps1
[CmdletBinding()]
param(
    [ValidateSet('Menu', 'Install', 'Uninstall', 'Reinstall')]
    [string]$Action = 'Menu',

    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$RuntimeRoot = 'E:\OMP',
    [string]$SqlServer = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string]$BootstrapPortalAdminPrincipal = "$env:USERDOMAIN\$env:USERNAME",
    [string]$DatabaseOwnerPrincipal = "$env:USERDOMAIN\$env:USERNAME",
    [string]$HostKey = 'sample-host',
    [string]$IisSiteName = 'OpenModulePlatform',
    [int]$IisPort = 8088,
    [switch]$DropDatabase,
    [switch]$ClearDatabaseObjects,
    [switch]$RemoveRuntimeFiles,
    [switch]$SkipBuild,
    [switch]$SkipPublish,
    [switch]$SkipSql,
    [switch]$SkipIis,
    [switch]$SkipServices,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:serviceNames = @('OpenModulePlatform.WorkerManager', 'OpenModulePlatform.HostAgent')
$script:startServiceNames = @('OpenModulePlatform.HostAgent', 'OpenModulePlatform.WorkerManager')
$script:appPools = @('OMP_Portal')
$script:publishRoot = Join-Path $RuntimeRoot 'Publish\OMP'

function Write-Step {
    param([string]$Message)
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Confirm-LocalAction {
    param([string]$Message)
    if ($Yes) { return $true }
    $answer = Read-Host "$Message [y/N]"
    return $answer -match '^(y|yes|j|ja)$'
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
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $($LASTEXITCODE): $FilePath $($Arguments -join ' ')"
    }
}

function Invoke-RobocopyChecked {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string[]]$Options = @('/MIR', '/R:2', '/W:2', '/NFL', '/NDL', '/NP')
    )

    Write-Host "> robocopy $Source $Destination $($Options -join ' ')"
    & robocopy $Source $Destination @Options
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $($LASTEXITCODE): $Source -> $Destination"
    }
}

function Invoke-SqlText {
    param(
        [Parameter(Mandatory = $true)][string]$Query,
        [string]$TargetDatabase = 'master'
    )

    Require-Command sqlcmd
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("omp-sql-{0}.sql" -f ([guid]::NewGuid().ToString('N')))
    Set-Content -LiteralPath $temp -Value $Query -Encoding UTF8
    try {
        Invoke-NativeChecked sqlcmd '-S' $SqlServer '-d' $TargetDatabase '-E' '-b' '-i' $temp
    }
    finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-SqlFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$TargetDatabase = $Database
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "SQL file does not exist: $Path"
    }

    Require-Command sqlcmd
    Invoke-NativeChecked sqlcmd '-S' $SqlServer '-d' $TargetDatabase '-E' '-b' '-i' $Path
}

function New-BootstrapPatchedSqlFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "SQL file does not exist: $Path"
    }

    if ([string]::IsNullOrWhiteSpace($BootstrapPortalAdminPrincipal) -or $BootstrapPortalAdminPrincipal -like 'REPLACE_ME*') {
        throw 'BootstrapPortalAdminPrincipal must be an actual Windows user or group, not REPLACE_ME.'
    }

    $escapedPrincipal = $BootstrapPortalAdminPrincipal.Replace("'", "''")
    $content = Get-Content -LiteralPath $Path -Raw
    $pattern = "DECLARE\s+@BootstrapPortalAdminPrincipal\s+nvarchar\(256\)\s*=\s*N'REPLACE_ME\\UserOrGroup';"
    $replacement = "DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = N'$escapedPrincipal';"
    $content = [System.Text.RegularExpressions.Regex]::Replace($content, $pattern, $replacement)

    if ($content -like '*REPLACE_ME\UserOrGroup*') {
        throw "Failed to patch bootstrap principal in SQL file: $Path"
    }

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("omp-init-{0}.sql" -f ([guid]::NewGuid().ToString('N')))
    Set-Content -LiteralPath $temp -Value $content -Encoding UTF8
    return $temp
}

function Invoke-SqlFileWithBootstrapPrincipal {
    param([Parameter(Mandatory = $true)][string]$Path)

    $temp = New-BootstrapPatchedSqlFile -Path $Path
    try {
        Invoke-SqlFile -Path $temp -TargetDatabase $Database
    }
    finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

function Stop-LocalRuntime {
    Write-Step 'Stopping local OMP runtime'

    foreach ($serviceName in $script:serviceNames) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            Write-Host "Service not installed: $serviceName"
            continue
        }
        if ($service.Status -eq 'Stopped') {
            Write-Host "Service already stopped: $serviceName"
            continue
        }
        Write-Host "Stopping service: $serviceName"
        Stop-Service -Name $serviceName -Force -ErrorAction Continue
        try { (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { }
    }

    if (-not $SkipIis -and (Get-Module -ListAvailable -Name WebAdministration)) {
        Import-Module WebAdministration
        foreach ($poolName in $script:appPools) {
            if (-not (Test-Path "IIS:\AppPools\$poolName")) {
                Write-Host "App pool not found: $poolName"
                continue
            }
            $state = (Get-WebAppPoolState -Name $poolName).Value
            if ($state -eq 'Stopped') {
                Write-Host "App pool already stopped: $poolName"
                continue
            }
            Write-Host "Stopping app pool: $poolName"
            Stop-WebAppPool -Name $poolName -ErrorAction Continue
        }
    }
}

function Start-LocalRuntime {
    Write-Step 'Starting local OMP runtime'

    if (-not $SkipIis -and (Get-Module -ListAvailable -Name WebAdministration)) {
        Import-Module WebAdministration
        foreach ($poolName in $script:appPools) {
            if (-not (Test-Path "IIS:\AppPools\$poolName")) { continue }
            $state = (Get-WebAppPoolState -Name $poolName).Value
            if ($state -eq 'Started') {
                Write-Host "App pool already started: $poolName"
                continue
            }
            Write-Host "Starting app pool: $poolName"
            Start-WebAppPool -Name $poolName -ErrorAction Continue
        }
    }

    foreach ($serviceName in $script:startServiceNames) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -eq $service) { continue }
        if ($service.Status -eq 'Running') {
            Write-Host "Service already running: $serviceName"
            continue
        }
        Write-Host "Starting service: $serviceName"
        Start-Service -Name $serviceName -ErrorAction Continue
    }
}

function Ensure-Database {
    Write-Step 'Ensuring database'

    if ($DropDatabase) {
        if (-not (Confirm-LocalAction "Drop and recreate database '$Database' on '$SqlServer'?")) {
            throw 'Database drop was not confirmed.'
        }

        Invoke-SqlText -TargetDatabase master -Query @"
IF DB_ID(N'$Database') IS NOT NULL
BEGIN
    ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$Database];
END
CREATE DATABASE [$Database];
"@
    }
    else {
        Invoke-SqlText -TargetDatabase master -Query @"
IF DB_ID(N'$Database') IS NULL
    CREATE DATABASE [$Database];
"@
    }

    if (-not [string]::IsNullOrWhiteSpace($DatabaseOwnerPrincipal)) {
        $escapedPrincipal = $DatabaseOwnerPrincipal.Replace("'", "''")
        Invoke-SqlText -TargetDatabase $Database -Query @"
DECLARE @principal sysname = N'$escapedPrincipal';
DECLARE @sql nvarchar(max);
IF SUSER_ID(@principal) IS NULL
BEGIN
    RAISERROR('Server login does not exist: %s. Create it manually or pass an existing -DatabaseOwnerPrincipal.', 11, 1, @principal);
END
IF DATABASE_PRINCIPAL_ID(@principal) IS NULL
BEGIN
    SET @sql = N'CREATE USER ' + QUOTENAME(@principal) + N' FOR LOGIN ' + QUOTENAME(@principal) + N';';
    EXEC sys.sp_executesql @sql;
END
IF IS_ROLEMEMBER(N'db_owner', @principal) <> 1
BEGIN
    EXEC sys.sp_addrolemember N'db_owner', @principal;
END
"@
    }
}

function Clear-DatabaseUserObjects {
    Write-Step 'Clearing database user objects'

    if (-not (Confirm-LocalAction "Drop all user tables, views, routines and non-system schemas in database '$Database'?")) {
        throw 'Database cleanup was not confirmed.'
    }

    Invoke-SqlText -TargetDatabase $Database -Query @'
SET NOCOUNT ON;
DECLARE @sql nvarchar(max);
DECLARE @iteration int = 0;

WHILE @iteration < 10
BEGIN
    SET @iteration += 1;
    SET @sql = N'';

    SELECT @sql += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id)) +
                   N' DROP CONSTRAINT ' + QUOTENAME(name) + N';' + CHAR(13) + CHAR(10)
    FROM sys.foreign_keys;
    IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;

    SET @sql = N'';
    SELECT @sql += N'DROP VIEW ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' + CHAR(13) + CHAR(10)
    FROM sys.views WHERE is_ms_shipped = 0;
    IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;

    SET @sql = N'';
    SELECT @sql += N'DROP PROCEDURE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' + CHAR(13) + CHAR(10)
    FROM sys.procedures WHERE is_ms_shipped = 0;
    IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;

    SET @sql = N'';
    SELECT @sql += N'DROP FUNCTION ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' + CHAR(13) + CHAR(10)
    FROM sys.objects
    WHERE type IN (N'FN', N'IF', N'TF', N'FS', N'FT') AND is_ms_shipped = 0;
    IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;

    SET @sql = N'';
    SELECT @sql += N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' + CHAR(13) + CHAR(10)
    FROM sys.tables WHERE is_ms_shipped = 0;
    IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE is_ms_shipped = 0)
        BREAK;
END

SET @sql = N'';
SELECT @sql += N'DROP SCHEMA ' + QUOTENAME(name) + N';' + CHAR(13) + CHAR(10)
FROM sys.schemas
WHERE name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
  AND schema_id < 16384
  AND NOT EXISTS (SELECT 1 FROM sys.objects WHERE schema_id = sys.schemas.schema_id);
IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;
'@
}

function Build-And-Publish {
    if ($SkipBuild -and $SkipPublish) { return }

    Push-Location $RepositoryRoot
    try {
        $solution = Join-Path $RepositoryRoot 'OpenModulePlatform.slnx'
        if (-not $SkipBuild) {
            Write-Step 'Building OpenModulePlatform'
            Invoke-NativeChecked dotnet 'restore' $solution
            Invoke-NativeChecked dotnet 'build' $solution '-c' 'Release' '--no-restore'
        }

        if (-not $SkipPublish) {
            Write-Step 'Publishing OpenModulePlatform'
            & (Join-Path $RepositoryRoot 'publish-all.ps1') -Configuration Release -OutputRoot $script:publishRoot -Restore -CleanOutput
            if ($LASTEXITCODE -ne 0) {
                throw "publish-all.ps1 failed with exit code $($LASTEXITCODE)."
            }

            $deployments = @(
                @{ Source = 'OpenModulePlatform.Portal'; Destination = 'Sites\Portal' },
                @{ Source = 'OpenModulePlatform.HostAgent.WindowsService'; Destination = 'Services\HostAgent' },
                @{ Source = 'OpenModulePlatform.WorkerManager.WindowsService'; Destination = 'Services\WorkerManager' },
                @{ Source = 'OpenModulePlatform.WorkerProcessHost'; Destination = 'Services\WorkerProcessHost' }
            )

            foreach ($deployment in $deployments) {
                $sourcePath = Join-Path $script:publishRoot $deployment.Source
                $destinationPath = Join-Path $RuntimeRoot $deployment.Destination
                New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
                Invoke-RobocopyChecked -Source $sourcePath -Destination $destinationPath
            }
        }
    }
    finally {
        Pop-Location
    }
}

function Run-SqlInstall {
    if ($SkipSql) { return }

    Write-Step 'Running OMP SQL scripts'
    Invoke-SqlFile -Path (Join-Path $RepositoryRoot 'sql\1-setup-openmoduleplatform.sql') -TargetDatabase $Database
    Invoke-SqlFileWithBootstrapPrincipal -Path (Join-Path $RepositoryRoot 'sql\2-initialize-openmoduleplatform.sql')
    Invoke-SqlFile -Path (Join-Path $RepositoryRoot 'OpenModulePlatform.Portal\sql\1-setup-omp-portal.sql') -TargetDatabase $Database
    Invoke-SqlFileWithBootstrapPrincipal -Path (Join-Path $RepositoryRoot 'OpenModulePlatform.Portal\sql\2-initialize-omp-portal.sql')
}

function Write-RuntimeConfig {
    Write-Step 'Writing local runtime configuration'
    & (Join-Path $RepositoryRoot 'scripts\write-local-runtime-config.ps1') `
        -RuntimeRoot $RuntimeRoot `
        -SqlServer $SqlServer `
        -Database $Database `
        -HostKey $HostKey `
        -Overwrite
}

function Ensure-IisPortal {
    if ($SkipIis) { return }
    if (-not (Get-Module -ListAvailable -Name WebAdministration)) {
        Write-Warning 'WebAdministration module not available. Skipping IIS configuration.'
        return
    }

    Write-Step 'Ensuring IIS Portal site/app pool'
    Import-Module WebAdministration

    $portalPath = Join-Path $RuntimeRoot 'Sites\Portal'
    New-Item -ItemType Directory -Path $portalPath -Force | Out-Null

    if (-not (Test-Path 'IIS:\AppPools\OMP_Portal')) {
        New-WebAppPool -Name 'OMP_Portal' | Out-Null
    }
    Set-ItemProperty 'IIS:\AppPools\OMP_Portal' -Name managedRuntimeVersion -Value ''

    if (-not (Test-Path "IIS:\Sites\$IisSiteName")) {
        New-Website -Name $IisSiteName -Port $IisPort -PhysicalPath $portalPath -ApplicationPool 'OMP_Portal' | Out-Null
    }
    else {
        Set-ItemProperty "IIS:\Sites\$IisSiteName" -Name physicalPath -Value $portalPath
        Set-ItemProperty "IIS:\Sites\$IisSiteName" -Name applicationPool -Value 'OMP_Portal'
    }
}

function Ensure-WindowsServices {
    if ($SkipServices) { return }

    Write-Step 'Ensuring Windows services'
    $hostAgentExe = Join-Path $RuntimeRoot 'Services\HostAgent\OpenModulePlatform.HostAgent.WindowsService.exe'
    $workerManagerExe = Join-Path $RuntimeRoot 'Services\WorkerManager\OpenModulePlatform.WorkerManager.WindowsService.exe'

    $serviceSpecs = @(
        @{ Name = 'OpenModulePlatform.HostAgent'; DisplayName = 'OpenModulePlatform HostAgent'; Exe = $hostAgentExe },
        @{ Name = 'OpenModulePlatform.WorkerManager'; DisplayName = 'OpenModulePlatform WorkerManager'; Exe = $workerManagerExe }
    )

    foreach ($spec in $serviceSpecs) {
        if (-not (Test-Path -LiteralPath $spec.Exe)) {
            Write-Warning "Service executable missing, skipping service creation: $($spec.Exe)"
            continue
        }

        $service = Get-Service -Name $spec.Name -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            New-Service -Name $spec.Name -DisplayName $spec.DisplayName -BinaryPathName ('"{0}"' -f $spec.Exe) -StartupType Automatic | Out-Null
            Write-Host "Created service: $($spec.Name)"
        }
    }
}

function Remove-RuntimeFiles {
    if (-not $RemoveRuntimeFiles) { return }

    if (-not (Confirm-LocalAction "Remove runtime files under '$RuntimeRoot'?")) {
        return
    }

    Write-Step 'Removing runtime folders'
    foreach ($relativePath in @('Sites\Portal', 'Services\HostAgent', 'Services\WorkerManager', 'Services\WorkerProcessHost', 'Publish\OMP')) {
        $path = Join-Path $RuntimeRoot $relativePath
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Continue
            Write-Host "Removed: $path"
        }
    }
}

function Install-LocalOmp {
    Stop-LocalRuntime
    Ensure-Database
    if ($ClearDatabaseObjects) { Clear-DatabaseUserObjects }
    Build-And-Publish
    Write-RuntimeConfig
    Run-SqlInstall
    Ensure-IisPortal
    Ensure-WindowsServices
    Start-LocalRuntime
}

function Uninstall-LocalOmp {
    Stop-LocalRuntime
    if ($DropDatabase) {
        if (-not (Confirm-LocalAction "Drop database '$Database' on '$SqlServer'?")) {
            throw 'Database drop was not confirmed.'
        }
        Invoke-SqlText -TargetDatabase master -Query @"
IF DB_ID(N'$Database') IS NOT NULL
BEGIN
    ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$Database];
END
"@
    }
    elseif ($ClearDatabaseObjects) {
        Clear-DatabaseUserObjects
    }
    Remove-RuntimeFiles
}

function Reinstall-LocalOmp {
    Stop-LocalRuntime
    if ($DropDatabase) {
        Ensure-Database
    }
    elseif ($ClearDatabaseObjects) {
        Clear-DatabaseUserObjects
        Ensure-Database
    }
    else {
        Ensure-Database
    }
    Remove-RuntimeFiles
    Build-And-Publish
    Write-RuntimeConfig
    Run-SqlInstall
    Ensure-IisPortal
    Ensure-WindowsServices
    Start-LocalRuntime
}

function Show-Menu {
    Write-Host ''
    Write-Host 'OpenModulePlatform local environment'
    Write-Host '1. Install / update local OMP runtime'
    Write-Host '2. Uninstall / reset local OMP runtime'
    Write-Host '3. Reinstall: uninstall/reset first, then install'
    Write-Host 'Q. Quit'
    $choice = Read-Host 'Select action'

    switch ($choice) {
        '1' { return 'Install' }
        '2' { return 'Uninstall' }
        '3' { return 'Reinstall' }
        default { return 'Quit' }
    }
}

if ($Action -eq 'Menu') {
    $Action = Show-Menu
}

switch ($Action) {
    'Install' { Install-LocalOmp }
    'Uninstall' { Uninstall-LocalOmp }
    'Reinstall' { Reinstall-LocalOmp }
    default { Write-Host 'No action selected.' }
}
