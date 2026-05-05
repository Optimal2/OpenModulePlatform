# File: scripts/manage-local-install.ps1
[CmdletBinding()]
param(
    [ValidateSet('Menu', 'Install', 'Uninstall', 'Reinstall')]
    [string]$Action = 'Menu',

    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$RuntimeRoot = 'E:\OMP',
    [string]$SqlServer = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string[]]$BootstrapPortalAdminPrincipal = @("$env:USERDOMAIN\$env:USERNAME"),
    [string]$DatabaseOwnerPrincipal = '',
    [string]$HostKey = 'sample-host',
    [string]$IisSiteName = 'OpenModulePlatform',
    [int]$IisPort = 8088,
    [switch]$CreateDatabase,
    [switch]$DropDatabase,
    [switch]$GrantDatabaseOwnerAccess,
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
$script:appPools = @('OMP_Portal', 'OMP_Auth')
$script:publishRoot = Join-Path $RuntimeRoot 'Publish\OMP'
$script:appcmdPath = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'

function Write-Step {
    param([string]$Message)
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Confirm-LocalAction {
    param([string]$Message)
    if ($Yes) { return $true }
    $answer = Read-Host "$Message [y/N]"
    # Accept Swedish j/ja as well as English y/yes for local developer prompts.
    return $answer -match '^(y|yes|j|ja)$'
}

function Require-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found in PATH."
    }
}

function Test-AppCmdAvailable {
    if (Test-Path -LiteralPath $script:appcmdPath) {
        return $true
    }

    Write-Warning "IIS appcmd.exe not found: $script:appcmdPath. Skipping IIS configuration."
    return $false
}

function Test-IsWindowsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-LocalInstallPrivileges {
    if (($SkipIis -and $SkipServices) -or (Test-IsWindowsAdministrator)) {
        return
    }

    throw "Local reinstall requires an elevated PowerShell when IIS or Windows services are enabled. Re-run as Administrator, or pass -SkipIis and -SkipServices for a non-IIS/service pass."
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

    $global:LASTEXITCODE = 0
}

function Invoke-AppCmdChecked {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    if (-not (Test-AppCmdAvailable)) {
        return
    }

    Invoke-NativeChecked $script:appcmdPath @Arguments
}

function Invoke-AppCmdOptional {
    param(
        [int[]]$IgnoredExitCodes = @(0),
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
    )

    if (-not (Test-AppCmdAvailable)) {
        return
    }

    Write-Host "> $script:appcmdPath $($Arguments -join ' ')"
    & $script:appcmdPath @Arguments
    if ($IgnoredExitCodes -notcontains $LASTEXITCODE) {
        throw "Command failed with exit code $($LASTEXITCODE): $script:appcmdPath $($Arguments -join ' ')"
    }

    $global:LASTEXITCODE = 0
}

function Get-IisAppPoolState {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Test-AppCmdAvailable)) {
        return $null
    }

    $output = & $script:appcmdPath list apppool "/name:$Name" 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $output) {
        $global:LASTEXITCODE = 0
        return $null
    }

    $global:LASTEXITCODE = 0
    $text = $output -join "`n"
    if ($text -match 'state:([^,\)]+)') {
        return $Matches[1]
    }

    return $null
}

function Test-IisAppPool {
    param([Parameter(Mandatory = $true)][string]$Name)

    $state = Get-IisAppPoolState -Name $Name
    return $null -ne $state
}

function Test-IisSite {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Test-AppCmdAvailable)) {
        return $false
    }

    $output = & $script:appcmdPath list site "/name:$Name" 2>$null
    $exists = $LASTEXITCODE -eq 0 -and $null -ne $output
    $global:LASTEXITCODE = 0
    return $exists
}

