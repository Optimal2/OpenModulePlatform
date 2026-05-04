# File: scripts/install-local-examples.ps1
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$RuntimeRoot = 'E:\OMP',
    [string]$SqlServer = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string]$IisSiteName = 'OpenModulePlatform',
    [int]$IisPort = 8088,
    [string]$OpenDocViewerRepositoryRoot = '',
    [string]$OpenDocViewerAppPath = 'opendocviewer',
    [switch]$SkipBuild,
    [switch]$SkipPublish,
    [switch]$SkipSql,
    [switch]$SkipIis,
    [switch]$SkipOpenDocViewer,
    [switch]$SkipOpenDocViewerBuild,
    [switch]$SkipExampleService,
    [switch]$SkipStartExampleService,
    [switch]$SkipRuntimeServices,
    [switch]$SkipStartRuntimeServices,
    [string]$RunAsUser = '',
    [string]$RunAsPassword = '',
    [switch]$KeepLegacyAppPoolDatabaseUsers,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:publishRoot = Join-Path $RuntimeRoot 'Publish\OMP'
$script:webAppsRoot = Join-Path $RuntimeRoot 'WebApps'
$script:portalPath = Join-Path $RuntimeRoot 'Sites\Portal'
$script:authAppPath = Join-Path $RuntimeRoot 'WebApps\auth'
$script:servicesRoot = Join-Path $RuntimeRoot 'Services'
$script:appcmdPath = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
$script:exampleServiceName = 'OpenModulePlatform.Service.ExampleServiceAppModule'
$script:hostAgentServiceName = 'OpenModulePlatform.HostAgent'
$script:workerManagerServiceName = 'OpenModulePlatform.WorkerManager'
$script:resolvedRunAsUser = ''
$script:resolvedRunAsPasswordSecure = $null
$script:resolvedRunAsCredential = $null

function Write-Step {
    param([string]$Message)
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Confirm-LocalAction {
    param([string]$Message)
    if ($Yes) { return $true }
    $answer = Read-Host "$Message [y/N]"
    return $answer -match '^(?i)(y|yes|j|ja)$'
}

function Resolve-WindowsAccountName {
    param([string]$Account)

    if ([string]::IsNullOrWhiteSpace($Account)) {
        return ''
    }

    $trimmed = $Account.Trim()
    if ($trimmed.StartsWith('.\', [System.StringComparison]::Ordinal)) {
        return "$env:COMPUTERNAME\$($trimmed.Substring(2))"
    }

    if ($trimmed.IndexOf('\') -lt 0 -and $trimmed.IndexOf('@') -lt 0) {
        return "$env:COMPUTERNAME\$trimmed"
    }

    return $trimmed
}

function ConvertFrom-SecureStringToPlainText {
    param([Parameter(Mandatory = $true)][Security.SecureString]$SecureString)

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Initialize-RunAsIdentity {
    $account = $RunAsUser
    if ([string]::IsNullOrWhiteSpace($account) -and -not $Yes) {
        $account = Read-Host 'Windows account for IIS app pools and the example service (blank keeps the current app pool identity)'
    }

    if ([string]::IsNullOrWhiteSpace($account)) {
        return
    }

    $script:resolvedRunAsUser = Resolve-WindowsAccountName -Account $account
    if ([string]::IsNullOrWhiteSpace($RunAsPassword)) {
        $script:resolvedRunAsPasswordSecure = Read-Host "Password for $script:resolvedRunAsUser" -AsSecureString
    }
    else {
        $script:resolvedRunAsPasswordSecure = ConvertTo-SecureString -String $RunAsPassword -AsPlainText -Force
    }

    if ($null -eq $script:resolvedRunAsPasswordSecure) {
        throw "A password is required when RunAsUser is set."
    }

    $script:resolvedRunAsCredential = New-Object -TypeName System.Management.Automation.PSCredential -ArgumentList $script:resolvedRunAsUser, $script:resolvedRunAsPasswordSecure
}

function Clear-RunAsIdentity {
    $script:resolvedRunAsPasswordSecure = $null
    $script:resolvedRunAsCredential = $null
}

function Get-RunAsPasswordPlainText {
    if ($null -eq $script:resolvedRunAsPasswordSecure) {
        return ''
    }

    return ConvertFrom-SecureStringToPlainText -SecureString $script:resolvedRunAsPasswordSecure
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
    & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($Arguments -join ' ')"
    }
}

function Invoke-NativeCheckedRedacted {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string[]]$DisplayArguments
    )

    Write-Host "> $FilePath $($DisplayArguments -join ' ')"
    & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($DisplayArguments -join ' ')"
    }
}

function Invoke-RobocopyChecked {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Assert-PathUnderRoot -Root $RuntimeRoot -Path $Destination
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    $options = @('/MIR', '/R:2', '/W:2', '/NFL', '/NDL', '/NP')
    Write-Host "> robocopy $Source $Destination $($options -join ' ')"
    & robocopy $Source $Destination @options
    $exitCode = $LASTEXITCODE
    if ($exitCode -ge 8) {
        throw "robocopy failed with exit code ${exitCode}: $Source -> $Destination"
    }
}

function Assert-PathUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $pathFull = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    if (-not $pathFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to mirror files outside runtime root. Root: $rootFull Path: $pathFull"
    }
}

function Get-OmpConnectionString {
    return "Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true;"
}

function Invoke-SqlFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "SQL file does not exist: $Path"
    }

    Require-Command sqlcmd
    Invoke-NativeChecked sqlcmd '-S' $SqlServer '-d' $Database '-E' '-b' '-i' $Path
}

function Invoke-SqlText {
    param([Parameter(Mandatory = $true)][string]$Query)

    Require-Command sqlcmd
    $temp = [System.IO.Path]::GetTempFileName()
    Set-Content -LiteralPath $temp -Value $Query -Encoding UTF8
    try {
        Invoke-NativeChecked sqlcmd '-S' $SqlServer '-d' $Database '-E' '-b' '-i' $temp
    }
    finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

function Test-IsWindowsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-OpenDocViewerRoot {
    if ($SkipOpenDocViewer) { return '' }

    if (-not [string]::IsNullOrWhiteSpace($OpenDocViewerRepositoryRoot)) {
        return [System.IO.Path]::GetFullPath($OpenDocViewerRepositoryRoot)
    }

    $workspaceRoot = Split-Path -Parent $RepositoryRoot
    $candidate = Join-Path $workspaceRoot 'OpenDocViewer'
    if (Test-Path -LiteralPath (Join-Path $candidate 'package.json')) {
        return [System.IO.Path]::GetFullPath($candidate)
    }

    if ($Yes) {
        Write-Warning 'OpenDocViewer repo was not found next to OpenModulePlatform. Skipping ODV deployment.'
        return ''
    }

    $entered = Read-Host 'OpenDocViewer repository path (blank skips ODV deployment)'
    if ([string]::IsNullOrWhiteSpace($entered)) {
        return ''
    }

    return [System.IO.Path]::GetFullPath($entered)
}

function Publish-OpenModulePlatform {
    if ($SkipBuild -and $SkipPublish) { return }

    Push-Location $RepositoryRoot
    try {
        $solution = Join-Path $RepositoryRoot 'OpenModulePlatform.slnx'
        if (-not (Test-Path -LiteralPath $solution)) {
            $solution = Join-Path $RepositoryRoot 'OpenModulePlatform.sln'
        }

        if (-not $SkipBuild) {
            Write-Step 'Building OpenModulePlatform'
            Invoke-NativeChecked dotnet 'restore' $solution
            Invoke-NativeChecked dotnet 'build' $solution '-c' 'Release' '--no-restore'
        }

        if (-not $SkipPublish) {
            Write-Step 'Publishing OpenModulePlatform web examples'
            & (Join-Path $RepositoryRoot 'publish-all.ps1') `
                -Configuration Release `
                -OutputRoot $script:publishRoot `
                -Restore `
                -CleanOutput

            $exitCode = $LASTEXITCODE
            if ($exitCode -ne 0) {
                throw "publish-all.ps1 failed with exit code ${exitCode}."
            }
        }
    }
    finally {
        Pop-Location
    }
}

function Publish-OpenDocViewer {
    param([string]$OpenDocViewerRoot)

    if ($SkipOpenDocViewer -or [string]::IsNullOrWhiteSpace($OpenDocViewerRoot)) {
        return ''
    }

    if (-not (Test-Path -LiteralPath (Join-Path $OpenDocViewerRoot 'package.json'))) {
        throw "OpenDocViewer package.json was not found in: $OpenDocViewerRoot"
    }

    if (-not $SkipOpenDocViewerBuild) {
        Write-Step 'Building OpenDocViewer'
        Require-Command npm
        Push-Location $OpenDocViewerRoot
        try {
            if (-not (Test-Path -LiteralPath (Join-Path $OpenDocViewerRoot 'node_modules'))) {
                Invoke-NativeChecked npm 'ci'
            }

            Invoke-NativeChecked npm 'run' 'build'
        }
        finally {
            Pop-Location
        }
    }

    $distPath = Join-Path $OpenDocViewerRoot 'dist'
    if (-not (Test-Path -LiteralPath $distPath)) {
        throw "OpenDocViewer dist folder was not found. Build ODV first or remove -SkipOpenDocViewerBuild. Path: $distPath"
    }

    return $distPath
}

function Deploy-PublishedOutputs {
    param([string]$OpenDocViewerDistPath)

    if ($SkipPublish) { return }

    Write-Step 'Deploying published web applications'
    Stop-IisAppPoolsForDeployment

    $deployments = @(
        @{ Source = 'OpenModulePlatform.Auth'; Destination = 'WebApps\auth' },
        @{ Source = 'OpenModulePlatform.Portal'; Destination = 'Sites\Portal' },
        @{ Source = 'OpenModulePlatform.Web.ExampleWebAppModule'; Destination = 'WebApps\ExampleWebAppModule' },
        @{ Source = 'OpenModulePlatform.Web.ExampleWebAppBlazorModule'; Destination = 'WebApps\ExampleWebAppBlazorModule' },
        @{ Source = 'OpenModulePlatform.Web.ExampleServiceAppModule'; Destination = 'WebApps\ExampleServiceAppModule' },
        @{ Source = 'OpenModulePlatform.Web.ExampleWorkerAppModule'; Destination = 'WebApps\ExampleWorkerAppModule' },
        @{ Source = 'OpenModulePlatform.Web.iFrameWebAppModule'; Destination = 'WebApps\iFrameWebAppModule' }
    )

    foreach ($deployment in $deployments) {
        $sourcePath = Join-Path $script:publishRoot $deployment.Source
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            throw "Published output was not found: $sourcePath"
        }

        $destinationPath = Join-Path $RuntimeRoot $deployment.Destination
        Invoke-RobocopyChecked -Source $sourcePath -Destination $destinationPath
    }

    $workerArtifactSourcePath = Join-Path $script:publishRoot 'OpenModulePlatform.Worker.ExampleWorkerAppModule'
    if (-not (Test-Path -LiteralPath $workerArtifactSourcePath)) {
        throw "Published example worker artifact output was not found: $workerArtifactSourcePath"
    }

    $workerArtifactDestinationPath = Join-Path $RuntimeRoot 'ArtifactStore\example-workerapp\worker\1.0.0'
    Invoke-RobocopyChecked -Source $workerArtifactSourcePath -Destination $workerArtifactDestinationPath

    if (-not $SkipRuntimeServices) {
        Stop-WindowsServiceIfInstalled -Name $script:workerManagerServiceName
        Stop-WindowsServiceIfInstalled -Name $script:hostAgentServiceName

        $runtimeServiceDeployments = @(
            @{ Source = 'OpenModulePlatform.HostAgent.WindowsService'; Destination = 'Services\HostAgent' },
            @{ Source = 'OpenModulePlatform.WorkerManager.WindowsService'; Destination = 'Services\WorkerManager' },
            @{ Source = 'OpenModulePlatform.WorkerProcessHost'; Destination = 'Services\WorkerProcessHost' }
        )

        foreach ($deployment in $runtimeServiceDeployments) {
            $sourcePath = Join-Path $script:publishRoot $deployment.Source
            if (-not (Test-Path -LiteralPath $sourcePath)) {
                throw "Published runtime service output was not found: $sourcePath"
            }

            $destinationPath = Join-Path $RuntimeRoot $deployment.Destination
            Invoke-RobocopyChecked -Source $sourcePath -Destination $destinationPath
        }
    }

    if (-not $SkipExampleService) {
        Stop-WindowsServiceIfInstalled -Name $script:exampleServiceName

        $serviceSourcePath = Join-Path $script:publishRoot $script:exampleServiceName
        if (-not (Test-Path -LiteralPath $serviceSourcePath)) {
            throw "Published example service output was not found: $serviceSourcePath"
        }

        $serviceDestinationPath = Join-Path $script:servicesRoot 'ExampleServiceAppModule'
        Invoke-RobocopyChecked -Source $serviceSourcePath -Destination $serviceDestinationPath
    }

    if (-not [string]::IsNullOrWhiteSpace($OpenDocViewerDistPath)) {
        $odvDestination = Join-Path $script:webAppsRoot $OpenDocViewerAppPath
        Invoke-RobocopyChecked -Source $OpenDocViewerDistPath -Destination $odvDestination
    }
}