function Invoke-SqlText {
    param(
        [Parameter(Mandatory = $true)][string]$Query,
        [string]$TargetDatabase = 'master'
    )

    Require-Command sqlcmd
    $temp = [System.IO.Path]::GetTempFileName()
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

    $bootstrapPrincipals = @(Get-BootstrapPortalAdminPrincipals)
    $primaryBootstrapPrincipal = $bootstrapPrincipals[0]

    if ([string]::IsNullOrWhiteSpace($primaryBootstrapPrincipal) -or $primaryBootstrapPrincipal -like 'REPLACE_ME*') {
        throw 'BootstrapPortalAdminPrincipal must contain at least one actual Windows user or group, not REPLACE_ME.'
    }

    $escapedPrincipal = $primaryBootstrapPrincipal.Replace("'", "''")
    $content = Get-Content -LiteralPath $Path -Raw
    $bootstrapPrincipalToken = '__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__'
    $sqlcmdVariableToken = '$(BootstrapPortalAdminPrincipal)'
    if ($content.Contains($bootstrapPrincipalToken)) {
        $content = $content.Replace($bootstrapPrincipalToken, $escapedPrincipal)
    }
    elseif ($content.Contains($sqlcmdVariableToken)) {
        $content = $content.Replace($sqlcmdVariableToken, $escapedPrincipal)
    }
    else {
        $pattern = "DECLARE\s+@BootstrapPortalAdminPrincipal\s+nvarchar\(256\)\s*=\s*N'REPLACE_ME\\UserOrGroup';"
        $replacement = "DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = N'$escapedPrincipal';"
        $content = [System.Text.RegularExpressions.Regex]::Replace($content, $pattern, $replacement)
    }

    if ($content -like '*REPLACE_ME\UserOrGroup*' -or $content.Contains($bootstrapPrincipalToken) -or $content.Contains($sqlcmdVariableToken)) {
        throw "Failed to patch bootstrap principal in SQL file: $Path"
    }

    $temp = [System.IO.Path]::GetTempFileName()
    Set-Content -LiteralPath $temp -Value $content -Encoding UTF8
    return $temp
}

function Get-BootstrapPortalAdminPrincipals {
    $principals = @(@($BootstrapPortalAdminPrincipal) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique)

    if ($principals.Count -eq 0 -or ($principals | Where-Object { $_ -like 'REPLACE_ME*' })) {
        throw 'BootstrapPortalAdminPrincipal must contain at least one actual Windows user or group, not REPLACE_ME.'
    }

    return $principals
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

function Ensure-BootstrapPortalAdminPrincipals {
    if ($SkipSql) { return }

    $values = @()
    foreach ($principal in Get-BootstrapPortalAdminPrincipals) {
        $values += "(N'$($principal.Replace("'", "''"))')"
    }

    Invoke-SqlText -TargetDatabase $Database -Query @"
DECLARE @PortalAdminsRoleId int;
SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';

IF @PortalAdminsRoleId IS NULL
    THROW 51002, 'PortalAdmins role is missing after OMP initialization.', 1;

DECLARE @BootstrapPrincipals table(Principal nvarchar(256) NOT NULL PRIMARY KEY);
INSERT INTO @BootstrapPrincipals(Principal)
VALUES
$(($values -join ",`r`n"));

INSERT INTO omp.RolePrincipals(RoleId, PrincipalType, Principal)
SELECT @PortalAdminsRoleId, N'User', bp.Principal
FROM @BootstrapPrincipals bp
WHERE NOT EXISTS
(
    SELECT 1
    FROM omp.RolePrincipals rp
    WHERE rp.RoleId = @PortalAdminsRoleId
      AND rp.PrincipalType = N'User'
      AND rp.Principal = bp.Principal
);
"@
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

    if (-not $SkipIis) {
        foreach ($poolName in $script:appPools) {
            $state = Get-IisAppPoolState -Name $poolName
            if ($null -eq $state) {
                Write-Host "App pool not found: $poolName"
                continue
            }
            if ($state -eq 'Stopped') {
                Write-Host "App pool already stopped: $poolName"
                continue
            }
            Write-Host "Stopping app pool: $poolName"
            Invoke-AppCmdChecked stop apppool "/apppool.name:$poolName"
        }
    }
}