function Stop-IisAppPoolsForDeployment {
    if ($SkipIis -or -not (Test-Path -LiteralPath $script:appcmdPath)) {
        return
    }

    foreach ($pool in @('OMP_Auth', 'OMP_Portal', 'OMP_ExampleWebAppModule', 'OMP_ExampleWebAppBlazorModule', 'OMP_ExampleServiceAppModule', 'OMP_ExampleWorkerAppModule', 'OMP_iFrameWebAppModule', 'OMP_OpenDocViewer')) {
        if (Test-IisAppPool -Name $pool) {
            Invoke-AppCmdOptional stop apppool "/apppool.name:$pool"
        }
    }
}

function Write-ExampleRuntimeConfig {
    Write-Step 'Writing example runtime configuration overrides'

    $connectionString = Get-OmpConnectionString
    $odvBaseUrl = '/' + $OpenDocViewerAppPath.Trim('/') + '/'
    $odvSampleUrl = $odvBaseUrl + 'sample.pdf'
    $dataProtectionKeyPath = Join-Path $RuntimeRoot 'DataProtectionKeys'
    New-Item -ItemType Directory -Path $dataProtectionKeyPath -Force | Out-Null

    $ompAuth = [ordered]@{
        CookieName = '.OpenModulePlatform.Auth'
        LoginPath = '/auth/login'
        AccessDeniedPath = '/status/403'
        ApplicationName = 'OpenModulePlatform'
        DataProtectionKeyPath = $dataProtectionKeyPath
    }

    if (Test-Path -LiteralPath $script:portalPath) {
        $portalOverride = [ordered]@{
            ConnectionStrings = [ordered]@{
                OmpDb = $connectionString
            }
            OmpAuth = $ompAuth
            Portal = [ordered]@{
                PortalTopBar = [ordered]@{
                    PortalBaseUrl = '/'
                }
            }
        }

        $target = Join-Path $script:portalPath 'appsettings.Production.json'
        $json = $portalOverride | ConvertTo-Json -Depth 8
        Set-Content -LiteralPath $target -Value $json -Encoding UTF8
        Write-Host "Wrote: $target"
    }

    if (Test-Path -LiteralPath $script:authAppPath) {
        $authOverride = [ordered]@{
            ConnectionStrings = [ordered]@{
                OmpDb = $connectionString
            }
            OmpAuth = $ompAuth
        }

        $target = Join-Path $script:authAppPath 'appsettings.Production.json'
        $json = $authOverride | ConvertTo-Json -Depth 8
        Set-Content -LiteralPath $target -Value $json -Encoding UTF8
        Write-Host "Wrote: $target"
    }

    $appFolders = @(
        'ExampleWebAppModule',
        'ExampleWebAppBlazorModule',
        'ExampleServiceAppModule',
        'ExampleWorkerAppModule',
        'iFrameWebAppModule'
    )

    foreach ($appFolder in $appFolders) {
        $appPath = Join-Path $script:webAppsRoot $appFolder
        if (-not (Test-Path -LiteralPath $appPath)) {
            continue
        }

        $override = [ordered]@{
            ConnectionStrings = [ordered]@{
                OmpDb = $connectionString
            }
            OmpAuth = $ompAuth
            Portal = [ordered]@{
                PortalTopBar = [ordered]@{
                    PortalBaseUrl = '/'
                }
            }
            OpenDocViewer = [ordered]@{
                BaseUrl = $odvBaseUrl
                SampleFileUrl = $odvSampleUrl
            }
        }

        $target = Join-Path $appPath 'appsettings.Production.json'
        $json = $override | ConvertTo-Json -Depth 8
        Set-Content -LiteralPath $target -Value $json -Encoding UTF8
        Write-Host "Wrote: $target"
    }

    if (-not $SkipExampleService) {
        $servicePath = Join-Path $script:servicesRoot 'ExampleServiceAppModule'
        if (Test-Path -LiteralPath $servicePath) {
            $override = [ordered]@{
                ConnectionStrings = [ordered]@{
                    OmpDb = $connectionString
                }
            }

            $target = Join-Path $servicePath 'appsettings.Production.json'
            $json = $override | ConvertTo-Json -Depth 8
            Set-Content -LiteralPath $target -Value $json -Encoding UTF8
            Write-Host "Wrote: $target"
        }
    }

    if (-not $SkipRuntimeServices) {
        $hostAgentPath = Join-Path $script:servicesRoot 'HostAgent'
        if (Test-Path -LiteralPath $hostAgentPath) {
            $hostAgentConfig = [ordered]@{
                ConnectionStrings = [ordered]@{
                    OmpDb = $connectionString
                }
                HostAgent = [ordered]@{
                    HostKey = 'sample-host'
                    HostName = $env:COMPUTERNAME
                    RefreshSeconds = 30
                    CentralArtifactRoot = (Join-Path $RuntimeRoot 'ArtifactStore')
                    LocalArtifactCacheRoot = (Join-Path $RuntimeRoot 'ArtifactCache')
                    ProvisionAppInstanceArtifacts = $true
                    ProvisionExplicitRequirements = $true
                    MaxArtifactsPerCycle = 100
                    EnableRpc = $true
                    RpcPipeName = ''
                    RpcRequestTimeoutSeconds = 60
                }
            }

            $target = Join-Path $hostAgentPath 'appsettings.Production.json'
            $json = $hostAgentConfig | ConvertTo-Json -Depth 8
            Set-Content -LiteralPath $target -Value $json -Encoding UTF8
            Write-Host "Wrote: $target"
        }

        $workerManagerPath = Join-Path $script:servicesRoot 'WorkerManager'
        if (Test-Path -LiteralPath $workerManagerPath) {
            $workerManagerConfig = [ordered]@{
                ConnectionStrings = [ordered]@{
                    OmpDb = $connectionString
                }
                WorkerManager = [ordered]@{
                    CatalogMode = 'OmpDatabase'
                    HostKey = 'sample-host'
                    HostName = $env:COMPUTERNAME
                    RefreshSeconds = 15
                    WorkerProcessPath = (Join-Path $script:servicesRoot 'WorkerProcessHost\OpenModulePlatform.WorkerProcessHost.exe')
                    StopTimeoutSeconds = 15
                    RestartDelaySeconds = 5
                    RestartWindowSeconds = 300
                    MaxRestartsPerWindow = 5
                    OmpDatabase = [ordered]@{
                        RuntimeKind = 'windows-worker-plugin'
                        RunningDesiredState = 1
                        UseHostArtifactCache = $true
                    }
                    HostAgentRpc = [ordered]@{
                        Enabled = $true
                        PipeName = ''
                        TimeoutSeconds = 60
                    }
                    Workers = @()
                }
            }

            $target = Join-Path $workerManagerPath 'appsettings.Production.json'
            $json = $workerManagerConfig | ConvertTo-Json -Depth 8
            Set-Content -LiteralPath $target -Value $json -Encoding UTF8
            Write-Host "Wrote: $target"
        }

        $workerProcessHostPath = Join-Path $script:servicesRoot 'WorkerProcessHost'
        if (Test-Path -LiteralPath $workerProcessHostPath) {
            $workerProcessHostConfig = [ordered]@{
                ConnectionStrings = [ordered]@{
                    OmpDb = $connectionString
                }
                WorkerProcess = [ordered]@{
                    AppInstanceId = '00000000-0000-0000-0000-000000000000'
                    WorkerInstanceId = '00000000-0000-0000-0000-000000000000'
                    WorkerInstanceKey = ''
                    WorkerTypeKey = ''
                    PluginAssemblyPath = ''
                    ShutdownEventName = ''
                    ConfigurationJson = ''
                }
            }

            $target = Join-Path $workerProcessHostPath 'appsettings.Production.json'
            $json = $workerProcessHostConfig | ConvertTo-Json -Depth 8
            Set-Content -LiteralPath $target -Value $json -Encoding UTF8
            Write-Host "Wrote: $target"
        }
    }
}

function Run-ExampleSql {
    if ($SkipSql) { return }

    Write-Step 'Running example SQL scripts'

    $sqlFiles = @(
        'examples\WebAppModule\Sql\1-setup-example-webapp.sql',
        'examples\WebAppModule\Sql\2-initialize-example-webapp.sql',
        'examples\WebAppBlazorModule\Sql\1-setup-example-webapp-blazor.sql',
        'examples\WebAppBlazorModule\Sql\2-initialize-example-webapp-blazor.sql',
        'examples\ServiceAppModule\sql\1-setup-example-serviceapp.sql',
        'examples\ServiceAppModule\sql\2-initialize-example-serviceapp.sql',
        'examples\WorkerAppModule\sql\1-setup-example-workerapp.sql',
        'examples\WorkerAppModule\sql\2-initialize-example-workerapp.sql',
        'OpenModulePlatform.Web.iFrameWebAppModule\Sql\1-setup-iframe-webapp.sql',
        'OpenModulePlatform.Web.iFrameWebAppModule\Sql\2-initialize-iframe-webapp.sql'
    )

    foreach ($relativePath in $sqlFiles) {
        Invoke-SqlFile -Path (Join-Path $RepositoryRoot $relativePath)
    }
}

function Ensure-RunAsDatabaseAccess {
    if ($SkipSql -or [string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) { return }

    Write-Step 'Ensuring database access for configured run-as account'

    $principal = $script:resolvedRunAsUser.Replace("'", "''")
    Invoke-SqlText -Query @"
DECLARE @principal sysname = N'$principal';
DECLARE @sql nvarchar(max);

IF SUSER_ID(@principal) IS NULL
BEGIN
    SET @sql = N'CREATE LOGIN ' + QUOTENAME(@principal) + N' FROM WINDOWS;';
    EXEC sys.sp_executesql @sql;
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

function Remove-LegacyAppPoolDatabaseUsers {
    if ($SkipSql -or $KeepLegacyAppPoolDatabaseUsers -or [string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) { return }

    Write-Step 'Removing legacy virtual app pool database users'

    $principals = @(
        'IIS APPPOOL\OMP_Portal',
        'IIS APPPOOL\OMP_Auth',
        'IIS APPPOOL\OMP_ExampleWebAppModule',
        'IIS APPPOOL\OMP_ExampleWebAppBlazorModule',
        'IIS APPPOOL\OMP_ExampleServiceAppModule',
        'IIS APPPOOL\OMP_ExampleWorkerAppModule',
        'IIS APPPOOL\OMP_iFrameWebAppModule',
        'IIS APPPOOL\OMP_OpenDocViewer'
    )

    $values = @()
    foreach ($principal in $principals) {
        $values += "(N'$($principal.Replace("'", "''"))')"
    }

    Invoke-SqlText -Query @"
DECLARE @Principals table(Principal sysname NOT NULL PRIMARY KEY);
INSERT INTO @Principals(Principal)
VALUES
$(($values -join ",`r`n"));

DECLARE @principal sysname;
DECLARE @sql nvarchar(max);

DECLARE principal_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT Principal FROM @Principals;

OPEN principal_cursor;
FETCH NEXT FROM principal_cursor INTO @principal;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF DATABASE_PRINCIPAL_ID(@principal) IS NOT NULL
    BEGIN
        IF IS_ROLEMEMBER(N'db_owner', @principal) = 1
        BEGIN
            EXEC sys.sp_droprolemember N'db_owner', @principal;
        END

        SET @sql = N'DROP USER ' + QUOTENAME(@principal) + N';';
        EXEC sys.sp_executesql @sql;
    END

    IF SUSER_ID(@principal) IS NOT NULL
    BEGIN
        SET @sql = N'DROP LOGIN ' + QUOTENAME(@principal) + N';';
        EXEC sys.sp_executesql @sql;
    END

    FETCH NEXT FROM principal_cursor INTO @principal;
END

CLOSE principal_cursor;
DEALLOCATE principal_cursor;
"@
}

function Require-AppCmd {
    if (-not (Test-Path -LiteralPath $script:appcmdPath)) {
        throw "IIS appcmd.exe was not found: $script:appcmdPath"
    }
}

function Invoke-AppCmdChecked {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    Require-AppCmd
    Invoke-NativeChecked $script:appcmdPath @Arguments
}

function Invoke-AppCmdOptional {
    param(
        [Parameter(ValueFromRemainingArguments = $true, Position = 0)][string[]]$Arguments,
        [int[]]$IgnoredExitCodes = @()
    )

    Require-AppCmd
    Write-Host "> $script:appcmdPath $($Arguments -join ' ')"
    $output = & $script:appcmdPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0 -and $IgnoredExitCodes -notcontains $exitCode) {
        if ($null -ne $output) {
            $output | ForEach-Object { Write-Host $_ }
        }

        Write-Warning "appcmd failed with exit code ${exitCode}: $($Arguments -join ' ')"
        return
    }

    if ($exitCode -eq 0 -and $null -ne $output) {
        $output | ForEach-Object { Write-Host $_ }
    }
}

function Test-IisAppPool {
    param([string]$Name)

    Require-AppCmd
    $output = & $script:appcmdPath list apppool "/name:$Name" 2>&1
    $null = $output
    return $LASTEXITCODE -eq 0
}

function Test-IisSite {
    param([string]$Name)

    Require-AppCmd
    $output = & $script:appcmdPath list site "/name:$Name" 2>&1
    $null = $output
    return $LASTEXITCODE -eq 0
}

function Get-IisAppName {
    param([string]$AppPath)

    Require-AppCmd
    $expected = "$IisSiteName/$AppPath"
    $output = & $script:appcmdPath list app 2>&1
    foreach ($line in @($output)) {
        $text = $line.ToString()
        if ($text -match '^APP "([^"]+)"') {
            $candidate = $Matches[1]
            if ([string]::Equals($candidate, $expected, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $candidate
            }
        }
    }

    return ''
}

function Ensure-IisAppPool {
    param([string]$Name)

    if (-not (Test-IisAppPool -Name $Name)) {
        Invoke-AppCmdChecked add apppool "/name:$Name"
    }

    Invoke-AppCmdChecked set apppool "/apppool.name:$Name" '/managedRuntimeVersion:'
    Invoke-AppCmdChecked set apppool "/apppool.name:$Name" '/processModel.loadUserProfile:true'

    if (-not [string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) {
        $runAsPasswordPlain = Get-RunAsPasswordPlainText
        try {
            $arguments = @(
                'set',
                'apppool',
                "/apppool.name:$Name",
                '/processModel.identityType:SpecificUser',
                "/processModel.userName:$script:resolvedRunAsUser",
                "/processModel.password:$runAsPasswordPlain"
            )
            $displayArguments = @(
                'set',
                'apppool',
                "/apppool.name:$Name",
                '/processModel.identityType:SpecificUser',
                "/processModel.userName:$script:resolvedRunAsUser",
                '/processModel.password:***'
            )
            Invoke-NativeCheckedRedacted -FilePath $script:appcmdPath -Arguments $arguments -DisplayArguments $displayArguments
        }
        finally {
            $runAsPasswordPlain = ''
        }
    }
}

function Ensure-IisWebApplication {
    param(
        [string]$AppPath,
        [string]$PhysicalPath,
        [string]$AppPoolName,
        [bool]$AnonymousEnabled
    )

    Ensure-IisAppPool -Name $AppPoolName
    Invoke-AppCmdOptional stop apppool "/apppool.name:$AppPoolName"

    $existingAppName = Get-IisAppName -AppPath $AppPath
    if (-not [string]::IsNullOrWhiteSpace($existingAppName)) {
        Invoke-AppCmdChecked delete app $existingAppName
    }

    Invoke-AppCmdChecked add app `
        "/site.name:$IisSiteName" `
        "/path:/$AppPath" `
        "/physicalPath:$PhysicalPath" `
        "/applicationPool:$AppPoolName"

    Set-IisAuthentication -Location "$IisSiteName/$AppPath" -AnonymousEnabled $AnonymousEnabled
}

function Set-IisAuthentication {
    param(
        [string]$Location,
        [bool]$AnonymousEnabled,
        [object]$WindowsEnabled = $null
    )

    $anonymousValue = $AnonymousEnabled.ToString().ToLowerInvariant()
    if ($null -eq $WindowsEnabled) {
        $WindowsEnabled = -not $AnonymousEnabled
    }

    $windowsEnabledBool = [bool]$WindowsEnabled
    $windowsValue = $windowsEnabledBool.ToString().ToLowerInvariant()

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

    if ($windowsEnabledBool) {
        Set-IisWindowsAuthenticationProviders -Location $Location
    }
}

function Set-IisWindowsAuthenticationProviders {
    param([string]$Location)

    # Keep Windows-auth enabled locations on the same provider list. Mixing
    # Negotiate+NTLM with NTLM-only child apps can make browsers issue a new
    # Windows-auth challenge when navigating between apps.
    Invoke-AppCmdOptional -IgnoredExitCodes @(183, 4312) set config $Location `
        '/section:system.webServer/security/authentication/windowsAuthentication' `
        "/-providers.[value='Negotiate']" `
        '/commit:apphost'

    Invoke-AppCmdOptional -IgnoredExitCodes @(183, 4312) set config $Location `
        '/section:system.webServer/security/authentication/windowsAuthentication' `
        "/-providers.[value='NTLM']" `
        '/commit:apphost'

    Invoke-AppCmdChecked set config $Location `
        '/section:system.webServer/security/authentication/windowsAuthentication' `
        "/+providers.[value='Negotiate']" `
        '/commit:apphost'

    Invoke-AppCmdChecked set config $Location `
        '/section:system.webServer/security/authentication/windowsAuthentication' `
        "/+providers.[value='NTLM']" `
        '/commit:apphost'
}

function Grant-RunAsRuntimeAccess {
    if ([string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) { return }

    Write-Step 'Granting runtime folder access to configured run-as account'

    New-Item -ItemType Directory -Path $RuntimeRoot -Force | Out-Null
    Invoke-NativeChecked icacls $RuntimeRoot '/grant' ('{0}:(OI)(CI)M' -f $script:resolvedRunAsUser) '/T' '/C' '/Q'
}

function Wait-ForWindowsServiceStatus {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][System.ServiceProcess.ServiceControllerStatus]$Status,
        [int]$TimeoutSeconds = 30
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -eq $Status) {
            return
        }

        Start-Sleep -Seconds 1
    }

    throw "Timed out waiting for service '$Name' to reach status '$Status'."
}

function Stop-WindowsServiceIfInstalled {
    param([Parameter(Mandatory = $true)][string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service -or $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        return
    }

    Write-Host "Stopping service before deployment: $Name"
    Stop-Service -Name $Name -Force -ErrorAction Stop
    Wait-ForWindowsServiceStatus -Name $Name -Status Stopped -TimeoutSeconds 30
}

function Invoke-ScChecked {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string[]]$DisplayArguments = $null
    )

    $scPath = Join-Path $env:windir 'System32\sc.exe'
    if ($null -eq $DisplayArguments) {
        $DisplayArguments = $Arguments
    }

    Invoke-NativeCheckedRedacted -FilePath $scPath -Arguments $Arguments -DisplayArguments $DisplayArguments
}