function Start-LocalRuntime {
    Write-Step 'Starting local OMP runtime'

    if (-not $SkipIis) {
        foreach ($poolName in $script:appPools) {
            $state = Get-IisAppPoolState -Name $poolName
            if ($null -eq $state) { continue }
            if ($state -eq 'Started') {
                Write-Host "App pool already started: $poolName"
                continue
            }
            Write-Host "Starting app pool: $poolName"
            Invoke-AppCmdChecked start apppool "/apppool.name:$poolName"
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
    Write-Step 'Checking database'

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
    elseif ($CreateDatabase) {
        Invoke-SqlText -TargetDatabase master -Query @"
IF DB_ID(N'$Database') IS NULL
    CREATE DATABASE [$Database];
"@
    }
    else {
        Invoke-SqlText -TargetDatabase master -Query @"
IF DB_ID(N'$Database') IS NULL
    RAISERROR('Database does not exist. Create it before running the installer, or pass -CreateDatabase for explicit local bootstrap.', 11, 1);
"@
    }

    if ($GrantDatabaseOwnerAccess -and -not [string]::IsNullOrWhiteSpace($DatabaseOwnerPrincipal)) {
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
                @{ Source = 'OpenModulePlatform.Auth'; Destination = 'WebApps\auth' },
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
    Ensure-BootstrapPortalAdminPrincipals
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

    Write-Step 'Ensuring IIS Portal site/app pool'
    $portalPath = Join-Path $RuntimeRoot 'Sites\Portal'
    $authPath = Join-Path $RuntimeRoot 'WebApps\auth'
    New-Item -ItemType Directory -Path $portalPath -Force | Out-Null
    New-Item -ItemType Directory -Path $authPath -Force | Out-Null

    if (-not (Test-AppCmdAvailable)) {
        return
    }

    if (-not (Test-IisAppPool -Name 'OMP_Portal')) {
        Invoke-AppCmdChecked add apppool '/name:OMP_Portal'
    }
    Invoke-AppCmdChecked set apppool '/apppool.name:OMP_Portal' '/managedRuntimeVersion:'

    if (-not (Test-IisAppPool -Name 'OMP_Auth')) {
        Invoke-AppCmdChecked add apppool '/name:OMP_Auth'
    }
    Invoke-AppCmdChecked set apppool '/apppool.name:OMP_Auth' '/managedRuntimeVersion:'

    if (-not (Test-IisSite -Name $IisSiteName)) {
        Invoke-AppCmdChecked add site "/name:$IisSiteName" ("/bindings:http/*:{0}:" -f $IisPort) "/physicalPath:$portalPath"
    }
    else {
        Invoke-AppCmdChecked set vdir "$IisSiteName/" "/physicalPath:$portalPath"
    }

    Invoke-AppCmdChecked set app "$IisSiteName/" '/applicationPool:OMP_Portal'
    Set-IisAuthentication -Location $IisSiteName -AnonymousEnabled $true

    Invoke-AppCmdOptional -IgnoredExitCodes @(0, 50, 1168) delete app "$IisSiteName/auth"
    Invoke-AppCmdChecked add app "/site.name:$IisSiteName" '/path:/auth' "/physicalPath:$authPath" '/applicationPool:OMP_Auth'
    Set-IisAuthentication -Location "$IisSiteName/auth" -AnonymousEnabled $true -WindowsEnabled $true
}

function Set-IisAuthentication {
    param(
        [Parameter(Mandatory = $true)][string]$Location,
        [Parameter(Mandatory = $true)][bool]$AnonymousEnabled,
        [object]$WindowsEnabled = $null
    )

    $anonymousValue = $AnonymousEnabled.ToString().ToLowerInvariant()
    if ($null -eq $WindowsEnabled) {
        $WindowsEnabled = -not $AnonymousEnabled
    }

    $windowsValue = ([bool]$WindowsEnabled).ToString().ToLowerInvariant()

    Invoke-AppCmdOptional set config $Location `
        '/section:system.webServer/security/authentication/anonymousAuthentication' `
        "/enabled:$anonymousValue" `
        '/userName:' `
        '/password:' `
        '/commit:apphost'

    Invoke-AppCmdOptional set config $Location `
        '/section:system.webServer/security/authentication/windowsAuthentication' `
        "/enabled:$windowsValue" `
        '/commit:apphost'
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
    foreach ($relativePath in @('Sites\Portal', 'WebApps\auth', 'Services\HostAgent', 'Services\WorkerManager', 'Services\WorkerProcessHost', 'Publish\OMP')) {
        $path = Join-Path $RuntimeRoot $relativePath
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Continue
            Write-Host "Removed: $path"
        }
    }
}

function Install-LocalOmp {
    Assert-LocalInstallPrivileges
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
    Assert-LocalInstallPrivileges
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
    Assert-LocalInstallPrivileges
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