function Set-WindowsServiceConfiguration {
    param(
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][string]$BinaryPath,
        [Parameter(Mandatory = $true)][string]$DisplayName,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $serviceWmi = Get-WmiObject -Class Win32_Service -Filter ("Name='{0}'" -f $ServiceName)
    if ($null -eq $serviceWmi) {
        throw "Windows service exists in Service Control Manager but could not be loaded through WMI: $ServiceName"
    }

    $runAsPasswordPlain = $null
    if (-not [string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) {
        $runAsPasswordPlain = Get-RunAsPasswordPlainText
    }

    try {
        $serviceAccount = if ([string]::IsNullOrWhiteSpace($script:resolvedRunAsUser)) { $null } else { $script:resolvedRunAsUser }
        $changeResult = $serviceWmi.Change(
            $DisplayName,
            $BinaryPath,
            $null,
            $null,
            'Automatic',
            $false,
            $serviceAccount,
            $runAsPasswordPlain,
            $null,
            $null,
            $null)

        if ($changeResult.ReturnValue -ne 0) {
            throw "Failed to update Windows service '$ServiceName'. Win32_Service.Change returned $($changeResult.ReturnValue)."
        }

        Invoke-ScChecked -Arguments @('description', $ServiceName, $Description)
    }
    finally {
        $runAsPasswordPlain = ''
    }
}

function Ensure-WindowsService {
    param(
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][string]$DisplayName,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [switch]$SkipStart
    )

    if (-not (Test-IsWindowsAdministrator)) {
        throw "Windows service installation requires an elevated PowerShell session. Re-run as Administrator or skip service setup."
    }

    if (-not (Test-Path -LiteralPath $ExecutablePath)) {
        throw "Service executable was not found for '$ServiceName': $ExecutablePath"
    }

    $binaryPath = '"{0}"' -f $ExecutablePath
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

    if ($null -ne $existingService -and $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "Stopping existing service: $ServiceName"
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        Wait-ForWindowsServiceStatus -Name $ServiceName -Status Stopped -TimeoutSeconds 30
    }

    if ($null -eq $existingService) {
        Write-Host "Creating Windows service: $ServiceName"
        if ($null -ne $script:resolvedRunAsCredential) {
            New-Service `
                -Name $ServiceName `
                -BinaryPathName $binaryPath `
                -DisplayName $DisplayName `
                -Description $Description `
                -StartupType Automatic `
                -Credential $script:resolvedRunAsCredential | Out-Null
        }
        else {
            New-Service `
                -Name $ServiceName `
                -BinaryPathName $binaryPath `
                -DisplayName $DisplayName `
                -Description $Description `
                -StartupType Automatic | Out-Null
        }
    }

    Set-WindowsServiceConfiguration `
        -ServiceName $ServiceName `
        -BinaryPath $binaryPath `
        -DisplayName $DisplayName `
        -Description $Description

    if (-not $SkipStart) {
        Write-Host "Starting service: $ServiceName"
        Start-Service -Name $ServiceName -ErrorAction Stop
        Wait-ForWindowsServiceStatus -Name $ServiceName -Status Running -TimeoutSeconds 30
    }
}

function Ensure-RuntimeWindowsServices {
    if ($SkipRuntimeServices) { return }

    Write-Step 'Ensuring OMP runtime Windows services'

    Ensure-WindowsService `
        -ServiceName $script:hostAgentServiceName `
        -DisplayName 'OpenModulePlatform HostAgent' `
        -Description 'Local OMP HostAgent for artifact provisioning.' `
        -ExecutablePath (Join-Path $script:servicesRoot 'HostAgent\OpenModulePlatform.HostAgent.WindowsService.exe') `
        -SkipStart:$SkipStartRuntimeServices

    Ensure-WindowsService `
        -ServiceName $script:workerManagerServiceName `
        -DisplayName 'OpenModulePlatform WorkerManager' `
        -Description 'Local OMP WorkerManager for manager-driven worker plugins.' `
        -ExecutablePath (Join-Path $script:servicesRoot 'WorkerManager\OpenModulePlatform.WorkerManager.WindowsService.exe') `
        -SkipStart:$SkipStartRuntimeServices
}

function Ensure-ExampleWindowsService {
    if ($SkipExampleService) { return }

    Write-Step 'Ensuring example Windows service'

    Ensure-WindowsService `
        -ServiceName $script:exampleServiceName `
        -DisplayName 'OpenModulePlatform Service - ExampleServiceAppModule' `
        -Description 'Example OMP service app installed by scripts/install-local-examples.ps1.' `
        -ExecutablePath (Join-Path $script:servicesRoot "ExampleServiceAppModule\$script:exampleServiceName.exe") `
        -SkipStart:$SkipStartExampleService
}

function Ensure-IisExamples {
    if ($SkipIis) { return }

    if (-not (Test-IsWindowsAdministrator)) {
        throw 'IIS installation requires an elevated PowerShell session. Re-run as Administrator or use -SkipIis.'
    }

    Write-Step 'Ensuring IIS site and example applications'
    Require-AppCmd

    New-Item -ItemType Directory -Path $script:portalPath -Force | Out-Null
    New-Item -ItemType Directory -Path $script:webAppsRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $script:authAppPath -Force | Out-Null

    Ensure-IisAppPool -Name 'OMP_Portal'

    if (-not (Test-IisSite -Name $IisSiteName)) {
        Invoke-AppCmdChecked add site `
            "/name:$IisSiteName" `
            ("/bindings:http/*:{0}:" -f $IisPort) `
            "/physicalPath:$script:portalPath"
    }
    else {
        Invoke-AppCmdChecked set vdir "$IisSiteName/" "/physicalPath:$script:portalPath"
    }

    Invoke-AppCmdChecked set app "$IisSiteName/" '/applicationPool:OMP_Portal'
    Set-IisAuthentication -Location $IisSiteName -AnonymousEnabled $true

    Ensure-IisWebApplication `
        -AppPath 'auth' `
        -PhysicalPath $script:authAppPath `
        -AppPoolName 'OMP_Auth' `
        -AnonymousEnabled $true

    Set-IisAuthentication -Location "$IisSiteName/auth" -AnonymousEnabled $true -WindowsEnabled $true

    $apps = @(
        @{ Path = 'ExampleWebAppModule'; Pool = 'OMP_ExampleWebAppModule'; Anonymous = $true },
        @{ Path = 'ExampleWebAppBlazorModule'; Pool = 'OMP_ExampleWebAppBlazorModule'; Anonymous = $true },
        @{ Path = 'ExampleServiceAppModule'; Pool = 'OMP_ExampleServiceAppModule'; Anonymous = $true },
        @{ Path = 'ExampleWorkerAppModule'; Pool = 'OMP_ExampleWorkerAppModule'; Anonymous = $true },
        @{ Path = 'iFrameWebAppModule'; Pool = 'OMP_iFrameWebAppModule'; Anonymous = $true }
    )

    foreach ($app in $apps) {
        $physicalPath = Join-Path $script:webAppsRoot $app.Path
        Ensure-IisWebApplication `
            -AppPath $app.Path `
            -PhysicalPath $physicalPath `
            -AppPoolName $app.Pool `
            -AnonymousEnabled ([bool]$app.Anonymous)
    }

    if (-not $SkipOpenDocViewer) {
        $odvPhysicalPath = Join-Path $script:webAppsRoot $OpenDocViewerAppPath
        if (Test-Path -LiteralPath $odvPhysicalPath) {
            Ensure-IisWebApplication `
                -AppPath $OpenDocViewerAppPath `
                -PhysicalPath $odvPhysicalPath `
                -AppPoolName 'OMP_OpenDocViewer' `
                -AnonymousEnabled $true
        }
    }

    foreach ($pool in @('OMP_Portal', 'OMP_Auth', 'OMP_ExampleWebAppModule', 'OMP_ExampleWebAppBlazorModule', 'OMP_ExampleServiceAppModule', 'OMP_ExampleWorkerAppModule', 'OMP_iFrameWebAppModule', 'OMP_OpenDocViewer')) {
        if (Test-IisAppPool -Name $pool) {
            Invoke-AppCmdOptional start apppool "/apppool.name:$pool"
        }
    }
}

$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$RuntimeRoot = [System.IO.Path]::GetFullPath($RuntimeRoot)
$script:publishRoot = Join-Path $RuntimeRoot 'Publish\OMP'
$script:webAppsRoot = Join-Path $RuntimeRoot 'WebApps'
$script:portalPath = Join-Path $RuntimeRoot 'Sites\Portal'
$script:authAppPath = Join-Path $RuntimeRoot 'WebApps\auth'
$script:servicesRoot = Join-Path $RuntimeRoot 'Services'

Initialize-RunAsIdentity

try {
    $openDocViewerRoot = Resolve-OpenDocViewerRoot
    $openDocViewerDist = Publish-OpenDocViewer -OpenDocViewerRoot $openDocViewerRoot
    Publish-OpenModulePlatform
    Grant-RunAsRuntimeAccess
    Deploy-PublishedOutputs -OpenDocViewerDistPath $openDocViewerDist
    Write-ExampleRuntimeConfig
    Run-ExampleSql
    Ensure-RunAsDatabaseAccess
    Remove-LegacyAppPoolDatabaseUsers
    Ensure-IisExamples
    Ensure-RuntimeWindowsServices
    Ensure-ExampleWindowsService

    Write-Host ''
    Write-Host 'Local OMP examples are installed.' -ForegroundColor Green
    Write-Host "Portal: http://localhost:$IisPort/"
    Write-Host "OMP Auth: http://localhost:$IisPort/auth/login"
    Write-Host "OpenDocViewer: http://localhost:$IisPort/$OpenDocViewerAppPath/"
    Write-Host "Example WebApp: http://localhost:$IisPort/ExampleWebAppModule/"
    Write-Host "Example Blazor WebApp: http://localhost:$IisPort/ExampleWebAppBlazorModule/"
    Write-Host "Example Service WebApp: http://localhost:$IisPort/ExampleServiceAppModule/"
    Write-Host "Example Worker WebApp: http://localhost:$IisPort/ExampleWorkerAppModule/"
    Write-Host "iFrame WebApp: http://localhost:$IisPort/iFrameWebAppModule/"
    if (-not $SkipExampleService) {
        Write-Host "Example Service: $script:exampleServiceName"
    }
    if (-not $SkipRuntimeServices) {
        Write-Host "HostAgent Service: $script:hostAgentServiceName"
        Write-Host "WorkerManager Service: $script:workerManagerServiceName"
    }
}
finally {
    Clear-RunAsIdentity
}
